using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using System.Text;

namespace CpuX.Services;

internal sealed record CacheInfo(byte Level, byte Associativity, ushort LineSize, uint Size, uint Type, int Count);
internal sealed record GraphicsAdapter(string Name, string Luid, uint VendorId, uint DeviceId, uint SubsystemId, uint Revision, ulong DedicatedVideo, ulong DedicatedSystem, ulong SharedSystem);
internal sealed record CpuIdentity(string Vendor, string Signature, int Family, int Model, int Stepping, string Instructions, bool Hypervisor);
internal sealed record MemoryState(ulong Total, ulong Available, uint Load);

internal static class NativeHardware
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct AdapterDescription
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint VendorId, DeviceId, SubSystemId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumAdapters(IntPtr self, uint index, out IntPtr adapter);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetDescription(IntPtr self, out AdapterDescription description);

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus status);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLogicalProcessorInformationEx(int relationship, IntPtr buffer, ref uint length);
    [DllImport("kernel32.dll")] internal static extern uint GetActiveProcessorCount(ushort groupNumber);
    [DllImport("dxgi.dll", ExactSpelling = true)] private static extern int CreateDXGIFactory1(ref Guid id, out IntPtr factory);
    [DllImport("dwmapi.dll")] internal static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("user32.dll")] internal static extern IntPtr GetThreadDpiAwarenessContext();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);

    public static MemoryState ReadMemory()
    {
        var status = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
        if (!GlobalMemoryStatusEx(ref status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return new(status.TotalPhysical, status.AvailablePhysical, status.Load);
    }

    public static IReadOnlyList<CacheInfo> ReadCaches()
    {
        uint size = 0;
        GetLogicalProcessorInformationEx(2, IntPtr.Zero, ref size);
        if (size == 0 || size > 16 * 1024 * 1024) throw new InvalidOperationException("Windows 未返回有效缓存拓扑。");
        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            if (!GetLogicalProcessorInformationEx(2, buffer, ref size)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            var caches = new List<CacheInfo>();
            for (var offset = 0; offset + 20 <= size;)
            {
                var p = IntPtr.Add(buffer, offset);
                var recordSize = Marshal.ReadInt32(p, 4);
                if (recordSize < 20 || (long)offset + recordSize > size) throw new InvalidOperationException("缓存拓扑记录长度无效。");
                if (Marshal.ReadInt32(p) == 2)
                    caches.Add(new(Marshal.ReadByte(p, 8), Marshal.ReadByte(p, 9), (ushort)Marshal.ReadInt16(p, 10), (uint)Marshal.ReadInt32(p, 12), (uint)Marshal.ReadInt32(p, 16), 1));
                offset += recordSize;
            }
            return caches.GroupBy(c => (c.Level, c.Associativity, c.LineSize, c.Size, c.Type))
                .Select(g => g.First() with { Count = g.Count() }).OrderBy(c => c.Level).ThenBy(c => c.Type).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static IReadOnlyList<GraphicsAdapter> ReadGraphics()
    {
        var id = new Guid("770aae78-f26f-4dba-a829-253c83d1b387");
        Marshal.ThrowExceptionForHR(CreateDXGIFactory1(ref id, out var factory));
        try
        {
            var enumerate = VTable<EnumAdapters>(factory, 12);
            var adapters = new List<GraphicsAdapter>();
            for (uint i = 0; i < 64; i++)
            {
                var hr = enumerate(factory, i, out var adapter);
                if ((uint)hr == 0x887A0002) break; // DXGI_ERROR_NOT_FOUND
                Marshal.ThrowExceptionForHR(hr);
                try
                {
                    Marshal.ThrowExceptionForHR(VTable<GetDescription>(adapter, 10)(adapter, out var description));
                    if ((description.Flags & 2) != 0) continue; // Microsoft software adapter
                    adapters.Add(new(description.Description.TrimEnd('\0'), $"{(uint)description.LuidHigh:X8}:{description.LuidLow:X8}", description.VendorId, description.DeviceId,
                        description.SubSystemId, description.Revision, description.DedicatedVideoMemory, description.DedicatedSystemMemory, description.SharedSystemMemory));
                }
                finally { Marshal.Release(adapter); }
            }
            return adapters.DistinctBy(a => a.Luid).ToArray();
        }
        finally { Marshal.Release(factory); }
    }

    private static T VTable<T>(IntPtr instance, int slot) where T : Delegate =>
        Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(Marshal.ReadIntPtr(instance), slot * IntPtr.Size));

    public static CpuIdentity? ReadCpuIdentity()
    {
        if (!X86Base.IsSupported) return null;
        var leaf0 = X86Base.CpuId(0, 0);
        var vendorBytes = new byte[12];
        BitConverter.GetBytes(leaf0.Ebx).CopyTo(vendorBytes, 0);
        BitConverter.GetBytes(leaf0.Edx).CopyTo(vendorBytes, 4);
        BitConverter.GetBytes(leaf0.Ecx).CopyTo(vendorBytes, 8);
        if (leaf0.Eax < 1) return null;
        var leaf1 = X86Base.CpuId(1, 0);
        uint signature = (uint)leaf1.Eax;
        int baseFamily = (int)((signature >> 8) & 15), model = (int)((signature >> 4) & 15);
        int family = baseFamily == 15 ? baseFamily + (int)((signature >> 20) & 255) : baseFamily;
        if (baseFamily is 6 or 15) model |= (int)((signature >> 16) & 15) << 4;
        var features = new List<string>();
        void Add(int bits, int bit, string name) { if (((uint)bits & (1u << bit)) != 0) features.Add(name); }
        Add(leaf1.Edx, 23, "MMX"); Add(leaf1.Edx, 25, "SSE"); Add(leaf1.Edx, 26, "SSE2");
        Add(leaf1.Ecx, 0, "SSE3"); Add(leaf1.Ecx, 9, "SSSE3"); Add(leaf1.Ecx, 19, "SSE4.1"); Add(leaf1.Ecx, 20, "SSE4.2");
        Add(leaf1.Ecx, 25, "AES"); Add(leaf1.Ecx, 28, "AVX"); Add(leaf1.Ecx, 12, "FMA"); Add(leaf1.Ecx, 30, "RDRAND");
        if (leaf0.Eax >= 7)
        {
            var leaf7 = X86Base.CpuId(7, 0);
            Add(leaf7.Ebx, 3, "BMI1"); Add(leaf7.Ebx, 5, "AVX2"); Add(leaf7.Ebx, 8, "BMI2");
            Add(leaf7.Ebx, 16, "AVX-512F"); Add(leaf7.Ebx, 18, "RDSEED"); Add(leaf7.Ebx, 29, "SHA");
        }
        var maxExtended = (uint)X86Base.CpuId(unchecked((int)0x80000000), 0).Eax;
        if (maxExtended >= 0x80000001)
        {
            var extended = X86Base.CpuId(unchecked((int)0x80000001), 0);
            Add(extended.Edx, 20, "NX"); Add(extended.Edx, 29, "x86-64"); Add(extended.Ecx, 5, "LZCNT");
        }
        return new(Encoding.ASCII.GetString(vendorBytes), $"0x{signature:X8}", family, model, (int)(signature & 15), string.Join(" · ", features), ((uint)leaf1.Ecx & (1u << 31)) != 0);
    }
}

internal sealed class LiveSampler
{
    private ulong lastIdle, lastKernel, lastUser;
    private bool initialized;
    public double? CpuUsage()
    {
        // GetSystemTimes covers only the calling processor group above 64 logical processors.
        // Do not mislabel that partial value as whole-machine usage.
        if (NativeHardware.GetActiveProcessorCount(0xffff) > 64) return null;
        if (!NativeHardware.GetSystemTimes(out var idle, out var kernel, out var user)) return null;
        double? value = null;
        if (initialized && kernel >= lastKernel && user >= lastUser && idle >= lastIdle)
        {
            var total = (kernel - lastKernel) + (user - lastUser);
            var idleDelta = idle - lastIdle;
            if (total > 0) value = Math.Clamp(100.0 * (1 - (double)idleDelta / total), 0, 100);
        }
        lastIdle = idle; lastKernel = kernel; lastUser = user; initialized = true;
        return value;
    }
}
