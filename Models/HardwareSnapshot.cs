using System.Text;
using System.Text.Json;

namespace CpuX.Models;

public sealed record HardwareField(string Label, string Value, string Source);
public sealed record HardwareCard(string Title, string Subtitle, IReadOnlyList<HardwareField> Fields)
{
    public string Kind { get; init; } = "local";
    public string KindLabel => Kind == "reference" ? "参考资料" : "本机读取";
}
public sealed record HardwarePage(string Id, string Label, string English, string Description, IReadOnlyList<HardwareCard> Cards);
public sealed record CollectionIssue(string Source, string Message);

public sealed class HardwareSnapshot
{
    public DateTimeOffset CapturedAt { get; init; } = DateTimeOffset.Now;
    public List<HardwarePage> Pages { get; init; } = [];
    public List<CollectionIssue> Issues { get; init; } = [];
    public string CpuName { get; set; } = "未读取";
    public string MemorySummary { get; set; } = "未读取";
    public string GpuName { get; set; } = "未读取";

    public string ToJson() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });

    public string ToReport()
    {
        var result = new StringBuilder();
        result.AppendLine("CPUX / 本机硬件报告").AppendLine($"采集时间：{CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        result.AppendLine("本地只读采集；参考资料来自内置离线数据库，不代表本机实时测量。未读取 = 当前接口未提供，不代表硬件不支持。");
        result.AppendLine("报告可能包含硬件序列号、设备标识和本机系统信息，请自行选择分享范围。");
        foreach (var page in Pages)
        {
            result.AppendLine().AppendLine($"[{page.Label}]");
            foreach (var card in page.Cards)
            {
                result.AppendLine($"  [{card.KindLabel}] {card.Title} · {card.Subtitle}");
                foreach (var field in card.Fields) result.AppendLine($"    {field.Label}：{field.Value}  [{field.Source}]");
            }
        }
        if (Issues.Count > 0)
        {
            result.AppendLine().AppendLine("[采集提示]");
            foreach (var issue in Issues) result.AppendLine($"  {issue.Source}：{issue.Message}");
        }
        return result.ToString();
    }
}
