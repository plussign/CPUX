using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using CpuX.Models;
using CpuX.Services;
using Microsoft.Win32;

namespace CpuX;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly CancellationTokenSource lifetime = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly LiveSampler sampler = new();
    private HardwareSnapshot? snapshot;
    private HardwarePage? selectedPage;
    private bool busy;
    private bool closing;
    private double cardWidth = 400;
    private string status = "准备读取本机硬件…", notice = "", cpuUsageText = "—", memoryUsageText = "—";
    private double cpuUsageValue, memoryUsageValue;

    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<HardwarePage> Pages { get; } = [];
    public HardwarePage? SelectedPage
    {
        get => selectedPage;
        set { selectedPage = value; Changed(); Changed(nameof(DisplayCards)); Changed(nameof(EmptyText)); }
    }
    public IReadOnlyList<HardwareCard> DisplayCards => SelectedPage?.Cards ?? [];
    public bool CanRefresh => !busy;
    public bool HasSnapshot => snapshot is not null;
    public bool HasNotice => !string.IsNullOrEmpty(Notice);
    public string Notice { get => notice; private set { notice = value; Changed(); Changed(nameof(HasNotice)); } }
    public string Status { get => status; private set { status = value; Changed(); } }
    public string CpuUsageText { get => cpuUsageText; private set { cpuUsageText = value; Changed(); } }
    public string MemoryUsageText { get => memoryUsageText; private set { memoryUsageText = value; Changed(); } }
    public double CpuUsageValue { get => cpuUsageValue; private set { cpuUsageValue = value; Changed(); } }
    public double MemoryUsageValue { get => memoryUsageValue; private set { memoryUsageValue = value; Changed(); } }
    public double CardWidth { get => cardWidth; private set { cardWidth = value; Changed(); } }
    public string EmptyText => busy && snapshot is null ? "正在读取 Windows 本机硬件信息，请稍候…" : DisplayCards.Count == 0 ? "当前接口未返回设备信息。可查看采集提示，或刷新重试。" : "";

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        var area = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(360, area.Width - 32));
        MinHeight = Math.Min(MinHeight, Math.Max(260, area.Height - 32));
        Width = Math.Min(Width, area.Width - 32);
        Height = Math.Min(Height, area.Height - 32);
        timer.Tick += (_, _) => UpdateLive();
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F5 && CanRefresh) { e.Handled = true; _ = RefreshAsync(); } };
        // Ctrl+C remains available on each selectable field; F5 refreshes the snapshot.
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateLive(); timer.Start();
        UpdateCardWidth();
        await RefreshAsync();
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var dark = 1;
        try { NativeHardware.DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int)); } catch (DllNotFoundException) { }
    }

    private async Task RefreshAsync()
    {
        if (busy || closing) return;
        busy = true; Changed(nameof(CanRefresh)); Changed(nameof(EmptyText));
        Status = "正在采集本机硬件（最长 45 秒）…";
        Notice = "";
        var previousId = SelectedPage?.Id;
        try
        {
            var result = await SnapshotRunner.CollectAsync(lifetime.Token);
            if (closing) return;
            PresentSnapshot(result, previousId);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            Notice = ex.Message;
            Status = snapshot is null ? "采集未完成 · 可刷新重试" : $"刷新失败 · 正显示 {snapshot.CapturedAt:HH:mm:ss} 的快照";
        }
        finally
        {
            busy = false;
            Changed(nameof(CanRefresh)); Changed(nameof(EmptyText));
        }
    }

    internal void PresentSnapshot(HardwareSnapshot result, string? selectedId = null)
    {
        snapshot = result;
        Pages.Clear();
        foreach (var page in result.Pages) Pages.Add(page);
        SelectedPage = Pages.FirstOrDefault(p => p.Id == selectedId) ?? Pages.FirstOrDefault();
        Notice = result.Issues.Count > 0 ? string.Join("\n", result.Issues.Select(i => $"{i.Source}：{i.Message}")) : "";
        Status = $"采集于 {result.CapturedAt:HH:mm:ss} · {result.Pages.Sum(p => p.Cards.Count)} 组信息 · 使用率每秒更新";
        Changed(nameof(HasSnapshot));
    }

    private void UpdateLive()
    {
        try
        {
            var usage = sampler.CpuUsage();
            CpuUsageText = usage.HasValue ? $"{usage:0.0} %" : "—";
            CpuUsageValue = usage ?? 0;
        }
        catch { CpuUsageText = "未读取"; CpuUsageValue = 0; }
        try
        {
            var m = NativeHardware.ReadMemory();
            MemoryUsageText = $"{Format.Bytes(m.Total - m.Available)} / {Format.Bytes(m.Total)}";
            MemoryUsageValue = m.Load;
        }
        catch { MemoryUsageText = "未读取"; MemoryUsageValue = 0; }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot is null) return;
        try { Clipboard.SetText(snapshot.ToReport()); Status = "报告已复制 · 包含硬件序列号，分享前请自行检查"; }
        catch (Exception ex) { Status = $"剪贴板暂不可用：{ex.Message}"; }
    }
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (snapshot is null) return;
        var dialog = new SaveFileDialog
        {
            Title = "导出本机硬件报告", FileName = $"CPUX-{snapshot.CapturedAt:yyyyMMdd-HHmmss}",
            Filter = "文本报告 (*.txt)|*.txt|JSON 数据 (*.json)|*.json", AddExtension = true, DefaultExt = ".txt"
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase) ? snapshot.ToJson() : snapshot.ToReport(), new System.Text.UTF8Encoding(false));
            Status = $"已保存到 {dialog.FileName}";
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "无法保存报告", MessageBoxButton.OK, MessageBoxImage.Error); }
    }
    private void Content_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateCardWidth();
    private void UpdateCardWidth()
    {
        if (ContentScroll is null) return;
        var available = Math.Max(220, ContentScroll.ActualWidth - 30);
        CardWidth = available >= 680 ? Math.Floor((available - 28) / 2) : Math.Max(206, available - 14);
    }
    private void Navigation_SelectionChanged(object sender, SelectionChangedEventArgs e) => ContentScroll?.ScrollToTop();
    private void Window_Closed(object? sender, EventArgs e)
    {
        closing = true; timer.Stop(); lifetime.Cancel();
    }
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
