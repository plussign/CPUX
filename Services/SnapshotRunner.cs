using System.Diagnostics;
using System.IO;
using System.Text.Json;
using CpuX.Models;

namespace CpuX.Services;

internal static class SnapshotRunner
{
    // Isolate provider calls: even a stuck WMI provider cannot freeze the UI or prevent closing.
    public static async Task<HardwareSnapshot> CollectAsync(CancellationToken cancellationToken)
    {
        var temporaryPath = Path.Combine(Path.GetTempPath(), $"CPUX-{Guid.NewGuid():N}.json");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = AppContext.BaseDirectory };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "CPUX.dll"));
        start.ArgumentList.Add("--collect-internal"); start.ArgumentList.Add(temporaryPath);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(45));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("无法启动本机采集进程。");
        using var registration = timeout.Token.Register(() => KillWorker(process));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(temporaryPath)) throw new InvalidOperationException("Windows 硬件采集未完成，请重试。");
            return JsonSerializer.Deserialize<HardwareSnapshot>(await File.ReadAllTextAsync(temporaryPath, cancellationToken)) ?? throw new InvalidOperationException("硬件快照为空。");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("采集超过 45 秒。Windows WMI 服务可能未响应，请稍后刷新。");
        }
        finally
        {
            KillWorker(process);
            try { File.Delete(temporaryPath); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void KillWorker(Process process)
    {
        try { if (!process.HasExited) process.Kill(true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
