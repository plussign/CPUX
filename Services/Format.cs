using System.Globalization;

namespace CpuX.Services;

internal static class Format
{
    public static string Bytes(ulong bytes, bool zeroIsUnknown = true)
    {
        if (bytes == 0 && zeroIsUnknown) return "未读取";
        var units = new[] { "B", "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }
    public static string Number(ulong value, string suffix = "") => value == 0 ? "未读取" : $"{value:N0}{suffix}";
    public static string WmiDate(string value)
    {
        if (value.Length >= 8 && DateTime.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date.ToString("yyyy-MM-dd");
        return value;
    }
    public static string MemoryType(ulong type) => type switch
    {
        18 => "DDR", 19 => "DDR2", 20 => "DDR2 FB-DIMM", 24 => "DDR3", 26 => "DDR4", 27 => "LPDDR", 28 => "LPDDR2", 29 => "LPDDR3", 30 => "LPDDR4", 34 => "DDR5", 35 => "LPDDR5", 36 => "HBM3", _ => type <= 2 ? "未读取" : $"SMBIOS 类型 {type}"
    };
    public static string FormFactor(ulong type) => type switch { 8 => "DIMM", 12 => "SO-DIMM", 21 => "BGA", 22 => "FPBGA", 23 => "LGA", _ => type == 0 ? "未读取" : $"SMBIOS 外形 {type}" };
    public static string Architecture(ulong type) => type switch { 0 => "x86", 5 => "ARM", 9 => "x64", 12 => "ARM64", _ => $"架构代码 {type}" };
}
