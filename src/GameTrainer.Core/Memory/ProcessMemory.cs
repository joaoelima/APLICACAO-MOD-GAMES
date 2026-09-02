using System.ComponentModel;
using System.Diagnostics;

namespace GameTrainer.Core.Memory;

public sealed class ProcessMemory : IDisposable
{
    private IntPtr _handle;
    public Process? Process { get; private set; }
    public bool IsAttached => _handle != IntPtr.Zero && Process is { HasExited: false };

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

    public void WriteBytes(nint address, ReadOnlySpan<byte> bytes)
    {
        EnsureAttached();
        var buffer = bytes.ToArray();
        if (!NativeMethods.WriteProcessMemory(_handle, address, buffer, buffer.Length, out var written) || written.ToInt64() != buffer.Length)
            throw new Win32Exception();
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
