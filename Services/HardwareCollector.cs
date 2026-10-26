using Microsoft.Win32;
using CpuX.Models;

namespace CpuX.Services;

internal static class HardwareCollector
{
    public static HardwareSnapshot Collect(IProgress<string>? progress = null)
    {
        var snapshot = new HardwareSnapshot();
        HardwareDatabase? database = null;
        try { database = HardwareDatabase.Instance; }
        catch (Exception e) { snapshot.Issues.Add(new("离线资料库", $"资料读取失败；本机采集继续：{e.Message}")); }
        using var wmi = new WmiReader(snapshot.Issues);
        void Step(string message) => progress?.Report(message);
        HardwareField F(string label, string? value, string source) => new(label, string.IsNullOrWhiteSpace(value) ? "未读取" : value, source);
        HardwareCard Card(string title, string subtitle, params HardwareField[] fields) => new(title, subtitle, fields);
        void Page(string id, string title, string english, string description, List<HardwareCard> cards) => snapshot.Pages.Add(new(id, title, english, description, cards));

        Step("正在读取处理器与缓存…");
        var cpus = wmi.Read("Win32_Processor", "Name", "Manufacturer", "SocketDesignation", "NumberOfCores", "NumberOfEnabledCore", "NumberOfLogicalProcessors", "MaxClockSpeed", "ProcessorId", "Architecture", "AddressWidth", "VirtualizationFirmwareEnabled", "VMMonitorModeExtensions", "SecondLevelAddressTranslationExtensions");
        CpuIdentity? identity = null;
        IReadOnlyList<CacheInfo> caches = [];
        try { identity = NativeHardware.ReadCpuIdentity(); } catch (Exception e) { snapshot.Issues.Add(new("CPUID", e.Message)); }
        try { caches = NativeHardware.ReadCaches(); } catch (Exception e) { snapshot.Issues.Add(new("Windows 缓存拓扑", e.Message)); }
        snapshot.CpuName = cpus.FirstOrDefault()?.Text("Name") ?? "未读取";
        var cpuCards = new List<HardwareCard>();
        foreach (var cpu in cpus)
            cpuCards.Add(Card(cpu.Text("Name"), $"处理器插槽 · {cpu.Text("SocketDesignation")}",
                F("制造商", cpu.Text("Manufacturer"), "Win32_Processor"), F("插槽标识", cpu.Text("SocketDesignation"), "Win32_Processor / 固件"),
                F("物理核心", Format.Number(cpu.Number("NumberOfCores")), "Win32_Processor"), F("启用核心", Format.Number(cpu.Number("NumberOfEnabledCore")), "Win32_Processor"),
                F("逻辑处理器", Format.Number(cpu.Number("NumberOfLogicalProcessors")), "Win32_Processor"),
                F("标称最高频率", Format.Number(cpu.Number("MaxClockSpeed"), " MHz"), "Win32_Processor / 固件；非实时频率"),
                F("架构 / 地址宽度", $"{Format.Architecture(cpu.Number("Architecture"))} / {Format.Number(cpu.Number("AddressWidth"), " bit")}", "Win32_Processor"),
                F("处理器标识", cpu.Text("ProcessorId"), "Win32_Processor")));
        if (identity is not null)
        {
            cpuCards.Add(Card("CPUID 标识", "当前执行 CPU 的硬件自述",
                F("厂商字符串", identity.Vendor, "CPUID.0"), F("签名", identity.Signature, "CPUID.1:EAX"),
                F("Family / Model", $"{identity.Family} / {identity.Model}", "CPUID；含扩展 Family / Model"), F("Stepping", identity.Stepping.ToString(), "CPUID.1:EAX"),
                F("Hypervisor 位", identity.Hypervisor ? "已置位" : "未置位", "CPUID.1:ECX[31]；未置位不保证无虚拟机")));
            cpuCards.Add(Card("指令集", "硬件声明；实际执行还取决于操作系统支持", F("支持特性", identity.Instructions, "CPUID.1 / 7 / 80000001")));
        }
        if (database is not null)
        {
            foreach (var cpu in cpus)
            {
                var processorId = cpu.Text("ProcessorId");
                var vendor = cpu.Text("Manufacturer") switch { var v when v.Contains("Intel", StringComparison.OrdinalIgnoreCase) => "GenuineIntel", var v when v.Contains("AMD", StringComparison.OrdinalIgnoreCase) => "AuthenticAMD", _ => identity?.Vendor ?? "" };
                if (processorId.Length >= 8 && uint.TryParse(processorId[^8..], System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var signature))
                    cpuCards.Add(database.CpuReference(cpu.Text("Name"), vendor, signature, (int)cpu.Number("NumberOfCores")));
            }
        }
        Page("cpu", "处理器", "PROCESSOR", "本机核心与硬件标识，附离线数据库中的核心代号和制程。标称频率不是实时睿频。", cpuCards);
        var cacheCards = caches.Select(c => Card($"L{c.Level} · {CacheType(c.Type)}", "Windows 处理器拓扑",
            F("单个缓存", Format.Bytes(c.Size), "GetLogicalProcessorInformationEx"), F("实例数量", c.Count.ToString(), "Windows 缓存拓扑；共享缓存仅计一次"),
            F("该类总容量", Format.Bytes((ulong)c.Size * (ulong)c.Count), "单个容量 × 实例数量"), F("缓存行", $"{c.LineSize} B", "Windows 缓存拓扑"),
            F("相联度", c.Associativity == 255 ? "全相联" : c.Associativity == 0 ? "未读取" : $"{c.Associativity} 路", "Windows 缓存拓扑"))).ToList();
        Page("cache", "缓存", "CACHE TOPOLOGY", "按级别、类型和容量分组。共享缓存按 Windows 返回的实例计数。", cacheCards);

        Step("正在读取主板与固件…");
        var boards = wmi.Read("Win32_BaseBoard", "Manufacturer", "Product", "Version", "SerialNumber");
        var bios = wmi.Read("Win32_BIOS", "Manufacturer", "SMBIOSBIOSVersion", "ReleaseDate", "SMBIOSMajorVersion", "SMBIOSMinorVersion");
        var systems = wmi.Read("Win32_ComputerSystem", "Manufacturer", "Model", "SystemType", "HypervisorPresent");
        var boardCards = boards.Select(b => Card("主板", "SMBIOS 基板信息",
            F("制造商", b.Text("Manufacturer"), "Win32_BaseBoard"), F("型号", b.Text("Product"), "Win32_BaseBoard"),
            F("版本", b.Text("Version"), "Win32_BaseBoard"), F("序列号", b.Text("SerialNumber"), "Win32_BaseBoard"))).ToList();
        foreach (var b in bios)
            boardCards.Add(Card("BIOS / UEFI", "固件自述",
                F("制造商", b.Text("Manufacturer"), "Win32_BIOS"), F("版本", b.Text("SMBIOSBIOSVersion"), "Win32_BIOS"),
                F("发布日期", Format.WmiDate(b.Text("ReleaseDate")), "Win32_BIOS"),
                F("SMBIOS 版本", b.Has("SMBIOSMajorVersion") && b.Number("SMBIOSMajorVersion") > 0 ? $"{b.Text("SMBIOSMajorVersion")}.{b.Text("SMBIOSMinorVersion")}" : "未读取", "Win32_BIOS"),
                F("安全启动", SecureBootState(), "注册表 SecureBoot / State；只读")));
        foreach (var s in systems)
            boardCards.Add(Card("整机", "系统制造商信息", F("制造商", s.Text("Manufacturer"), "Win32_ComputerSystem"), F("型号", s.Text("Model"), "Win32_ComputerSystem"), F("系统类型", s.Text("SystemType"), "Win32_ComputerSystem")));
        Page("board", "主板", "MAINBOARD", "来自系统固件的主板和 BIOS 信息，保留制造商提供的原始标识。", boardCards);

        Step("正在读取内存模块…");
        var modules = wmi.Read("Win32_PhysicalMemory", "DeviceLocator", "BankLabel", "Capacity", "Manufacturer", "PartNumber", "SerialNumber", "SMBIOSMemoryType", "Speed", "ConfiguredClockSpeed", "FormFactor", "DataWidth", "TotalWidth", "ConfiguredVoltage", "Attributes");
        var arrays = wmi.Read("Win32_PhysicalMemoryArray", "MemoryDevices", "MaxCapacityEx", "MemoryErrorCorrection", "Use");
        var memoryCards = new List<HardwareCard>();
        var totalInstalled = modules.Aggregate(0UL, (total, m) => total + m.Number("Capacity"));
        snapshot.MemorySummary = Format.Bytes(totalInstalled);
        memoryCards.Add(Card("内存配置", "SMBIOS 内存设备；不是直接 SPD 读取",
            F("已安装容量", Format.Bytes(totalInstalled), "Win32_PhysicalMemory 容量求和"), F("已识别模块", modules.Count.ToString(), "Win32_PhysicalMemory；可能包含板载内存"),
            F("内存类型", JoinKnown(modules.Select(m => Format.MemoryType(m.Number("SMBIOSMemoryType")))), "SMBIOSMemoryType"),
            F("配置传输率", JoinKnown(modules.Select(m => Format.Number(m.Number("ConfiguredClockSpeed"), " MT/s"))), "固件配置速率；DDR 有效传输率，非时钟 MHz")));
        foreach (var array in arrays.Where(a => a.Number("Use") == 3))
            memoryCards.Add(Card("系统内存阵列", "固件声明；槽位可能包含板载设备",
                F("设备位置数", Format.Number(array.Number("MemoryDevices")), "Win32_PhysicalMemoryArray"),
                F("固件声明上限", array.Number("MaxCapacityEx") > ulong.MaxValue / 1024 ? "未读取" : Format.Bytes(array.Number("MaxCapacityEx") * 1024), "MaxCapacityEx (KiB)；非本软件兼容性验证"),
                F("纠错类型", ErrorCorrection(array.Number("MemoryErrorCorrection")), "Win32_PhysicalMemoryArray；不等于 DDR5 片内 ECC")));
        foreach (var m in modules)
            memoryCards.Add(Card(m.Has("DeviceLocator") ? m.Text("DeviceLocator") : $"内存设备 {modules.IndexOf(m) + 1}", $"{Format.Bytes(m.Number("Capacity"))} · {Format.MemoryType(m.Number("SMBIOSMemoryType"))}",
                F("容量", Format.Bytes(m.Number("Capacity")), "Win32_PhysicalMemory"), F("制造商", m.Text("Manufacturer"), "Win32_PhysicalMemory"),
                F("部件号", m.Text("PartNumber"), "Win32_PhysicalMemory"), F("序列号", m.Text("SerialNumber"), "Win32_PhysicalMemory"),
                F("位置 / Bank", $"{m.Text("DeviceLocator")} / {m.Text("BankLabel")}", "SMBIOS"), F("外形", Format.FormFactor(m.Number("FormFactor")), "SMBIOS"),
                F("标称传输率", Format.Number(m.Number("Speed"), " MT/s"), "固件 Speed；非当前频率"), F("配置传输率", Format.Number(m.Number("ConfiguredClockSpeed"), " MT/s"), "固件 ConfiguredClockSpeed；非实时读数"),
                F("数据 / 总位宽", $"{Format.Number(m.Number("DataWidth"))} / {Format.Number(m.Number("TotalWidth"))} bit", "SMBIOS"),
                F("Rank 数", Format.Number(m.Number("Attributes") & 15), "SMBIOS Attributes 低 4 位"),
                F("配置电压", m.Number("ConfiguredVoltage") == 0 ? "未读取" : $"{m.Number("ConfiguredVoltage") / 1000.0:0.###} V", "SMBIOS 配置值；非传感器测量")));
        Page("memory", "内存", "MEMORY MODULES", "按模块查看容量、料号与固件配置。不读取 SPD 时序、XMP/EXPO 或推断通道模式。", memoryCards);

        Step("正在读取图形设备…");
        var videos = wmi.Read("Win32_VideoController", "Name", "PNPDeviceID", "DriverVersion", "DriverDate", "VideoProcessor", "CurrentHorizontalResolution", "CurrentVerticalResolution", "CurrentRefreshRate", "Status");
        var gpuCards = new List<HardwareCard>();
        IReadOnlyList<GraphicsAdapter> adapters = [];
        try { adapters = NativeHardware.ReadGraphics(); } catch (Exception e) { snapshot.Issues.Add(new("DXGI", e.Message)); }
        snapshot.GpuName = adapters.FirstOrDefault()?.Name ?? videos.FirstOrDefault()?.Text("Name") ?? "未读取";
        foreach (var a in adapters)
            gpuCards.Add(Card(a.Name, $"DXGI 适配器 · {a.Luid}",
                F("专用显存", Format.Bytes(a.DedicatedVideo, false), "DXGI_ADAPTER_DESC1；64 位容量"),
                F("专用系统内存", Format.Bytes(a.DedicatedSystem, false), "DXGI；系统内存中的专用区域"),
                F("共享内存上限", Format.Bytes(a.SharedSystem, false), "DXGI；可用上限，非当前占用或物理显存"),
                F("厂商 / 设备 ID", $"{a.VendorId:X4} / {a.DeviceId:X4}", "DXGI PCI 标识；不查型号数据库"), F("修订号", $"0x{a.Revision:X2}", "DXGI"),
                F("子系统 ID", $"0x{a.SubsystemId:X8}", "DXGI PCI 子系统标识"),
                F("适配器 LUID", a.Luid, "DXGI；当前 Windows 会话内唯一标识，可能包含间接显示适配器")));
        foreach (var v in videos)
            gpuCards.Add(Card(v.Text("Name"), "Windows 显示控制器",
                F("驱动版本", v.Text("DriverVersion"), "Win32_VideoController"), F("驱动日期", Format.WmiDate(v.Text("DriverDate")), "Win32_VideoController"),
                F("视频处理器", v.Text("VideoProcessor"), "驱动自述；不查数据库"),
                F("当前输出模式", v.Number("CurrentHorizontalResolution") > 0 && v.Number("CurrentVerticalResolution") > 0 ? $"{v.Number("CurrentHorizontalResolution")} × {v.Number("CurrentVerticalResolution")}" : "未读取", "Win32_VideoController；非全部显示器枚举"),
                F("报告刷新率", v.Number("CurrentRefreshRate") <= 1 ? "未读取" : $"{v.Number("CurrentRefreshRate")} Hz", "Win32_VideoController；可能为整数近似"),
                F("设备标识", v.Text("PNPDeviceID"), "Windows PnP"), F("状态", v.Text("Status"), "Win32_VideoController")));
        if (database is not null)
            foreach (var adapter in adapters.DistinctBy(a => (a.VendorId, a.DeviceId, a.SubsystemId, a.Name)))
                gpuCards.Add(database.GpuReference(adapter.Name, adapter.VendorId, adapter.DeviceId, adapter.DedicatedVideo, adapter.SubsystemId));
        Page("graphics", "显卡", "GRAPHICS", "本机显存和驱动信息附离线参考规格；功耗、频率、接口与卡长等资料值不代表本板卡实测。", gpuCards);

        Step("正在读取磁盘与系统…");
        var disks = wmi.Read("Win32_DiskDrive", "Model", "SerialNumber", "FirmwareRevision", "Size", "InterfaceType", "MediaType", "PNPDeviceID", "Index", "Partitions", "BytesPerSector");
        var diskCards = disks.OrderBy(d => d.Number("Index")).Select(d => Card($"磁盘 {d.Text("Index")} · {d.Text("Model")}", "Windows 物理磁盘设备",
            F("容量", Format.Bytes(d.Number("Size")), "Win32_DiskDrive；二进制单位"), F("固件版本", d.Text("FirmwareRevision"), "Win32_DiskDrive"),
            F("序列号", d.Text("SerialNumber"), "Win32_DiskDrive"), F("系统接口", d.Text("InterfaceType"), "WMI 接口类型；NVMe 也可能报告 SCSI"),
            F("介质描述", d.Text("MediaType"), "Windows 描述；不用于判断 SSD / HDD"), F("分区数", d.Text("Partitions"), "Win32_DiskDrive"),
            F("逻辑扇区", Format.Number(d.Number("BytesPerSector"), " B"), "Win32_DiskDrive"), F("设备标识", d.Text("PNPDeviceID"), "Windows PnP"))).ToList();
        foreach (var drive in wmi.Read("Win32_LogicalDisk", "DeviceID", "DriveType", "VolumeName", "FileSystem", "Size", "FreeSpace").Where(d => d.Number("DriveType") is 2 or 3))
            diskCards.Add(Card($"卷 {drive.Text("DeviceID")}", drive.Has("VolumeName") ? drive.Text("VolumeName") : "本地卷",
                F("文件系统", drive.Text("FileSystem"), "Win32_LogicalDisk"), F("总容量", Format.Bytes(drive.Number("Size")), "Win32_LogicalDisk"),
                F("可用空间", drive.Has("FreeSpace") ? Format.Bytes(drive.Number("FreeSpace"), false) : "未读取", "Win32_LogicalDisk；采集时快照")));
        Page("storage", "存储", "STORAGE", "物理磁盘与本地卷分别展示；容量按 GiB / TiB 计算。", diskCards);

        var os = wmi.Read("Win32_OperatingSystem", "Caption", "Version", "BuildNumber", "OSArchitecture", "InstallDate", "LastBootUpTime").FirstOrDefault();
        var systemCards = new List<HardwareCard>();
        if (os is not null)
            systemCards.Add(Card("操作系统", "Windows 本机信息",
                F("名称", os.Text("Caption"), "Win32_OperatingSystem"), F("版本", os.Text("Version"), "Win32_OperatingSystem"),
                F("Build", os.Text("BuildNumber"), "Win32_OperatingSystem"), F("架构", os.Text("OSArchitecture"), "Win32_OperatingSystem"),
                F("安装日期", Format.WmiDate(os.Text("InstallDate")), "Win32_OperatingSystem；功能更新可能重置"),
                F("最近启动", WmiTimestamp(os.Text("LastBootUpTime")), "Win32_OperatingSystem；快速启动可能延续原会话")));
        if (cpus.Count > 0)
            systemCards.Add(Card("虚拟化", "Windows 报告的能力与状态",
                F("固件虚拟化", Boolean(cpus[0], "VirtualizationFirmwareEnabled"), "Win32_Processor；Hypervisor 活跃时可能不暴露"),
                F("虚拟机监控扩展", Boolean(cpus[0], "VMMonitorModeExtensions"), "Win32_Processor"), F("二级地址转换", Boolean(cpus[0], "SecondLevelAddressTranslationExtensions"), "Win32_Processor"),
                F("Hypervisor 运行", systems.Count > 0 ? Boolean(systems[0], "HypervisorPresent") : "未读取", "Win32_ComputerSystem；也可能由 VBS / 内存完整性启用")));
        systemCards.Add(Card("关于采集", "CPUX 1.1 · 本地只读",
            F("数据来源", "Windows API · WMI · CPUID · SMBIOS", "本机接口"), F("权限", "普通用户；不安装内核驱动", "应用配置"),
            F("数据边界", "含离线参考资料；无跑分、上传或后台联网", "应用功能"), F("空值含义", "未读取表示接口未提供，不代表硬件不支持", "采集策略")));
        Page("system", "系统", "SYSTEM", "系统版本、虚拟化能力与采集边界。", systemCards);
        if (database is not null) snapshot.Pages.Add(database.DatabasePage());

        var overview = new List<HardwareCard>
        {
            Card("处理器", "COMPUTE", F("型号", snapshot.CpuName, "Win32_Processor"),
                F("核心 / 线程", cpus.Count == 0 ? "未读取" : $"{cpus.Sum(c => (long)c.Number("NumberOfCores"))} 核 / {cpus.Sum(c => (long)c.Number("NumberOfLogicalProcessors"))} 线程", "各处理器插槽求和"),
                F("插槽数", cpus.Count == 0 ? "未读取" : cpus.Count.ToString(), "Win32_Processor")),
            Card("内存", "MEMORY", F("已安装", snapshot.MemorySummary, "固件模块容量求和"),
                F("类型", JoinKnown(modules.Select(m => Format.MemoryType(m.Number("SMBIOSMemoryType")))), "SMBIOS"),
                F("设备数", modules.Count.ToString(), "Win32_PhysicalMemory")),
            Card("图形设备", "GRAPHICS", F("首个适配器", snapshot.GpuName, "DXGI / Windows"),
                F("DXGI 适配器", adapters.Count > 0 ? adapters.Count.ToString() : "未读取", "DXGI；按 LUID 去重，可能包含虚拟/间接显示适配器；不等于物理显卡数")),
            Card("平台", "PLATFORM", F("主板", boards.Count > 0 ? $"{boards[0].Text("Manufacturer")} · {boards[0].Text("Product")}" : "未读取", "Win32_BaseBoard"),
                F("系统", os?.Text("Caption"), "Win32_OperatingSystem"), F("物理磁盘", disks.Count.ToString(), "Win32_DiskDrive"))
        };
        snapshot.Pages.Insert(0, new("overview", "概览", "LOCAL HARDWARE", "本机硬件摘要。处理器和显卡页另附带来源的离线参考资料。", overview));
        Step("采集完成");
        return snapshot;
    }

    private static string CacheType(uint type) => type switch { 0 => "统一缓存", 1 => "指令缓存", 2 => "数据缓存", 3 => "Trace 缓存", _ => $"类型 {type}" };
    private static string Boolean(WmiRow row, string key) => row.Text(key).ToLowerInvariant() switch { "true" or "-1" or "1" => "是", "false" or "0" => "否", _ => "未读取" };
    private static string JoinKnown(IEnumerable<string> values) { var list = values.Where(x => x != "未读取").Distinct().ToArray(); return list.Length == 0 ? "未读取" : string.Join(" / ", list); }
    private static string ErrorCorrection(ulong value) => value switch { 3 => "无", 4 => "Parity", 5 => "单比特 ECC", 6 => "多比特 ECC", 7 => "CRC", _ => "未读取" };
    private static string SecureBootState()
    {
        try { return Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\SecureBoot\State", "UEFISecureBootEnabled", null) is int value ? value == 1 ? "已启用" : "未启用" : "未读取"; }
        catch { return "未读取"; }
    }
    private static string WmiTimestamp(string value)
    {
        try
        {
            if (value.Length < 25) return Format.WmiDate(value);
            var local = DateTime.ParseExact(value[..14], "yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
            var minutes = int.Parse(value[22..25], System.Globalization.CultureInfo.InvariantCulture) * (value[21] == '-' ? -1 : 1);
            return new DateTimeOffset(local, TimeSpan.FromMinutes(minutes)).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
        }
        catch { return Format.WmiDate(value); }
    }
}
