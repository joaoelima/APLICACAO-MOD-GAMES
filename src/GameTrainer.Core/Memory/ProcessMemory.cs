using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GameTrainer.Core.Memory;

public readonly record struct MemoryRegionInfo(nint BaseAddress, long Size, uint Type, bool IsWritable);

public sealed class ProcessMemory : IDisposable
{
    private IntPtr _handle;

    public Process? Process { get; private set; }
    public bool IsAttached => _handle != IntPtr.Zero && Process is { HasExited: false };

    public nint MainModuleBase
    {
        get
        {
            EnsureAttached();
            return Process!.MainModule?.BaseAddress
                   ?? throw new InvalidOperationException("Não foi possível obter o módulo principal do jogo.");
        }
    }

    public int MainModuleSize
    {
        get
        {
            EnsureAttached();
            return Process!.MainModule?.ModuleMemorySize
                   ?? throw new InvalidOperationException("Não foi possível obter o tamanho do módulo principal do jogo.");
        }
    }

    public void Attach(Process process)
    {
        Detach();
        var access = NativeMethods.ProcessAccess.QueryInformation |
                     NativeMethods.ProcessAccess.VmOperation |
                     NativeMethods.ProcessAccess.VmRead |
                     NativeMethods.ProcessAccess.VmWrite;

        _handle = NativeMethods.OpenProcess(access, false, process.Id);
        if (_handle == IntPtr.Zero)
            throw new Win32Exception();

        Process = process;
    }

    public byte[] ReadBytes(nint address, int length)
    {
        EnsureAttached();
        var buffer = new byte[length];
        if (!NativeMethods.ReadProcessMemory(_handle, address, buffer, length, out var read) || read.ToInt64() != length)
            throw new Win32Exception();
        return buffer;
    }

    public bool TryReadBytes(nint address, int length, out byte[] buffer)
    {
        buffer = new byte[length];
        if (!IsAttached) return false;
        return NativeMethods.ReadProcessMemory(_handle, address, buffer, length, out var read)
               && read.ToInt64() == length;
    }

    public T Read<T>(nint address) where T : unmanaged
    {
        var size = Marshal.SizeOf<T>();
        var bytes = ReadBytes(address, size);
        return MemoryMarshal.Read<T>(bytes);
    }

    public bool TryRead<T>(nint address, out T value) where T : unmanaged
    {
        value = default;
        var size = Marshal.SizeOf<T>();
        if (!TryReadBytes(address, size, out var bytes)) return false;
        value = MemoryMarshal.Read<T>(bytes);
        return true;
    }

    public nint ReadPointer(nint address) => (nint)Read<long>(address);

    public bool TryReadPointer(nint address, out nint value)
    {
        value = 0;
        if (!TryRead<long>(address, out var raw)) return false;
        value = (nint)raw;
        return IsLikelyPointer(value) && IsReadable(value);
    }

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        EnsureAttached();
        var buffer = bytes.ToArray();
        if (!NativeMethods.WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written) || written.ToInt64() != buffer.Length)
            throw new Win32Exception();
    }

    public void WriteProtectedBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        EnsureAttached();
        var size = (UIntPtr)bytes.Length;
        if (!NativeMethods.VirtualProtectEx(_handle, address, size, NativeMethods.MemoryProtection.ExecuteReadWrite, out var oldProtect))
            throw new Win32Exception();

        try
        {
            WriteBytes(address, bytes);
            NativeMethods.FlushInstructionCache(_handle, address, size);
        }
        finally
        {
            NativeMethods.VirtualProtectEx(_handle, address, size, oldProtect, out _);
        }
    }

    public void Write<T>(nint address, T value) where T : unmanaged
    {
        var buffer = new byte[Marshal.SizeOf<T>()];
        MemoryMarshal.Write(buffer.AsSpan(), in value);
        WriteBytes(address, buffer);
    }

    public nint AllocateExecutable(int size)
    {
        EnsureAttached();
        var result = NativeMethods.VirtualAllocEx(
            _handle,
            IntPtr.Zero,
            (UIntPtr)size,
            NativeMethods.AllocationType.Commit | NativeMethods.AllocationType.Reserve,
            NativeMethods.MemoryProtection.ExecuteReadWrite);
        if (result == IntPtr.Zero)
            throw new Win32Exception();
        return result;
    }

    public void FreeRemote(nint address)
    {
        if (!IsAttached || address == 0) return;
        NativeMethods.VirtualFreeEx(_handle, address, UIntPtr.Zero, NativeMethods.FreeType.Release);
    }

    public IReadOnlyList<MemoryRegionInfo> GetReadableRegions(bool writableOnly = false)
    {
        EnsureAttached();

        const long minimumAddress = 0x10000;
        const long maximumUserAddress = 0x00007FFFFFFF0000;
        var regions = new List<MemoryRegionInfo>();
        var cursor = minimumAddress;
        var mbiSize = (UIntPtr)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (cursor < maximumUserAddress && IsAttached)
        {
            if (NativeMethods.VirtualQueryEx(_handle, (nint)cursor, out var info, mbiSize) == UIntPtr.Zero)
            {
                cursor += 0x1000;
                continue;
            }

            var regionStart = info.BaseAddress.ToInt64();
            var regionSize = (long)info.RegionSize.ToUInt64();
            if (regionSize <= 0)
            {
                cursor += 0x1000;
                continue;
            }

            var regionEnd = regionStart + regionSize;
            var writable = IsWritableRegion(info);

            if (IsReadableRegion(info) && (!writableOnly || writable))
                regions.Add(new MemoryRegionInfo((nint)regionStart, regionSize, info.Type, writable));

            cursor = regionEnd > cursor ? regionEnd : cursor + 0x1000;
        }

        return regions;
    }

    public nint? FindPatternInMainModule(string signature, int chunkSize = 1024 * 1024)
    {
        EnsureAttached();

        var pattern = new AobPattern(signature);
        var moduleStart = MainModuleBase.ToInt64();
        var moduleEnd = moduleStart + MainModuleSize;
        var cursor = moduleStart;
        var mbiSize = (UIntPtr)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>();

        while (cursor < moduleEnd)
        {
            if (NativeMethods.VirtualQueryEx(
                    _handle,
                    (nint)cursor,
                    out var info,
                    mbiSize) == UIntPtr.Zero)
            {
                cursor += 0x1000;
                continue;
            }

            var regionStart = Math.Max(info.BaseAddress.ToInt64(), moduleStart);
            var queriedRegionEnd = info.BaseAddress.ToInt64() + (long)info.RegionSize.ToUInt64();
            var regionEnd = Math.Min(queriedRegionEnd, moduleEnd);

            if (IsReadableRegion(info) && regionEnd > regionStart)
            {
                var overlap = Math.Max(pattern.Bytes.Length - 1, 0);
                var offset = regionStart;

                while (offset < regionEnd)
                {
                    var remaining = regionEnd - offset;
                    var requestedLong = Math.Min(chunkSize + overlap, remaining);
                    if (requestedLong <= 0 || requestedLong > int.MaxValue)
                        break;

                    var requested = (int)requestedLong;
                    if (TryReadBytes((nint)offset, requested, out var bytes))
                    {
                        for (var i = 0; i <= bytes.Length - pattern.Bytes.Length; i++)
                        {
                            if (pattern.IsMatch(bytes, i))
                                return (nint)(offset + i);
                        }
                    }

                    offset += Math.Min(chunkSize, remaining);
                }
            }

            cursor = Math.Max(regionEnd, cursor + 0x1000);
        }

        return null;
    }

    public nint ResolveRipRelative(nint instructionAddress, int displacementOffset, int instructionEndOffset)
    {
        var displacement = Read<int>(instructionAddress + displacementOffset);
        return instructionAddress + instructionEndOffset + displacement;
    }

    public bool IsReadable(nint address, int length = 1)
    {
        if (!IsAttached || address == 0) return false;
        if (NativeMethods.VirtualQueryEx(
                _handle,
                address,
                out var info,
                (UIntPtr)Marshal.SizeOf<NativeMethods.MemoryBasicInformation>()) == UIntPtr.Zero)
            return false;

        if (!IsReadableRegion(info)) return false;

        var regionEnd = info.BaseAddress.ToInt64() + (long)info.RegionSize.ToUInt64();
        return address.ToInt64() + length <= regionEnd;
    }

    public static bool IsLikelyPointer(nint value)
    {
        var address = value.ToInt64();
        return address >= 0x10000 && address <= 0x00007FFFFFFFFFFF;
    }

    private static bool IsReadableRegion(NativeMethods.MemoryBasicInformation info)
    {
        if (info.State != NativeMethods.MemoryState.Commit) return false;
        if ((info.Protect & NativeMethods.MemoryProtection.NoAccess) != 0) return false;
        if ((info.Protect & NativeMethods.MemoryProtection.Guard) != 0) return false;

        var readable = NativeMethods.MemoryProtection.ReadOnly |
                       NativeMethods.MemoryProtection.ReadWrite |
                       NativeMethods.MemoryProtection.WriteCopy |
                       NativeMethods.MemoryProtection.ExecuteRead |
                       NativeMethods.MemoryProtection.ExecuteReadWrite |
                       NativeMethods.MemoryProtection.ExecuteWriteCopy;

        return (info.Protect & readable) != 0;
    }

    private static bool IsWritableRegion(NativeMethods.MemoryBasicInformation info)
    {
        var writable = NativeMethods.MemoryProtection.ReadWrite |
                       NativeMethods.MemoryProtection.WriteCopy |
                       NativeMethods.MemoryProtection.ExecuteReadWrite |
                       NativeMethods.MemoryProtection.ExecuteWriteCopy;
        return (info.Protect & writable) != 0;
    }

    public void Detach()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
        Process = null;
    }

    private void EnsureAttached()
    {
        if (!IsAttached)
            throw new InvalidOperationException("Nenhum processo de jogo está conectado.");
    }

    public void Dispose() => Detach();
}
