using System.Drawing.Drawing2D;
using System.Reflection;
using System.Runtime.InteropServices;

namespace UsageTray.UI;

internal static class AppIcon
{
    private static readonly byte[]? s_iconBytes = LoadIconBytes();

    private static byte[]? LoadIconBytes()
    {
        try
        {
            var assembly = typeof(AppIcon).Assembly;
            using var stream = assembly.GetManifestResourceStream("UsageTray.Resources.app.ico");
            if (stream != null)
            {
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                return ms.ToArray();
            }
        }
        catch
        {
            // Ignore and use fallback
        }

        return null;
    }

    public static Icon Create()
    {
        if (s_iconBytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(s_iconBytes);
                return new Icon(ms);
            }
            catch
            {
                // Fallback to drawing
            }
        }

        return CreateFallback();
    }

    public static Icon Create(int width, int height)
    {
        if (s_iconBytes is { Length: > 0 })
        {
            try
            {
                using var ms = new MemoryStream(s_iconBytes);
                return new Icon(ms, width, height);
            }
            catch
            {
                // Fallback to drawing
            }
        }

        return CreateFallback();
    }

    private static Icon CreateFallback()
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        using (var background = new SolidBrush(Color.FromArgb(245, 248, 252)))
        using (var dark = new SolidBrush(Color.FromArgb(44, 52, 64)))
        using (var accent = new SolidBrush(Color.FromArgb(69, 125, 196)))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            graphics.FillRoundedRectangle(background, new Rectangle(1, 1, 30, 30), 7);
            graphics.FillRectangle(dark, new Rectangle(7, 8, 8, 8));
            graphics.FillRectangle(accent, new Rectangle(17, 8, 8, 8));
            graphics.FillRectangle(accent, new Rectangle(7, 18, 8, 8));
            graphics.FillRectangle(dark, new Rectangle(17, 18, 8, 8));
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var icon = Icon.FromHandle(handle);
            return (Icon)icon.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    private static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
