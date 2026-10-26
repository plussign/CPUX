using System.Text.Json;
using CpuX.Models;
using CpuX.Services;

namespace CpuX.Tests;

internal static class SelfChecks
{
    public static (bool Passed, string Report) Run()
    {
        var messages = new List<string>();
        var passed = true;
        void Check(string name, Func<bool> test)
        {
            try { var ok = test(); messages.Add($"{(ok ? "PASS" : "FAIL")} {name}"); passed &= ok; }
            catch (Exception e) { passed = false; messages.Add($"FAIL {name}: {e}"); }
        }
        Check("64-bit VRAM formatting (24 GiB)", () => Format.Bytes(24UL * 1024 * 1024 * 1024) == "24 GiB");
        Check("Unavailable is distinct from zero", () => Format.Bytes(0) == "未读取" && Format.Bytes(0, false) == "0 B");
        Check("DDR5 / LPDDR5 firmware types", () => Format.MemoryType(34) == "DDR5" && Format.MemoryType(35) == "LPDDR5" && Format.MemoryType(0) == "未读取");
        Check("Firmware date rejects placeholders", () => Format.WmiDate("20261026120000.000000+480") == "2026-10-26" && Format.WmiDate("**************.******+***") == "**************.******+***");
        Check("Snapshot worker JSON round trip preserves fields and issues", () =>
        {
            var s = new HardwareSnapshot { CpuName = "Test CPU" };
            s.Pages.Add(new("test", "测试", "TEST", "description", [new("card", "source", [new("容量", "24 GiB", "DXGI")])]));
            s.Issues.Add(new("Test", "Unavailable"));
            var copy = JsonSerializer.Deserialize<HardwareSnapshot>(s.ToJson());
            return copy?.Pages[0].Cards[0].Fields[0].Value == "24 GiB" && copy.Issues.Count == 1 && copy.ToReport().Contains("[DXGI]");
        });
        Check("Native physical memory sanity", () => { var m = NativeHardware.ReadMemory(); return m.Total > 0 && m.Available <= m.Total && m.Load <= 100; });
        Check("Native cache topology sanity", () => NativeHardware.ReadCaches().All(c => c.Level > 0 && c.Size > 0 && c.Count > 0 && c.LineSize > 0));
        Check("CPUID family and model decoding", () => { var c = NativeHardware.ReadCpuIdentity(); return c is null || (c.Vendor.Length == 12 && c.Family > 0 && c.Model >= 0 && c.Stepping is >= 0 and <= 15); });
        Check("DXGI enumeration / 64-bit descriptors", () => NativeHardware.ReadGraphics().All(g => !string.IsNullOrWhiteSpace(g.Name) && g.VendorId != 0));
        Check("Executable activates Per-Monitor V2 DPI awareness", () => NativeHardware.AreDpiAwarenessContextsEqual(NativeHardware.GetThreadDpiAwarenessContext(), new IntPtr(-4)));
        Check("Offline package loads complete CPU/GPU catalogs", () => HardwareDatabase.Instance.Manifest.CpuRules == 888 && HardwareDatabase.Instance.Manifest.GpuRecords == 2923);
        Check("Intel CPU signature and brand identify Raptor Lake / Intel 7", () =>
        {
            var matches = HardwareDatabase.Instance.MatchCpu("13th Gen Intel(R) Core(TM) i7-13700K", "GenuineIntel", 0x000B0671, 16);
            return matches.Count == 1 && matches[0].Codename == "Core i7 (Raptor Lake-S)" && matches[0].Technology == "Intel 7";
        });
        Check("AMD CPU signature and brand identify Raphael", () =>
        {
            var matches = HardwareDatabase.Instance.MatchCpu("AMD Ryzen 9 7950X 16-Core Processor", "AuthenticAMD", 0x00A60F12, 16);
            return matches.Count == 1 && matches[0].Codename.Contains("Raphael");
        });
        Check("CPU HX suffix is preserved instead of matching desktop", () =>
        {
            var m = HardwareDatabase.Instance.MatchCpu("Intel(R) Core(TM) i7-13700HX", "GenuineIntel", 0x000B0671);
            return m.Count > 0 && m.All(r => r.Codename.Contains("Lake-HX"));
        });
        Check("Unseen CPU signatures are not guessed", () => HardwareDatabase.Instance.MatchCpu("Intel(R) Core(TM) i7-13700K", "GenuineIntel", 0x00F00F00).Count == 0);
        Check("PCI identity resolves actual GPU chip", () => HardwareDatabase.Instance.LookupPci(0x10de, 0x2705).Device.Contains("AD103"));
        Check("GPU PCI chip resolves AD103 versus AD102 variant", () =>
        {
            var m = HardwareDatabase.Instance.MatchGpu("NVIDIA GeForce RTX 4070 Ti SUPER", 0x10de, 0x2705, 16UL << 30);
            return m.CanShowSpecifications && m.Candidates.Count == 1 && m.Candidates[0].Values["gpuName"].GetString() == "AD103";
        });
        Check("Unknown GPU chip retains variant ambiguity", () => HardwareDatabase.Instance.MatchGpu("NVIDIA GeForce RTX 4070 Ti SUPER", 0x10de, 0xffff, 16UL << 30).Candidates.Count > 1);
        Check("GPU memory conflict prevents using reference specifications", () => !HardwareDatabase.Instance.MatchGpu("NVIDIA GeForce RTX 4070 Ti SUPER", 0x10de, 0x2705, 8UL << 30).CanShowSpecifications);
        Check("Unknown GPU and wrong vendor do not fuzzy match", () => HardwareDatabase.Instance.MatchGpu("RTX 9999", 0x10de, 0xffff, 16UL << 30).Candidates.Count == 0 && HardwareDatabase.Instance.MatchGpu("Radeon RX 7900 XTX", 0x10de, 0xffff, 24UL << 30).Candidates.Count == 0);
        Check("GPU name/PCI conflict does not substitute another card", () => !HardwareDatabase.Instance.MatchGpu("GeForce RTX 4090", 0x10de, 0x2705, 24UL << 30).CanShowSpecifications);
        Check("Generic Microsoft adapter can use exact PCI identity", () => HardwareDatabase.Instance.MatchGpu("Microsoft Basic Display Adapter", 0x10de, 0x2705, 16UL << 30).Candidates.Count == 1);
        Check("Ambiguous GPU hides chip-specific values", () => !HardwareDatabase.Instance.GpuReference("GeForce RTX 4070 Ti SUPER", 0x10de, 0xffff, 16UL << 30).Fields.Any(f => f.Label == "芯片代号"));
        Check("Reference labels survive report JSON round trip", () =>
        {
            var s = new HardwareSnapshot();
            s.Pages.Add(new("test", "测试", "TEST", "", [HardwareDatabase.Instance.GpuReference("GeForce RTX 4070 Ti SUPER", 0x10de, 0x2705, 16UL << 30)]));
            var copy = JsonSerializer.Deserialize<HardwareSnapshot>(s.ToJson());
            return copy?.Pages[0].Cards[0].Kind == "reference" && copy.ToReport().Contains("[参考资料]");
        });
        return (passed, string.Join(Environment.NewLine, messages));
    }
}
