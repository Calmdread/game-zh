using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace GameZh;

public sealed record CapturedFrame(int Width, int Height, byte[] Pixels)
{
    public byte[] Signature()
    {
        byte[] signature = new byte[Math.Min(12000, Width * Height)];
        FillSignature(signature);
        return signature;
    }

    public void FillSignature(Span<byte> signature)
    {
        // Sample the entire region. Small pixel noise is ignored, while text changes trigger OCR.
        int count = Math.Min(signature.Length, Width * Height);
        int stride = Math.Max(1, Width * Height / count);
        for (int i = 0, pixel = 0; i < count; i++, pixel += stride)
        {
            int offset = Math.Min(Pixels.Length - 4, pixel * 4);
            signature[i] = (byte)((Pixels[offset] * 11 + Pixels[offset + 1] * 59 + Pixels[offset + 2] * 30) / 100);
        }
        signature[count..].Clear();
    }

    public static bool HasChanged(byte[]? previous, byte[] current)
    {
        if (previous is null || previous.Length != current.Length) return true;
        int changes = 0;
        for (int i = 0; i < current.Length; i++)
            if (Math.Abs(current[i] - previous[i]) >= 18 && ++changes >= 4) return true;
        return false;
    }
}

public static class CaptureService
{
    public static CapturedFrame Capture(Rectangle region)
    {
        Rectangle virtualBounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        if (region.Width < 10 || region.Height < 10 || !virtualBounds.IntersectsWith(region))
            throw new InvalidOperationException("识别区域已不在屏幕上，请重新框选。若游戏是独占全屏，请改用无边框窗口模式。");
        Rectangle clipped = Rectangle.Intersect(region, virtualBounds);
        double scale = Math.Min(1.0, 2000.0 / Math.Max(clipped.Width, clipped.Height));
        int width = Math.Max(1, (int)(clipped.Width * scale));
        int height = Math.Max(1, (int)(clipped.Height * scale));
        using var screen = new Bitmap(clipped.Width, clipped.Height, PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(screen))
            g.CopyFromScreen(clipped.X, clipped.Y, 0, 0, screen.Size, CopyPixelOperation.SourceCopy);
        Bitmap? scaled = null;
        try
        {
            Bitmap image = screen;
            if (width != screen.Width || height != screen.Height)
            {
                scaled = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                using (Graphics g = Graphics.FromImage(scaled))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(screen, new Rectangle(0, 0, width, height));
                }
                image = scaled;
            }
            var bits = image.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            byte[] pixels = new byte[width * height * 4];
            try
            {
                for (int y = 0; y < height; y++)
                    Marshal.Copy(IntPtr.Add(bits.Scan0, y * bits.Stride), pixels, y * width * 4, width * 4);
            }
            finally { image.UnlockBits(bits); }
            return new CapturedFrame(width, height, pixels);
        }
        finally { scaled?.Dispose(); }
    }
}

public sealed class RegionPicker : System.Windows.Forms.Form
{
    System.Drawing.Point start;
    System.Drawing.Point end;
    bool selecting;
    public Rectangle Selected { get; private set; }

    public RegionPicker()
    {
        FormBorderStyle = System.Windows.Forms.FormBorderStyle.None;
        Bounds = System.Windows.Forms.SystemInformation.VirtualScreen;
        StartPosition = System.Windows.Forms.FormStartPosition.Manual;
        TopMost = true;
        DoubleBuffered = true;
        BackColor = Color.Black;
        Opacity = 0.48;
        Cursor = System.Windows.Forms.Cursors.Cross;
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == System.Windows.Forms.Keys.Escape) { DialogResult = System.Windows.Forms.DialogResult.Cancel; Close(); } };
        MouseDown += (_, e) => { if (e.Button == System.Windows.Forms.MouseButtons.Left) { start = e.Location; end = start; selecting = true; Invalidate(); } };
        MouseMove += (_, e) => { if (selecting) { end = e.Location; Invalidate(); } };
        MouseUp += (_, e) =>
        {
            if (!selecting) return;
            selecting = false;
            end = e.Location;
            Rectangle rect = Rect(start, end);
            if (rect.Width >= 10 && rect.Height >= 10)
            {
                Selected = new Rectangle(rect.X + Left, rect.Y + Top, rect.Width, rect.Height);
                DialogResult = System.Windows.Forms.DialogResult.OK;
                Close();
            }
        };
    }

    static Rectangle Rect(System.Drawing.Point a, System.Drawing.Point b) =>
        Rectangle.FromLTRB(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Max(a.X, b.X), Math.Max(a.Y, b.Y));

    protected override void OnPaint(System.Windows.Forms.PaintEventArgs e)
    {
        base.OnPaint(e);
        using var font = new Font("Microsoft YaHei UI", 18, FontStyle.Bold);
        e.Graphics.DrawString("拖动框选游戏文字 · Esc 取消", font, Brushes.White, 24, 24);
        if (!selecting) return;
        Rectangle r = Rect(start, end);
        using var brush = new SolidBrush(Color.FromArgb(100, 110, 220, 195));
        using var pen = new Pen(Color.White, 2);
        e.Graphics.FillRectangle(brush, r);
        e.Graphics.DrawRectangle(pen, r);
    }
}
