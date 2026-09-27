using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace GameZh;

public partial class MainWindow : Window
{
    const int WmHotkey = 0x0312;
    const uint ModCtrlShift = 0x0002 | 0x0004;
    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    readonly AppSettings settings;
    readonly OcrBridgeClient ocr = new();
    readonly OpenAiTranslator translator = new();
    readonly TranslationCoordinator coordinator;
    readonly OverlayWindow overlay;
    readonly Forms.NotifyIcon tray;
    string original = "";
    string translation = "";
    bool initializing = true;
    bool exiting;
    bool hotkeysReady;
    IntPtr hwnd;

    public MainWindow(AppSettings settings, string? warning)
    {
        InitializeComponent();
        this.settings = settings;
        overlay = new OverlayWindow(settings);
        coordinator = new TranslationCoordinator(settings, ocr, translator, CaptureForCoordinatorAsync);
        coordinator.StatusChanged += s => Dispatcher.InvokeAsync(() => SetStatus(s));
        coordinator.OriginalChanged += s => Dispatcher.InvokeAsync(() => { original = s; OriginalBox.Text = s; });
        coordinator.TranslationChanged += s => Dispatcher.InvokeAsync(() =>
        {
            translation = s;
            TranslationBox.Text = s;
            overlay.ShowText(original, s);
        });
        tray = CreateTray();
        FillUi();
        initializing = false;
        UpdateRegionText();
        if (warning is not null) SetStatus(warning);
        Loaded += async (_, _) => await RefreshLanguagesAsync();
        SourceInitialized += (_, _) =>
        {
            hwnd = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(hwnd)?.AddHook(HotkeyHook);
            hotkeysReady = true;
            RegisterHotkeys();
        };
        Closing += OnClosing;
        StateChanged += (_, _) => { if (WindowState == WindowState.Minimized) Hide(); };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.PowerModeChanged += PowerChanged;
    }

    Forms.NotifyIcon CreateTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("开始 / 暂停", null, async (_, _) => await ToggleAsync());
        menu.Items.Add("重新框选", null, async (_, _) => await PickAsync());
        menu.Items.Add("显示 / 隐藏字幕", null, (_, _) => ToggleOverlay());
        menu.Items.Add("设置", null, (_, _) => ShowMain());
        menu.Items.Add("退出", null, async (_, _) => await ExitAsync());
        var icon = new Forms.NotifyIcon { Text = "GameZh 游戏实时翻译", Icon = System.Drawing.SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        icon.DoubleClick += (_, _) => ShowMain();
        return icon;
    }

    void FillUi()
    {
        IntervalBox.Text = settings.IntervalMs.ToString(CultureInfo.InvariantCulture);
        ApiUrlBox.Text = settings.ApiUrl;
        ApiKeyBox.Password = settings.ApiKey;
        ModelBox.Text = settings.Model;
        TimeoutBox.Text = settings.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
        TemperatureBox.Text = settings.Temperature.ToString(CultureInfo.InvariantCulture);
        MaxTokensBox.Text = settings.MaxTokens.ToString(CultureInfo.InvariantCulture);
        ClickThroughBox.IsChecked = settings.OverlayClickThrough;
        ShowOriginalBox.IsChecked = settings.ShowOriginal;
        WidthSlider.Value = settings.OverlayWidth;
        OpacitySlider.Value = settings.OverlayOpacity;
        FontSlider.Value = settings.FontSize;
        ColorBox.Text = settings.FontColor;
        foreach (var box in new[] { ToggleHotkeyBox, RegionHotkeyBox, UnlockHotkeyBox })
            for (int vk = 116; vk <= 123; vk++) box.Items.Add("F" + (vk - 111));
        ToggleHotkeyBox.SelectedItem = "F" + (settings.ToggleHotkey - 111);
        RegionHotkeyBox.SelectedItem = "F" + (settings.RegionHotkey - 111);
        UnlockHotkeyBox.SelectedItem = "F" + (settings.UnlockHotkey - 111);
        if (!File.Exists(ConfigStore.FilePath)) GuideText.Visibility = Visibility.Visible;
        else if (!string.IsNullOrWhiteSpace(settings.Model)) GuideText.Visibility = Visibility.Collapsed;
    }

    void SetStatus(string message) { StatusText.Text = message; }
    void ShowMain() { Show(); WindowState = WindowState.Normal; Activate(); }
    void UpdateRegionText() => RegionText.Text = settings.CaptureWidth < 10 ? "尚未框选" :
        $"{settings.CaptureX}, {settings.CaptureY} · {settings.CaptureWidth}×{settings.CaptureHeight}";

    async Task RefreshLanguagesAsync()
    {
        try
        {
            string[] tags = await ocr.LanguagesAsync(CancellationToken.None);
            string desired = settings.OcrLanguage;
            initializing = true;
            LanguageBox.Items.Clear();
            foreach (string wanted in new[] { "en-US", "ja-JP", "ko-KR" })
                if (tags.Contains(wanted, StringComparer.OrdinalIgnoreCase)) LanguageBox.Items.Add(wanted);
            foreach (string tag in tags)
                if (!LanguageBox.Items.Contains(tag)) LanguageBox.Items.Add(tag);
            LanguageBox.SelectedItem = desired;
            if (LanguageBox.SelectedIndex < 0 && LanguageBox.Items.Count > 0) LanguageBox.SelectedIndex = 0;
            initializing = false;
            settings.OcrLanguage = LanguageBox.SelectedItem?.ToString() ?? "";
            if (LanguageBox.Items.Count == 0)
                SetStatus("没有可用的 Windows OCR 语言。请在 Windows 设置 → 时间和语言 → 语言和区域中安装语言及 OCR 功能。");
            else if (!tags.Contains(desired, StringComparer.OrdinalIgnoreCase))
                SetStatus($"未安装 {desired} OCR 组件，已切换到 {LanguageBox.Text}。可在 Windows 语言设置中添加。");
        }
        catch (Exception ex) { initializing = false; SetStatus("OCR 检查失败：" + ex.Message); }
    }

    async Task<CapturedFrame> CaptureForCoordinatorAsync(CancellationToken token)
    {
        bool hidden = overlay.IsVisible && overlay.IntersectsPhysical(settings.CaptureRect);
        if (hidden)
        {
            overlay.Hide();
            await Task.Delay(35, token);
        }
        try { return await Task.Run(() => CaptureService.Capture(settings.CaptureRect), token); }
        finally { if (hidden && !string.IsNullOrEmpty(translation) && !exiting) overlay.Show(); }
    }

    bool ReadUi(bool showError = true)
    {
        if (initializing) return false;
        try
        {
            settings.OcrLanguage = LanguageBox.SelectedItem?.ToString() ?? "";
            settings.IntervalMs = Math.Clamp(int.Parse(IntervalBox.Text), 400, 5000);
            settings.ApiUrl = ApiUrlBox.Text.Trim();
            settings.ApiKey = ApiKeyBox.Password;
            settings.Model = ModelBox.Text.Trim();
            settings.TimeoutSeconds = Math.Clamp(int.Parse(TimeoutBox.Text), 5, 120);
            settings.Temperature = Math.Clamp(double.Parse(TemperatureBox.Text, CultureInfo.InvariantCulture), 0, 1);
            settings.MaxTokens = Math.Clamp(int.Parse(MaxTokensBox.Text), 64, 4096);
            settings.OverlayClickThrough = ClickThroughBox.IsChecked == true;
            settings.ShowOriginal = ShowOriginalBox.IsChecked == true;
            settings.OverlayWidth = WidthSlider.Value;
            settings.OverlayOpacity = OpacitySlider.Value;
            settings.FontSize = FontSlider.Value;
            settings.FontColor = ColorBox.Text.Trim();
            if (System.Windows.Media.ColorConverter.ConvertFromString(settings.FontColor) is not System.Windows.Media.Color)
                throw new FormatException("字体颜色不是有效的 #RRGGBB 值");
            settings.ToggleHotkey = HotkeyCode(ToggleHotkeyBox, 119);
            settings.RegionHotkey = HotkeyCode(RegionHotkeyBox, 120);
            settings.UnlockHotkey = HotkeyCode(UnlockHotkeyBox, 121);
            overlay.ApplySettings();
            return true;
        }
        catch (Exception ex)
        {
            if (showError) SetStatus("设置格式有误：" + ex.Message + "。请检查间隔、超时、温度和最大输出数值。");
            return false;
        }
    }

    static int HotkeyCode(System.Windows.Controls.ComboBox box, int fallback) =>
        int.TryParse(box.SelectedItem?.ToString()?.TrimStart('F'), out int number) && number is >= 5 and <= 12 ? 111 + number : fallback;

    bool SaveSettings()
    {
        if (!ReadUi()) return false;
        try { ConfigStore.Save(settings); SetStatus("设置已保存。"); return true; }
        catch (Exception ex) { SetStatus("保存设置失败：" + ex.Message); return false; }
    }

    async Task ToggleAsync()
    {
        if (coordinator.IsRunning)
        {
            await coordinator.StopAsync();
            StartButton.Content = "开始翻译";
            return;
        }
        if (!ReadUi()) return;
        if (LanguageBox.Items.Count == 0) { SetStatus("请先安装 Windows OCR 语言组件。"); return; }
        if (settings.CaptureWidth < 10) { SetStatus("请先框选识别区域。"); return; }
        if (string.IsNullOrWhiteSpace(settings.Model)) { SetStatus("请填写模型名称，并先测试连接。"); return; }
        if (!SaveSettings()) return;
        coordinator.ResetCapture();
        coordinator.Start();
        StartButton.Content = "暂停翻译";
    }

    async Task PickAsync()
    {
        bool resume = coordinator.IsRunning;
        if (resume) await coordinator.StopAsync();
        bool overlayVisible = overlay.IsVisible;
        overlay.Hide(); Hide();
        try
        {
            await Task.Delay(80);
            using var picker = new RegionPicker();
            if (picker.ShowDialog() == Forms.DialogResult.OK)
            {
                settings.SetCaptureRect(picker.Selected);
                coordinator.ResetCapture();
                UpdateRegionText();
                SaveSettings();
                SetStatus("识别区域已更新。可点“识别预览”确认画面。");
            }
        }
        finally
        {
            ShowMain();
            if (overlayVisible && !string.IsNullOrEmpty(translation)) overlay.Show();
            if (resume) { coordinator.Start(); StartButton.Content = "暂停翻译"; }
        }
    }

    void ToggleOverlay()
    {
        if (overlay.IsVisible) overlay.Hide();
        else { overlay.ShowText(original, string.IsNullOrEmpty(translation) ? "字幕预览 · 等待翻译" : translation); }
    }

    void RegisterHotkeys()
    {
        if (!hotkeysReady) return;
        for (int id = 1; id <= 3; id++) UnregisterHotKey(hwnd, id);
        var bindings = new[] { settings.ToggleHotkey, settings.RegionHotkey, settings.UnlockHotkey };
        var names = new[] { "开始/暂停", "框选", "退出穿透" };
        var failures = new List<string>();
        for (int i = 0; i < bindings.Length; i++)
            if (!RegisterHotKey(hwnd, i + 1, ModCtrlShift, (uint)bindings[i])) failures.Add(names[i] + " Ctrl+Shift+F" + (bindings[i] - 111));
        if (failures.Count > 0) SetStatus("快捷键冲突或注册失败：" + string.Join("、", failures) + "。请改选其他 F 键；仍可使用托盘菜单。");
    }

    IntPtr HotkeyHook(IntPtr source, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message != WmHotkey) return IntPtr.Zero;
        handled = true;
        switch (wparam.ToInt32())
        {
            case 1: _ = ToggleAsync(); break;
            case 2: _ = PickAsync(); break;
            case 3:
                settings.OverlayClickThrough = false;
                ClickThroughBox.IsChecked = false;
                overlay.ApplySettings();
                SetStatus("已退出鼠标穿透模式，可拖动或缩放字幕窗。");
                break;
        }
        return IntPtr.Zero;
    }

    void DisplayChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        coordinator.ResetCapture();
        if (!Forms.SystemInformation.VirtualScreen.IntersectsWith(settings.CaptureRect))
            SetStatus("显示器布局已变化，识别区域不在屏幕上。请重新框选。");
    });

    void PowerChanged(object sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume) Dispatcher.InvokeAsync(() => { coordinator.ResetCapture(); SetStatus("系统已恢复，正在重新检查画面。"); });
    }

    void OnClosing(object? sender, CancelEventArgs e)
    {
        if (exiting) return;
        e.Cancel = true;
        Hide();
        SetStatus("已最小化到系统托盘。右击托盘图标可退出。");
    }

    async Task ExitAsync()
    {
        if (exiting) return;
        exiting = true;
        await coordinator.DisposeAsync();
        try { ConfigStore.Save(settings); }
        catch (Exception ex) { AppLog.Write("save_on_exit_error", ex.GetType().Name); }
        for (int id = 1; id <= 3; id++) if (hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, id);
        SystemEvents.DisplaySettingsChanged -= DisplayChanged;
        SystemEvents.PowerModeChanged -= PowerChanged;
        tray.Visible = false; tray.Dispose();
        overlay.Close();
        await ocr.DisposeAsync();
        translator.Dispose();
        Close();
        System.Windows.Application.Current.Shutdown();
    }

    async void Pick_Click(object sender, RoutedEventArgs e) => await PickAsync();
    async void StartPause_Click(object sender, RoutedEventArgs e) => await ToggleAsync();
    void ShowOverlay_Click(object sender, RoutedEventArgs e) => ToggleOverlay();
    void Save_Click(object sender, RoutedEventArgs e) => SaveSettings();
    async void Exit_Click(object sender, RoutedEventArgs e) => await ExitAsync();
    async void RefreshLanguages_Click(object sender, RoutedEventArgs e) => await RefreshLanguagesAsync();
    void Settings_Changed(object sender, RoutedEventArgs e)
    {
        if (initializing || !ReadUi(false)) return;
        if (ReferenceEquals(sender, LanguageBox) || ReferenceEquals(sender, ApiUrlBox) ||
            ReferenceEquals(sender, ApiKeyBox) || ReferenceEquals(sender, ModelBox)) coordinator.ResetCapture();
    }
    void OverlaySetting_Changed(object sender, RoutedEventArgs e) { if (!initializing) ReadUi(false); }
    void Hotkey_Changed(object sender, RoutedEventArgs e) { if (!initializing && ReadUi(false)) RegisterHotkeys(); }

    async void Once_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadUi()) return;
        coordinator.ResetCapture();
        await coordinator.ProcessOnceAsync(true);
    }

    async void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadUi()) return;
        try
        {
            var frame = await CaptureForCoordinatorAsync(CancellationToken.None);
            var source = BitmapSource.Create(frame.Width, frame.Height, 96, 96, System.Windows.Media.PixelFormats.Bgra32,
                null, frame.Pixels, frame.Width * 4);
            source.Freeze();
            var preview = new Window
            {
                Title = "识别区域预览", Owner = this, Width = Math.Min(1000, frame.Width + 40),
                Height = Math.Min(700, frame.Height + 80), Background = System.Windows.Media.Brushes.Black,
                Content = new System.Windows.Controls.ScrollViewer
                { Content = new System.Windows.Controls.Image { Source = source, Stretch = System.Windows.Media.Stretch.None } }
            };
            preview.ShowDialog();
        }
        catch (Exception ex) { SetStatus("预览失败：" + ex.Message + "。可尝试无边框窗口模式并重新框选。"); }
    }

    async void TestConnection_Click(object sender, RoutedEventArgs e)
    {
        if (!ReadUi()) return;
        SetStatus("正在发送真实连接测试请求…");
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds + 3));
            var options = new TranslationOptions(settings.ApiUrl, settings.ApiKey, settings.Model,
                settings.TimeoutSeconds, settings.Temperature, settings.MaxTokens);
            string result = await translator.TestAsync(options, timeout.Token);
            SetStatus("连接成功，测试译文：" + result[..Math.Min(result.Length, 100)]);
            SaveSettings();
        }
        catch (Exception ex) { SetStatus("连接测试失败：" + ex.Message); }
    }
}
