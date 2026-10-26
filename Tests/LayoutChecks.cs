using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CpuX.Models;

namespace CpuX.Tests;

internal static class LayoutChecks
{
    // Render the real WPF tree at different logical work areas and pixel densities.
    // This catches binding failures / clipping, but does not replace moving between physical monitors.
    public static bool Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var log = new List<string>();
        var passed = true;
        var fixture = new HardwareSnapshot();
        fixture.Pages.Add(new("overview", "概览", "LOCAL HARDWARE", "高 DPI 布局验证 · 长型号、不同窗口宽度和文本换行", [
            new("处理器", "COMPUTE", [new("型号", "13th Gen Intel(R) Core(TM) i7-13700K", "WMI"), new("核心 / 线程", "16 核 / 24 线程", "WMI")]),
            new("内存模块", "MEMORY", [new("制造商", "Long Manufacturer Name / 长制造商名称换行验证", "WMI"), new("配置传输率", "3,600 MT/s", "SMBIOS")]),
            new("图形设备", "GRAPHICS", [new("设备标识", "PCI\\VEN_10DE&DEV_2705&SUBSYS_00000000&REV_A1\\LONG_DEVICE_INSTANCE_IDENTIFIER", "PnP"), new("专用显存", "16 GiB", "DXGI")]),
            new("GPU 参考资料 · 长型号与来源换行验证", "离线资料 · 快照 2026-10-26", [new("参考功耗指标", "285 W", "数据库参考资料，非实测")]) { Kind = "reference" } ]));
        var window = new MainWindow();
        window.PresentSnapshot(fixture);
        try
        {
            var root = (FrameworkElement)window.Content;
            foreach (var (width, height, scale, name) in new[] { (1120, 780, 1.0, "100"), (1120, 700, 1.5, "150"), (960, 510, 2.0, "200"), (640, 340, 3.0, "300"), (560, 360, 1.0, "narrow") })
            {
                try
                {
                    for (var i = 0; i < 3; i++)
                    {
                        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);
                    }
                    var cards = Descendants(root).OfType<Border>().Where(b => b.DataContext is HardwareCard && b.CornerRadius.TopLeft == 9).ToList();
                    var scroll = (ScrollViewer)window.FindName("ContentScroll");
                    var boxes = Descendants(root).OfType<TextBox>().ToArray();
                    var ok = cards.Count == 4 && cards.All(b => b.ActualWidth > 0 && b.ActualWidth <= scroll.ActualWidth) && boxes.All(b => b.ActualWidth > 20 && !double.IsNaN(b.ActualWidth));
                    passed &= ok;
                    log.Add($"{(ok ? "PASS" : "FAIL")} {name}% / {width}x{height} DIP / {cards.Count} cards / card {window.CardWidth:0} / viewport {scroll.ActualWidth:0}");
                    var bitmap = new RenderTargetBitmap((int)(width * scale), (int)(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
                    bitmap.Render(root);
                    var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                    using var file = File.Create(Path.Combine(outputDirectory, $"layout-{name}.png")); encoder.Save(file);
                }
                catch (Exception ex) { passed = false; log.Add($"FAIL {name}: {ex}"); }
            }
        }
        catch (Exception ex) { passed = false; log.Add(ex.ToString()); }
        finally { window.Close(); }
        File.WriteAllLines(Path.Combine(outputDirectory, "layout-check.txt"), log);
        return passed;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in Descendants(child)) yield return descendant;
        }
    }
}
