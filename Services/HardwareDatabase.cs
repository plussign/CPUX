using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;
using CpuX.Models;

namespace CpuX.Services;

internal sealed record CpuRule(int Family, int Model, int Stepping, int DisplayFamily, int DisplayModel, int Cores, int L2, int L3,
    string Vendor, string Pattern, int PatternWeight, string Codename, string Technology);
internal sealed record GpuSpecification(string Id, string Name, string Vendor, string[] Aliases, string DeviceType,
    Dictionary<string, JsonElement> Values, Dictionary<string, string> Sources, string[] Urls);
internal sealed record GpuMatch(IReadOnlyList<GpuSpecification> Candidates, string Status, bool CanShowSpecifications);
internal sealed record PciIdentity(string Vendor, string Device, string Subsystem);
internal sealed record DatabaseSource(string File, string Url, string Sha256);
internal sealed record DatabaseManifest(int SchemaVersion, string ImportedAt, int CpuRules, int GpuRecords, string GpuVersion, string PciVersion, DatabaseSource[] Sources);

internal sealed class HardwareDatabase
{
    public static HardwareDatabase Instance => shared.Value;
    private static readonly Lazy<HardwareDatabase> shared = new(() => new HardwareDatabase());
    private readonly CpuRule[] cpuRules;
    private readonly GpuSpecification[] gpus;
    private readonly Dictionary<uint, string> pciDevices = [];
    private readonly Dictionary<ushort, string> pciVendors = [];
    private readonly Dictionary<(uint Device, uint Subsystem), string> pciSubsystems = [];
    public DatabaseManifest Manifest { get; }

    private HardwareDatabase()
    {
        cpuRules = ReadJson<CpuRule[]>("cpu-identities.json.gz");
        gpus = ReadJson<GpuSpecification[]>("gpu-specifications.json.gz");
        Manifest = ReadJson<DatabaseManifest>("manifest.json", false);
        if (Manifest.SchemaVersion != 1 || cpuRules.Length != Manifest.CpuRules || gpus.Length != Manifest.GpuRecords)
            throw new InvalidDataException("内置硬件数据库版本或记录数不匹配。");
        using var reader = new StreamReader(OpenResource("pci.ids.gz", true));
        string? line; ushort? vendor = null; ushort? device = null;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (!line.StartsWith('\t'))
            {
                vendor = null; device = null;
                if (line.Length > 6 && ushort.TryParse(line[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                { vendor = id; pciVendors[id] = line[4..].Trim(); }
            }
            else if (vendor.HasValue && line.StartsWith("\t\t"))
            {
                var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                if (device.HasValue && parts.Length == 3 && ushort.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sv)
                    && ushort.TryParse(parts[1], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var sd))
                    pciSubsystems[((uint)vendor.Value << 16 | device.Value, (uint)sd << 16 | sv)] = parts[2];
            }
            else if (vendor.HasValue)
            {
                device = null;
                var value = line.Trim();
                if (value.Length > 6 && ushort.TryParse(value[..4], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id))
                { device = id; pciDevices[(uint)vendor.Value << 16 | id] = value[4..].Trim(); }
            }
        }
    }

    private static Stream OpenResource(string name, bool compressed)
    {
        var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream($"CpuX.Data.{name}") ?? throw new InvalidDataException($"缺少内置数据：{name}");
        return compressed ? new GZipStream(stream, CompressionMode.Decompress) : stream;
    }
    private static T ReadJson<T>(string name, bool compressed = true)
    {
        using var stream = OpenResource(name, compressed);
        return JsonSerializer.Deserialize<T>(stream) ?? throw new InvalidDataException($"无效内置数据：{name}");
    }

    public PciIdentity LookupPci(uint vendor, uint device, uint subsystem = 0)
    {
        var key = vendor << 16 | device;
        return new(pciVendors.GetValueOrDefault((ushort)vendor, "未收录"), pciDevices.GetValueOrDefault(key, "未收录"),
            pciSubsystems.GetValueOrDefault((key, subsystem), "未收录"));
    }

    public IReadOnlyList<CpuRule> MatchCpu(string name, string vendor, uint signature, int cores = 0)
    {
        var family = (int)(signature >> 8 & 15); var model = (int)(signature >> 4 & 15); var stepping = (int)(signature & 15);
        var displayFamily = family == 15 ? family + (int)(signature >> 20 & 255) : family;
        var displayModel = family is 6 or 15 ? model | (int)(signature >> 16 & 15) << 4 : model;
        var matches = new List<(CpuRule Rule, int Score)>();
        foreach (var rule in cpuRules)
        {
            if (rule.Vendor != vendor || rule.Codename.StartsWith("Unknown", StringComparison.OrdinalIgnoreCase)) continue;
            // Do not guess a new CPU from the nearest old signature. All specified identity fields must agree.
            if (rule.Model < 0 && rule.DisplayModel < 0) continue;
            if (rule.Family >= 0 && rule.Family != family || rule.Model >= 0 && rule.Model != model || rule.Stepping >= 0 && rule.Stepping != stepping
                || rule.DisplayFamily >= 0 && rule.DisplayFamily != displayFamily || rule.DisplayModel >= 0 && rule.DisplayModel != displayModel) continue;
            var normalizedPattern = RemoveTrademarks(rule.Pattern);
            if (normalizedPattern.Length > 0 && !MatchCpuPattern(RemoveTrademarks(name), normalizedPattern)) continue;
            var specificity = new[] { rule.Family, rule.Model, rule.Stepping, rule.DisplayFamily, rule.DisplayModel }.Count(n => n >= 0);
            var score = rule.PatternWeight * 20 + normalizedPattern.Count(char.IsLetterOrDigit) + specificity * 2 + (cores > 0 && rule.Cores == cores ? 1 : 0);
            matches.Add((rule, score));
        }
        if (matches.Count == 0) return [];
        var best = matches.Max(m => m.Score);
        return matches.Where(m => m.Score == best).Select(m => m.Rule).DistinctBy(r => (r.Codename, r.Technology)).ToArray();
    }

    private static string RemoveTrademarks(string text) => Regex.Replace(text.Replace("®", "").Replace("™", ""), @"\((?:r|tm)\)", "", RegexOptions.IgnoreCase);
    private static bool MatchCpuPattern(string name, string pattern)
    {
        var regex = new System.Text.StringBuilder();
        for (var i = 0; i < pattern.Length; i++)
        {
            if (pattern[i] == '#') regex.Append("[0-9]");
            else if (pattern[i] == '.') regex.Append('.');
            else if (pattern[i] == '[' && pattern.IndexOf(']', i + 1) is var end && end > i)
            { regex.Append('[').Append(Regex.Escape(pattern[(i + 1)..end])).Append(']'); i = end; }
            else regex.Append(Regex.Escape(pattern[i].ToString()));
        }
        return Regex.IsMatch(name, regex.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));
    }

    public HardwareCard CpuReference(string name, string vendor, uint signature, int cores = 0)
    {
        var matches = MatchCpu(name, vendor, signature, cores);
        var fields = new List<HardwareField> { new("本机型号", name, "Windows / CPUID 品牌字符串"), new("匹配依据", $"{vendor} · 签名 0x{signature:X8} · 品牌模式", "CPU 识别条件") };
        if (matches.Count == 0) fields.Add(new("匹配状态", "数据库未收录此签名和品牌组合", "libcpuid 识别表；不会使用相近型号猜测"));
        else
        {
            fields.Add(new("匹配状态", matches.Count == 1 ? "识别到一个代号/制程组合" : $"{matches.Count} 个候选；未唯一确定", "libcpuid；代号不是 SKU 完整规格"));
            fields.Add(new("核心代号", string.Join(" / ", matches.Select(r => r.Codename).Distinct()), "数据库资料 · libcpuid"));
            var technologies = matches.Select(r => r.Technology).Distinct().ToArray();
            fields.Add(new("制程节点", technologies.Length == 1 && technologies[0].Length > 0 ? technologies[0] : "候选不同或未收录", "数据库资料 · 保留 Intel 7 / TSMC N4 等原始节点名称"));
        }
        return new($"CPU 参考资料 · {name}", $"离线识别表 · 快照 {Manifest.ImportedAt}", fields) { Kind = "reference" };
    }

    internal static string NormalizeGpu(string name)
    {
        name = RemoveTrademarks(name).ToUpperInvariant();
        name = Regex.Replace(name, @"^(?:NVIDIA|AMD|ATI|INTEL)(?:\s+CORPORATION)?\s+", "");
        return Regex.Replace(name, @"[^A-Z0-9]+", " ").Trim();
    }
    private static string WithoutGpuVariant(string name)
    {
        name = Regex.Replace(name, @" (?:AD|GA|TU|GP|GM|GF|GK|GB)\d{3}(?: [A-Z0-9]+)*$", "");
        return Regex.Replace(name, @" \d+ ?(?:GB|MB)$", "");
    }

    public GpuMatch MatchGpu(string name, uint vendorId, uint deviceId, ulong dedicatedVideoBytes)
    {
        var vendor = vendorId switch { 0x10de => "nvidia", 0x1002 => "amd", 0x8086 => "intel", 0x102b => "matrox", _ => "" };
        if (vendor.Length == 0) return new([], "未知厂商；未自动匹配", false);
        var target = NormalizeGpu(name);
        var pci = LookupPci(vendorId, deviceId).Device;
        var identities = new HashSet<string> { target };
        foreach (Match bracket in Regex.Matches(pci, @"\[([^\]]+)\]")) identities.Add(NormalizeGpu(bracket.Groups[1].Value));
        // Exact names/aliases, with only explicit chip/memory variant suffixes generalized.
        // Mobile, Ti, SUPER, Max-Q and model suffixes are retained.
        bool Fits(GpuSpecification g, HashSet<string> names) => new[] { g.Name }.Concat(g.Aliases)
            .Any(alias => names.Contains(NormalizeGpu(alias)) || names.Contains(WithoutGpuVariant(NormalizeGpu(alias))));
        var byName = gpus.Where(g => g.Vendor == vendor && Fits(g, [target])).ToArray();
        var byPci = gpus.Where(g => g.Vendor == vendor && Fits(g, identities.Where(n => n != target).ToHashSet())).ToArray();
        GpuSpecification[] candidates;
        if (byName.Length > 0 && byPci.Length > 0)
        {
            candidates = byName.IntersectBy(byPci.Select(g => g.Id), g => g.Id).ToArray();
            if (candidates.Length == 0) return new(byName.Concat(byPci).DistinctBy(g => g.Id).ToArray(), "本机名称与 PCI 名称冲突；未采用参考规格", false);
        }
        else if (byName.Length > 0) candidates = byName;
        else if (Regex.IsMatch(target, @"^(?:MICROSOFT BASIC DISPLAY ADAPTER|STANDARD VGA GRAPHICS ADAPTER|BASIC DISPLAY ADAPTER|DISPLAY ADAPTER)$")) candidates = byPci;
        else candidates = [];
        if (candidates.Length == 0) return new([], "数据库未收录此设备名称 / PCI 名称", false);
        if (dedicatedVideoBytes >= 1024UL * 1024 * 1024)
        {
            var actual = dedicatedVideoBytes / (1024.0 * 1024 * 1024);
            var memoryMatches = candidates.Where(g => !g.Values.TryGetValue("memorySize", out var v) || !v.TryGetDouble(out var size)
                || g.DeviceType == "integrated" || Math.Abs(size - actual) <= Math.Max(0.5, size * 0.06)).ToArray();
            if (memoryMatches.Length == 0) return new(candidates, "本机显存与资料不符；未采用参考规格", false);
            candidates = memoryMatches;
        }
        var chipMatches = candidates.Where(g => g.Values.TryGetValue("gpuName", out var chip) && chip.ValueKind == JsonValueKind.String
            && chip.GetString() is { Length: > 0 } text && Regex.IsMatch(pci, $@"(?<![A-Z0-9]){Regex.Escape(text)}(?![A-Z0-9])", RegexOptions.IgnoreCase)).ToArray();
        if (chipMatches.Length > 0) candidates = chipMatches;
        return new(candidates, candidates.Length == 1 ? "厂商、名称 / PCI 标识匹配；参考型号规格" : $"{candidates.Length} 个候选；仅展示共同规格", true);
    }

    private static readonly (string Key, string Label, string Unit)[] gpuFields =
    [
        ("gpuName", "芯片代号", ""), ("architecture", "架构", ""), ("generation", "产品系列", ""), ("releaseDate", "库记录发布日期", ""),
        ("foundry", "代工厂", ""), ("processSize", "制程节点", " nm"), ("transistors", "晶体管", " 十亿"), ("dieSize", "芯片面积", " mm²"),
        ("shaders", "着色单元", ""), ("sms", "SM 数", ""), ("tmus", "纹理单元", ""), ("rops", "光栅单元", ""), ("tensorCores", "Tensor 核心", ""), ("rtCores", "光追核心", ""),
        ("memoryType", "显存类型", ""), ("memorySize", "参考显存容量", " GB（库标称）"), ("memoryBus", "显存位宽", " bit"), ("memoryBandwidth", "参考显存带宽", " GB/s"),
        ("baseClock", "参考基础频率", " MHz"), ("boostClock", "参考 Boost", " MHz"), ("memoryClock", "参考显存时钟", " MHz"),
        ("busInterface", "参考总线规格", ""), ("tdp", "参考功耗指标", " W"), ("suggestedPSU", "库建议电源", " W"), ("powerConnectors", "参考供电接口", ""),
        ("length", "参考卡长", " mm"), ("width", "参考卡厚", " mm"), ("slot", "参考占用槽位", ""), ("displayOutputs", "参考输出接口", ""),
        ("directX", "DirectX（资料）", ""), ("openGL", "OpenGL（资料）", ""), ("vulkan", "Vulkan（资料）", ""), ("openCL", "OpenCL（资料）", ""),
        ("cuda", "CUDA 计算能力", ""), ("shaderModel", "Shader Model", ""), ("warpSize", "线程束大小", ""), ("maxThreadsPerBlock", "每 Block 线程上限", ""),
        ("maxThreadsPerSM", "每 SM 线程上限", ""), ("maxBlocksPerSM", "每 SM Block 上限", ""), ("sharedMemPerSM", "每 SM 共享内存", " B"), ("registersPerSM", "每 SM 寄存器", "")
    ];

    public HardwareCard GpuReference(string name, uint vendorId, uint deviceId, ulong dedicatedVideoBytes, uint subsystemId = 0)
    {
        var match = MatchGpu(name, vendorId, deviceId, dedicatedVideoBytes);
        var pci = LookupPci(vendorId, deviceId, subsystemId);
        var fields = new List<HardwareField> { new("匹配状态", match.Status, "CPUX 离线匹配；不是读取显卡 BIOS"),
            new("PCI 厂商名称", pci.Vendor, $"数据库资料 · pci.ids {Manifest.PciVersion}"),
            new("PCI 设备名称", LookupPci(vendorId, deviceId).Device, $"数据库资料 · pci.ids {Manifest.PciVersion}") };
        if (subsystemId != 0) fields.Add(new("PCI 子系统名称", pci.Subsystem, $"数据库资料 · pci.ids {Manifest.PciVersion}"));
        if (match.Candidates.Count > 0)
        {
            fields.Add(new("参考型号", string.Join(" / ", match.Candidates.Select(c => c.Name)), "离线 GPU 数据库"));
            if (match.CanShowSpecifications)
            {
                foreach (var (key, label, unit) in gpuFields)
                {
                    var values = match.Candidates.Select(c => c.Values.TryGetValue(key, out var value) ? ValueText(value) : "").ToArray();
                    if (values.Any(string.IsNullOrWhiteSpace) || values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 1) continue;
                    var source = string.Join(" / ", match.Candidates.Select(c => c.Sources.GetValueOrDefault(key, "Open GPU DB")).Distinct());
                    fields.Add(new(label, values[0] + unit, $"数据库参考规格 · {source}；不代表本机当前值或板卡定制规格"));
                }
            }
            var urls = match.Candidates.SelectMany(c => c.Urls).Distinct().ToArray();
            if (urls.Length > 0) fields.Add(new("资料来源", string.Join("\n", urls), "上游数据来源；程序不会自动访问链接"));
        }
        return new($"GPU 参考资料 · {name}", $"离线资料 v{Manifest.GpuVersion} · 快照 {Manifest.ImportedAt}", fields) { Kind = "reference" };
    }

    private static string ValueText(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString() ?? "",
        JsonValueKind.Number when value.TryGetDouble(out var number) && double.IsFinite(number) => number.ToString("0.####", CultureInfo.InvariantCulture),
        _ => ""
    };

    public HardwarePage DatabasePage() => new("database", "资料库", "OFFLINE DATABASES", "随程序内置的参考资料。数据可离线查询，来源和快照版本随报告保存。",
    [
        new("数据包", "本地读取，无后台更新", [new("CPU 识别规则", $"{cpuRules.Length:N0} 条", "libcpuid Intel / AMD 识别表"), new("GPU 型号", $"{gpus.Length:N0} 条", "Open GPU DB + RightNow 补充字段"),
            new("PCI 设备条目", $"{pciDevices.Count:N0} 条", "PCI ID Repository"), new("快照日期", Manifest.ImportedAt, "数据包导入日期"),
            new("GPU 目录版本", Manifest.GpuVersion, "Open GPU DB"), new("PCI 库版本", Manifest.PciVersion, "pci.ids")]) { Kind = "reference" },
        new("匹配与读数", "资料说明", [new("CPU", "签名必须符合；再比较品牌模式，候选不唯一时保留候选", "CPUX 匹配策略"),
            new("GPU", "匹配厂商、名称、显存与 PCI 芯片提示；无法区分变体时仅展示共同规格", "CPUX 匹配策略"),
            new("参考规格", "功耗、频率、卡长、供电接口、API 版本等是资料值，不是实时测量或本板卡确认值", "资料边界"),
            new("版本校验", "Data/manifest.json 记录上游 URL 与 SHA-256", "构建溯源")]) { Kind = "reference" },
        new("来源与署名", "第三方资料", [new("CPU", "https://github.com/anrieff/libcpuid", "BSD-2-Clause"), new("GPU", "https://github.com/onepunk/open-gpu-db", "Apache-2.0 / dbgpu MIT"),
            new("GPU 补充", "https://github.com/RightNow-AI/RightNow-GPU-Database", "Apache-2.0 / 数据源 dbgpu、TechPowerUp"),
            new("PCI", "https://pci-ids.ucw.cz/", "BSD-3-Clause"), new("随附说明", "ThirdPartyNotices 目录保留原始许可证和来源说明", "第三方资料署名")]) { Kind = "reference" }
    ]);
}
