using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Controls.Primitives;

namespace GameZh;

public partial class OverlayWindow : Window
{
    const int GwlExStyle = -20;
    const long WsExTransparent = 0x20;
    const long WsExNoActivate = 0x08000000;
    const uint WdaExcludeFromCapture = 0x11;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint affinity);

    readonly AppSettings settings;
    bool clickThrough;
    public bool ClickThrough
    {
        get => clickThrough;
        set { clickThrough = value; ApplyClickThrough(); ResizeGrip.Visibility = value ? Visibility.Collapsed : Visibility.Visible; }
    }

    public OverlayWindow(AppSettings settings)
    {
        InitializeComponent();
        this.settings = settings;
        Left = settings.OverlayLeft; Top = settings.OverlayTop;
        Width = settings.OverlayWidth;
        Height = settings.OverlayHeight;
        Opacity = settings.OverlayOpacity;
        TranslatedText.FontSize = settings.FontSize;
        OriginalText.Visibility = settings.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        clickThrough = settings.OverlayClickThrough;
        ResizeGrip.Visibility = clickThrough ? Visibility.Collapsed : Visibility.Visible;
        SourceInitialized += (_, _) =>
        {
            ApplyClickThrough();
            SetWindowDisplayAffinity(new WindowInteropHelper(this).Handle, WdaExcludeFromCapture);
        };
        LocationChanged += (_, _) => { settings.OverlayLeft = Left; settings.OverlayTop = Top; };
        SizeChanged += (_, _) => { settings.OverlayWidth = Width; settings.OverlayHeight = Height; };
        MouseLeftButtonDown += (_, e) => { if (!clickThrough && e.ButtonState == MouseButtonState.Pressed) DragMove(); };
        ApplySettings();
    }

    public void ApplySettings()
    {
        Width = settings.OverlayWidth;
        Opacity = settings.OverlayOpacity;
        TranslatedText.FontSize = settings.FontSize;
        OriginalText.Visibility = settings.ShowOriginal ? Visibility.Visible : Visibility.Collapsed;
        try { TranslatedText.Foreground = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(settings.FontColor)!); }
        catch { TranslatedText.Foreground = System.Windows.Media.Brushes.White; }
        ClickThrough = settings.OverlayClickThrough;
    }

    public void ShowText(string original, string translated)
    {
        OriginalText.Text = original;
        TranslatedText.Text = translated;
        if (!string.IsNullOrEmpty(translated) && !IsVisible) Show();
        if (string.IsNullOrEmpty(translated)) Hide();
    }

    public bool IntersectsPhysical(System.Drawing.Rectangle area)
    {
        if (!IsVisible) return false;
        System.Windows.Point a = PointToScreen(new System.Windows.Point(0, 0));
        System.Windows.Point b = PointToScreen(new System.Windows.Point(ActualWidth, ActualHeight));
        var rect = System.Drawing.Rectangle.FromLTRB((int)a.X, (int)a.Y, (int)b.X, (int)b.Y);
        return rect.IntersectsWith(area);
    }

    void ApplyClickThrough()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        long style = GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() | WsExNoActivate;
        style = clickThrough ? style | WsExTransparent : style & ~WsExTransparent;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(style));
    }

    void ResizeGrip_DragDelta(object sender, DragDeltaEventArgs e)
    {
        if (clickThrough) return;
        Width = Math.Clamp(Width + e.HorizontalChange, 300, 2400);
        Height = Math.Clamp(Height + e.VerticalChange, 95, 400);
    }
}
