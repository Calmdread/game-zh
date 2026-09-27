using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace GameZh;

public sealed class TextStabilizer
{
    public string Pending { get; private set; } = "";
    public DateTime SinceUtc { get; private set; }
    public string Sent { get; set; } = "";

    public bool Observe(string text, DateTime now, bool force = false)
    {
        if (text != Pending)
        {
            Pending = text;
            SinceUtc = now;
            Sent = "";
        }
        if (text.Length == 0 || text == Sent) return false;
        return force || (now - SinceUtc).TotalMilliseconds >= 400;
    }

    public void Reset() { Pending = ""; Sent = ""; SinceUtc = DateTime.MinValue; }
}

public sealed class TranslationCoordinator : IAsyncDisposable
{
    readonly AppSettings settings;
    readonly IOcrEngine ocr;
    readonly ITranslationService translator;
    readonly Func<CancellationToken, Task<CapturedFrame>> capture;
    readonly TextStabilizer stabilizer = new();
    CancellationTokenSource? loopCts;
    CancellationTokenSource? translationCts;
    Task? loopTask;
    Task? translationTask;
    readonly byte[] signatureA = new byte[12000];
    readonly byte[] signatureB = new byte[12000];
    byte[]? previousSignature;
    string previousContext = "";
    string currentText = "";
    DateTime retryAfterUtc = DateTime.MinValue;
    int processing;
    int version;
    bool running;

    public bool IsRunning => running;
    public event Action<string>? StatusChanged;
    public event Action<string>? OriginalChanged;
    public event Action<string>? TranslationChanged;

    public TranslationCoordinator(AppSettings settings, IOcrEngine ocr, ITranslationService translator,
        Func<CancellationToken, Task<CapturedFrame>> capture)
    {
        this.settings = settings; this.ocr = ocr; this.translator = translator; this.capture = capture;
    }

    public void Start()
    {
        if (running) return;
        running = true;
        loopCts = new CancellationTokenSource();
        loopTask = LoopAsync(loopCts.Token);
        StatusChanged?.Invoke("运行中：等待画面变化");
    }

    public async Task StopAsync()
    {
        if (!running) return;
        running = false;
        Interlocked.Increment(ref version);
        CancelTranslation();
        loopCts?.Cancel();
        if (loopTask is not null) { try { await loopTask; } catch (OperationCanceledException) { } }
        if (translationTask is not null)
        {
            try { await translationTask.WaitAsync(TimeSpan.FromSeconds(3)); }
            catch (OperationCanceledException) { }
            catch (TimeoutException) { AppLog.Write("translation_stop_timeout"); }
        }
        loopCts?.Dispose(); loopCts = null;
        StatusChanged?.Invoke("已暂停");
    }

    public void ResetCapture()
    {
        previousSignature = null;
        stabilizer.Reset();
        currentText = "";
        CancelTranslation();
        Interlocked.Increment(ref version);
    }

    async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            await ProcessOnceAsync(false, token);
            await Task.Delay(settings.IntervalMs, token);
        }
    }

    public async Task ProcessOnceAsync(bool force, CancellationToken token = default)
    {
        if (Interlocked.Exchange(ref processing, 1) != 0) return;
        try
        {
            if (settings.CaptureWidth < 10 || settings.CaptureHeight < 10)
            {
                StatusChanged?.Invoke("请先框选识别区域。");
                return;
            }
            var frame = await capture(token);
            byte[] signature = ReferenceEquals(previousSignature, signatureA) ? signatureB : signatureA;
            frame.FillSignature(signature);
            bool changed = CapturedFrame.HasChanged(previousSignature, signature);
            previousSignature = signature;
            if (changed || force)
            {
                var watch = Stopwatch.StartNew();
                string raw = await ocr.RecognizeAsync(frame, settings.OcrLanguage, token);
                currentText = NormalizeOcr(raw);
                OriginalChanged?.Invoke(currentText);
                AppLog.Write("ocr", $"ms={watch.ElapsedMilliseconds} chars={currentText.Length}");
            }
            if (currentText.Length == 0)
            {
                if (stabilizer.Pending.Length > 0)
                {
                    stabilizer.Reset();
                    CancelTranslation();
                    Interlocked.Increment(ref version);
                    TranslationChanged?.Invoke("");
                }
                StatusChanged?.Invoke("未识别到文字；可打开预览检查区域。");
                return;
            }
            DateTime now = DateTime.UtcNow;
            bool textChanged = currentText != stabilizer.Pending;
            if (textChanged)
            {
                CancelTranslation();
                Interlocked.Increment(ref version);
                TranslationChanged?.Invoke("");
            }
            if (stabilizer.Observe(currentText, now, force) && now >= retryAfterUtc)
            {
                stabilizer.Sent = currentText;
                int requestVersion = Interlocked.Increment(ref version);
                CancelTranslation();
                translationCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                translationTask = TranslateAsync(currentText, requestVersion, translationCts);
            }
            else if (textChanged) StatusChanged?.Invoke("文字变化，等待稳定…");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            AppLog.Write("capture_or_ocr_error", ex.GetType().Name);
            StatusChanged?.Invoke("识别失败：" + ex.Message);
        }
        finally { Interlocked.Exchange(ref processing, 0); }
    }

    async Task TranslateAsync(string text, int requestVersion, CancellationTokenSource source)
    {
        CancellationToken token = source.Token;
        var watch = Stopwatch.StartNew();
        StatusChanged?.Invoke("正在翻译…");
        try
        {
            var options = new TranslationOptions(settings.ApiUrl, settings.ApiKey, settings.Model,
                settings.TimeoutSeconds, settings.Temperature, settings.MaxTokens);
            string translation = await translator.TranslateAsync(text, previousContext, options, token);
            if (requestVersion != Volatile.Read(ref version) || token.IsCancellationRequested) return;
            previousContext = text[..Math.Min(text.Length, 200)];
            TranslationChanged?.Invoke(translation);
            StatusChanged?.Invoke($"已翻译 · {watch.ElapsedMilliseconds} ms");
            AppLog.Write("translation_ok", $"ms={watch.ElapsedMilliseconds} chars={text.Length}");
        }
        catch (OperationCanceledException) { }
        catch (TranslationException ex)
        {
            if (requestVersion != Volatile.Read(ref version)) return;
            AppLog.Write("translation_error", ex.Retryable ? "retryable" : "permanent");
            StatusChanged?.Invoke(ex.Message);
            if (ex.Retryable)
            {
                stabilizer.Sent = "";
                retryAfterUtc = DateTime.UtcNow.AddSeconds(15);
            }
        }
        catch (Exception ex)
        {
            if (requestVersion != Volatile.Read(ref version)) return;
            AppLog.Write("translation_error", ex.GetType().Name);
            StatusChanged?.Invoke("翻译失败，请检查服务连接。" + ex.Message);
            stabilizer.Sent = "";
            retryAfterUtc = DateTime.UtcNow.AddSeconds(15);
        }
        finally
        {
            if (ReferenceEquals(translationCts, source)) translationCts = null;
            source.Dispose();
        }
    }

    public static string NormalizeOcr(string input)
    {
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string source in input.Replace("\r", "").Split('\n'))
        {
            string line = Regex.Replace(source, @"\s+", " ").Trim();
            if (line.Length == 0 || !seen.Add(line)) continue;
            lines.Add(line);
        }
        string result = string.Join("\n", lines);
        return result[..Math.Min(result.Length, 1500)];
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        CancelTranslation();
    }

    void CancelTranslation()
    {
        try { translationCts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }
}
