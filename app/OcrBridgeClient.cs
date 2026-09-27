using System.Diagnostics;
using System.IO;
using System.Text;

namespace GameZh;

public interface IOcrEngine
{
    Task<string> RecognizeAsync(CapturedFrame frame, string language, CancellationToken cancellationToken);
}

public sealed class OcrBridgeClient : IOcrEngine, IAsyncDisposable
{
    readonly SemaphoreSlim gate = new(1, 1);
    Process? process;
    string BridgePath => Path.Combine(AppContext.BaseDirectory, "OcrBridge.exe");

    public async Task<string[]> LanguagesAsync(CancellationToken token)
    {
        if (!File.Exists(BridgePath)) throw new FileNotFoundException("缺少 OcrBridge.exe，请重新解压完整便携包。", BridgePath);
        using var probe = Process.Start(StartInfo("--languages")) ?? throw new InvalidOperationException("无法启动 OCR 组件");
        string result = await probe.StandardOutput.ReadToEndAsync(token);
        await probe.WaitForExitAsync(token);
        if (probe.ExitCode != 0) throw new InvalidOperationException("Windows OCR 组件不可用。请检查系统语言组件。");
        return result.Trim().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public async Task<string> RecognizeAsync(CapturedFrame frame, string language, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var token = timeout.Token;
            EnsureStarted();
            try
            {
                await process!.StandardInput.WriteLineAsync($"{frame.Width}|{frame.Height}|{language}".AsMemory(), token);
                await process.StandardInput.WriteLineAsync(Convert.ToBase64String(frame.Pixels).AsMemory(), token);
                await process.StandardInput.FlushAsync(token);
                string? response = await process.StandardOutput.ReadLineAsync(token);
                if (response is null) throw new IOException("OCR 进程意外退出");
                if (response.StartsWith("OK:")) return Encoding.UTF8.GetString(Convert.FromBase64String(response[3..]));
                if (response.StartsWith("ERR:")) throw new InvalidOperationException(Encoding.UTF8.GetString(Convert.FromBase64String(response[4..])));
                throw new IOException("OCR 响应格式错误");
            }
            catch
            {
                StopProcess();
                throw;
            }
        }
        finally { gate.Release(); }
    }

    ProcessStartInfo StartInfo(string argument)
    {
        var info = new ProcessStartInfo(BridgePath, argument)
        {
            UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true,
            StandardInputEncoding = Encoding.UTF8, StandardOutputEncoding = Encoding.UTF8
        };
        return info;
    }

    void EnsureStarted()
    {
        if (process is { HasExited: false }) return;
        if (!File.Exists(BridgePath)) throw new FileNotFoundException("缺少 OcrBridge.exe，请重新解压完整便携包。", BridgePath);
        process = Process.Start(StartInfo("--bridge")) ?? throw new InvalidOperationException("无法启动 OCR 组件");
    }

    void StopProcess()
    {
        try { if (process is { HasExited: false }) process.Kill(true); } catch { }
        process?.Dispose();
        process = null;
    }

    public ValueTask DisposeAsync()
    {
        StopProcess();
        gate.Dispose();
        return ValueTask.CompletedTask;
    }
}
