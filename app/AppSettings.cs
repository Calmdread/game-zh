using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameZh;

public sealed class AppSettings
{
    public int CaptureX { get; set; }
    public int CaptureY { get; set; }
    public int CaptureWidth { get; set; }
    public int CaptureHeight { get; set; }
    public string OcrLanguage { get; set; } = "en-US";
    public int IntervalMs { get; set; } = 1000;
    public string ApiUrl { get; set; } = "https://api.openai.com/v1/chat/completions";
    public string Model { get; set; } = "";
    public string ProtectedApiKey { get; set; } = "";
    [JsonIgnore] public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 20;
    public double Temperature { get; set; } = 0.2;
    public int MaxTokens { get; set; } = 512;
    public double OverlayLeft { get; set; } = 220;
    public double OverlayTop { get; set; } = 420;
    public double OverlayWidth { get; set; } = 820;
    public double OverlayHeight { get; set; } = 170;
    public double OverlayOpacity { get; set; } = 0.88;
    public double FontSize { get; set; } = 25;
    public string FontColor { get; set; } = "#FFFFFF";
    public bool ShowOriginal { get; set; }
    public bool OverlayClickThrough { get; set; } = true;
    public int ToggleHotkey { get; set; } = 119; // F8
    public int RegionHotkey { get; set; } = 120; // F9
    public int UnlockHotkey { get; set; } = 121; // F10

    public Rectangle CaptureRect => new(CaptureX, CaptureY, CaptureWidth, CaptureHeight);
    public void SetCaptureRect(Rectangle value)
    {
        CaptureX = value.X; CaptureY = value.Y;
        CaptureWidth = value.Width; CaptureHeight = value.Height;
    }
}

public static class ConfigStore
{
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    public static readonly string DirectoryPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameZh");
    public static readonly string FilePath = Path.Combine(DirectoryPath, "settings.json");
    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static (AppSettings Settings, string? Warning) Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return (Defaults(), null);
            var data = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), JsonOptions) ?? Defaults();
            if (!string.IsNullOrEmpty(data.ProtectedApiKey))
                data.ApiKey = Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(data.ProtectedApiKey), null, DataProtectionScope.CurrentUser));
            Validate(data);
            return (data, null);
        }
        catch (Exception ex)
        {
            try
            {
                if (File.Exists(FilePath))
                    File.Move(FilePath, FilePath + ".bad-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            }
            catch { }
            AppLog.Write("config_recovered", ex.GetType().Name);
            return (Defaults(), "配置文件损坏或密钥无法解密，已恢复默认设置。请重新配置翻译服务。");
        }
    }

    public static void Save(AppSettings data)
    {
        Validate(data);
        Directory.CreateDirectory(DirectoryPath);
        data.ProtectedApiKey = string.IsNullOrEmpty(data.ApiKey) ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(data.ApiKey), null, DataProtectionScope.CurrentUser));
        string temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(data, JsonOptions), Encoding.UTF8);
        File.Move(temp, FilePath, true);
    }

    public static AppSettings Defaults()
    {
        var s = new AppSettings();
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        s.SetCaptureRect(new Rectangle(screen.Left + screen.Width / 5, screen.Top + screen.Height * 3 / 4,
            Math.Max(300, screen.Width * 3 / 5), Math.Max(80, screen.Height / 7)));
        double scale = Math.Max(1, GetDpiForSystem()) / 96.0;
        s.OverlayLeft = (screen.Left + screen.Width / 5) / scale;
        s.OverlayTop = (screen.Top + screen.Height / 2) / scale;
        s.OverlayWidth = Math.Max(400, screen.Width * 3 / 5 / scale);
        return s;
    }

    static void Validate(AppSettings s)
    {
        s.IntervalMs = Math.Clamp(s.IntervalMs, 400, 5000);
        s.TimeoutSeconds = Math.Clamp(s.TimeoutSeconds, 5, 120);
        s.MaxTokens = Math.Clamp(s.MaxTokens, 64, 4096);
        s.Temperature = Math.Clamp(s.Temperature, 0, 1);
        s.OverlayWidth = Math.Clamp(s.OverlayWidth, 300, 2400);
        s.OverlayHeight = Math.Clamp(s.OverlayHeight, 95, 400);
        s.OverlayOpacity = Math.Clamp(s.OverlayOpacity, 0.25, 1);
        s.FontSize = Math.Clamp(s.FontSize, 14, 48);
        s.CaptureWidth = Math.Clamp(s.CaptureWidth, 0, 10000);
        s.CaptureHeight = Math.Clamp(s.CaptureHeight, 0, 10000);
        s.ApiUrl ??= ""; s.Model ??= ""; s.OcrLanguage ??= "en-US";
    }
}

public static class AppLog
{
    static readonly object Gate = new();
    static string LogPath => Path.Combine(ConfigStore.DirectoryPath, "gamezh.log");

    public static void Write(string name, string detail = "")
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(ConfigStore.DirectoryPath);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length >= 1024 * 1024)
                {
                    string older = LogPath + ".2";
                    if (File.Exists(older)) File.Delete(older);
                    string previous = LogPath + ".1";
                    if (File.Exists(previous)) File.Move(previous, older);
                    File.Move(LogPath, previous);
                }
                File.AppendAllText(LogPath, $"{DateTime.Now:O} {name} {detail[..Math.Min(detail.Length, 160)]}{Environment.NewLine}");
            }
        }
        catch { }
    }
}
