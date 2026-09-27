using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using GameZh;

static class TestProgram
{
    static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && args[0] == "--soak") return await SoakAsync(int.Parse(args[1]));
            await TestRealOcrAsync();
            await TestOpenAiProtocolAsync();
            await TestCoordinatorAsync();
            Console.WriteLine("全部自动化测试通过");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    static CapturedFrame MakeImage(string text)
    {
        using var bitmap = new Bitmap(900, 170, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.White);
            using var font = new Font("Arial", 52, FontStyle.Bold);
            g.DrawString(text, font, System.Drawing.Brushes.Black, 20, 30);
        }
        var bits = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        byte[] data = new byte[bitmap.Width * bitmap.Height * 4];
        try { for (int y = 0; y < bitmap.Height; y++) Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), data, y * bitmap.Width * 4, bitmap.Width * 4); }
        finally { bitmap.UnlockBits(bits); }
        return new CapturedFrame(bitmap.Width, bitmap.Height, data);
    }

    static async Task TestRealOcrAsync()
    {
        await using var ocr = new OcrBridgeClient();
        string[] languages = await ocr.LanguagesAsync(CancellationToken.None);
        if (!languages.Contains("en-US")) throw new Exception("测试机缺少 en-US OCR");
        var watch = Stopwatch.StartNew();
        string result = await ocr.RecognizeAsync(MakeImage("HELLO WORLD"), "en-US", CancellationToken.None);
        Console.WriteLine($"Windows OCR: {result}；首次桥接识别延迟 {watch.ElapsedMilliseconds} ms");
        if (!result.Contains("HELLO", StringComparison.OrdinalIgnoreCase)) throw new Exception("真实 OCR 识别失败");
    }

    static async Task TestOpenAiProtocolAsync()
    {
        await using var server = new MockServer();
        using var translator = new OpenAiTranslator();
        var options = new TranslationOptions(server.Url, "test-key", "mock-model", 5, 0.2, 256);
        var watch = Stopwatch.StartNew();
        string one = await translator.TranslateAsync("HELLO WORLD", "", options, CancellationToken.None);
        long firstLatencyMs = watch.ElapsedMilliseconds;
        string two = await translator.TranslateAsync("HELLO WORLD", "", options, CancellationToken.None);
        if (one != "你好，世界" || two != one || server.Requests != 1) throw new Exception("真实 HTTP 翻译或缓存失败");
        if (!server.LastBody.Contains("mock-model") || !server.LastBody.Contains("HELLO WORLD") || server.LastAuthorization != "Bearer test-key")
            throw new Exception("OpenAI 兼容请求格式错误");
        server.Status = 401;
        try { await translator.TranslateAsync("OTHER TEXT", "", options, CancellationToken.None); throw new Exception("401 未被识别"); }
        catch (TranslationException ex) when (!ex.Retryable && ex.Message.Contains("API Key")) { }
        if (server.Requests != 2) throw new Exception("认证错误发生了错误重试");
        server.Status = 429;
        try { await translator.TranslateAsync("RATE LIMITED", "", options, CancellationToken.None); throw new Exception("429 未被识别"); }
        catch (TranslationException ex) when (ex.Retryable && ex.Message.Contains("限流")) { }
        if (server.Requests != 5) throw new Exception("429 重试次数不为有限的 3 次");
        int closedPort;
        var portProbe = new TcpListener(IPAddress.Loopback, 0);
        portProbe.Start();
        closedPort = ((IPEndPoint)portProbe.LocalEndpoint).Port;
        portProbe.Stop();
        var offline = options with { Url = $"http://127.0.0.1:{closedPort}/v1/chat/completions" };
        try { await translator.TranslateAsync("OFFLINE", "", offline, CancellationToken.None); throw new Exception("连接失败未被识别"); }
        catch (TranslationException ex) when (ex.Retryable && ex.Message.Contains("无法连接")) { }
        Console.WriteLine($"OpenAI 兼容 HTTP、缓存、401 不重试、429 有限重试、断线提示：通过；本地首请求延迟 {firstLatencyMs} ms");
    }

    static async Task TestCoordinatorAsync()
    {
        var settings = ConfigStore.Defaults();
        settings.Model = "fake";
        var frame = MakeImage("HELLO WORLD");
        var ocr = new FakeOcr("FIRST", "FIRST", "SECOND");
        var translation = new FakeTranslator();
        int captures = 0;
        await using var coordinator = new TranslationCoordinator(settings, ocr, translation, _ =>
        {
            captures++;
            return Task.FromResult(frame);
        });
        string latest = "";
        coordinator.TranslationChanged += t => { if (t.Length > 0) latest = t; };
        await coordinator.ProcessOnceAsync(true);
        await coordinator.ProcessOnceAsync(true);
        await Task.Delay(100);
        if (translation.Calls != 1) throw new Exception("重复画面触发了重复翻译");
        await coordinator.ProcessOnceAsync(true);
        await Task.Delay(850);
        if (latest != "NEW" || translation.Calls != 2) throw new Exception("旧译文覆盖新译文或请求次数异常：" + latest);
        coordinator.Start();
        await Task.Delay(500);
        await coordinator.StopAsync();
        int pausedCaptures = captures;
        await Task.Delay(600);
        if (captures != pausedCaptures) throw new Exception("暂停后仍在轮询");
        coordinator.Start();
        await Task.Delay(500);
        await coordinator.StopAsync();
        if (captures <= pausedCaptures) throw new Exception("恢复后未开始轮询");
        Console.WriteLine("去重、旧结果隔离、暂停恢复：通过");
    }

    static async Task<int> SoakAsync(int minutes)
    {
        var settings = ConfigStore.Defaults();
        settings.IntervalMs = 400;
        settings.Model = "fake";
        var frame = MakeImage("HELLO WORLD");
        var fakeOcr = new FakeOcr("HELLO");
        var fakeTranslator = new FakeTranslator();
        int captures = 0;
        await using var coordinator = new TranslationCoordinator(settings, fakeOcr, fakeTranslator, _ =>
        {
            captures++;
            return Task.FromResult(frame);
        });
        coordinator.Start();
        var process = Process.GetCurrentProcess();
        DateTime until = DateTime.UtcNow.AddMinutes(minutes);
        Console.WriteLine("环境：" + Environment.OSVersion + "; .NET " + Environment.Version + "; CPU=" + Environment.ProcessorCount);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(TimeSpan.FromMinutes(1));
            process.Refresh();
            Console.WriteLine($"{DateTime.Now:O} running cpu_ms={process.TotalProcessorTime.TotalMilliseconds:0} private_mb={process.PrivateMemorySize64 / 1048576.0:0.0} managed_mb={GC.GetTotalMemory(false) / 1048576.0:0.0} captures={captures} ocr={fakeOcr.Calls} translate={fakeTranslator.Calls}");
        }
        await coordinator.StopAsync();
        int pausedCaptures = captures;
        await Task.Delay(1000);
        process.Refresh();
        Console.WriteLine($"paused cpu_ms={process.TotalProcessorTime.TotalMilliseconds:0} private_mb={process.PrivateMemorySize64 / 1048576.0:0.0} captures={captures} stopped_polling={captures == pausedCaptures}");
        return captures == pausedCaptures ? 0 : 1;
    }

    sealed class FakeOcr(params string[] values) : IOcrEngine
    {
        int index;
        public int Calls { get; private set; }
        public Task<string> RecognizeAsync(CapturedFrame _, string language, CancellationToken token)
        {
            Calls++;
            return Task.FromResult(values[Math.Min(index++, values.Length - 1)]);
        }
    }

    sealed class FakeTranslator : ITranslationService
    {
        public int Calls { get; private set; }
        public async Task<string> TranslateAsync(string text, string previous, TranslationOptions options, CancellationToken token)
        {
            Calls++;
            await Task.Delay(text == "FIRST" ? 650 : 50); // Intentionally ignores cancellation to test version guard.
            return text == "FIRST" ? "OLD" : "NEW";
        }
    }

    sealed class MockServer : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task loop;
        public int Requests => Volatile.Read(ref requests);
        public int Status { get; set; } = 200;
        public string LastBody { get; private set; } = "";
        public string LastAuthorization { get; private set; } = "";
        public string Url { get; }
        public MockServer()
        {
            listener.Start();
            Url = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/v1/chat/completions";
            loop = LoopAsync();
        }
        async Task LoopAsync()
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await listener.AcceptTcpClientAsync(stop.Token); }
                catch (OperationCanceledException) { break; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var stream = client.GetStream())
                    {
                        var headerBytes = new List<byte>();
                        while (headerBytes.Count < 16384)
                        {
                            int next = stream.ReadByte();
                            if (next < 0) throw new EndOfStreamException();
                            headerBytes.Add((byte)next);
                            int n = headerBytes.Count;
                            if (n >= 4 && headerBytes[n - 4] == 13 && headerBytes[n - 3] == 10 && headerBytes[n - 2] == 13 && headerBytes[n - 1] == 10) break;
                        }
                        int length = 0;
                        foreach (string line in Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n"))
                        {
                            if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line[15..].Trim());
                            if (line.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) LastAuthorization = line[14..].Trim();
                        }
                        byte[] requestBody = new byte[length];
                        await stream.ReadExactlyAsync(requestBody);
                        LastBody = Encoding.UTF8.GetString(requestBody);
                        Interlocked.Increment(ref requests);
                        byte[] body = Encoding.UTF8.GetBytes(Status == 200 ? "{\"choices\":[{\"message\":{\"content\":\"你好，世界\"}}]}" : "{\"error\":\"unauthorized\"}");
                        byte[] headers = Encoding.ASCII.GetBytes($"HTTP/1.1 {Status} {(Status == 200 ? "OK" : "Unauthorized")}\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n");
                        await stream.WriteAsync(headers);
                        await stream.WriteAsync(body);
                    }
                });
            }
        }
        int requests;
        public async ValueTask DisposeAsync()
        {
            stop.Cancel(); listener.Stop();
            try { await loop; } catch { }
            stop.Dispose();
        }
    }
}
