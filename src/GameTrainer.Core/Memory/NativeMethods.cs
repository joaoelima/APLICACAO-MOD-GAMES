using System.Runtime.InteropServices;

namespace GameTrainer.Core.Memory;

internal static class NativeMethods
{
    [Flags]
    internal enum ProcessAccess : uint
    {
        VmOperation = 0x0008,
        VmRead = 0x0010,
        VmWrite = 0x0020,
        QueryInformation = 0x0400
    }

    [Flags]
    internal enum MemoryProtection : uint
    {
        NoAccess = 0x01,
        ReadOnly = 0x02,
        ReadWrite = 0x04,
        WriteCopy = 0x08,
        Execute = 0x10,
        ExecuteRead = 0x20,
        ExecuteReadWrite = 0x40,
        ExecuteWriteCopy = 0x80,
        Guard = 0x100
    }

    internal enum MemoryState : uint
    {
        Commit = 0x1000,
        Reserve = 0x2000,
        Free = 0x10000
    }

    [Flags]
    internal enum AllocationType : uint
    {
        Commit = 0x1000,
        Reserve = 0x2000
    }

    internal enum FreeType : uint
    {
        Release = 0x8000
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public MemoryProtection AllocationProtect;
        public UIntPtr RegionSize;
        public MemoryState State;
        public MemoryProtection Protect;
        public uint Type;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr OpenProcess(ProcessAccess access, bool inheritHandle, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ReadProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesRead);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool WriteProcessMemory(
        IntPtr processHandle,
        IntPtr baseAddress,
        byte[] buffer,
        int size,
        out IntPtr bytesWritten);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern UIntPtr VirtualQueryEx(
        IntPtr processHandle,
        IntPtr address,
        out MemoryBasicInformation buffer,
        UIntPtr length);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr VirtualAllocEx(
        IntPtr processHandle,
        IntPtr address,
        UIntPtr size,
        AllocationType allocationType,
        MemoryProtection protection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualFreeEx(
        IntPtr processHandle,
        IntPtr address,
        UIntPtr size,
        FreeType freeType);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool VirtualProtectEx(
        IntPtr processHandle,
        IntPtr address,
        UIntPtr size,
        MemoryProtection newProtection,
        out MemoryProtection oldProtection);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FlushInstructionCache(
        IntPtr processHandle,
        IntPtr baseAddress,
        UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(IntPtr handle);
}
