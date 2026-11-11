using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Remoter.App.Features;

public static class ScreenshotService
{
    public sealed record Shot(string Path, int Width, int Height);

    public static Task<Shot> CaptureAsync(CancellationToken ct) => Task.Run(Capture, ct);

    private static Shot Capture()
    {
        if (IsSessionLocked())
            throw new InvalidOperationException("Экран заблокирован — скриншот недоступен.");

        var bounds = SystemInformation.VirtualScreen;
        if (bounds.Width <= 0 || bounds.Height <= 0)
            throw new InvalidOperationException("Не удалось определить размер экрана.");

        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(bitmap))
        {
            graphics.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, bounds.Size, CopyPixelOperation.SourceCopy);
        }

        using var scaled = Scale(bitmap, 1920);

        var path = Path.Combine(Path.GetTempPath(), $"remoter-screen-{DateTime.Now:HHmmss-fff}.jpg");
        SaveJpeg(scaled, path, 80L);

        return new Shot(path, scaled.Width, scaled.Height);
    }

    private static Bitmap Scale(Bitmap source, int maxWidth)
    {
        if (source.Width <= maxWidth) return new Bitmap(source);

        var ratio = (double)maxWidth / source.Width;
        var width = maxWidth;
        var height = Math.Max(1, (int)Math.Round(source.Height * ratio));

        var result = new Bitmap(width, height);
        using var graphics = Graphics.FromImage(result);
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        graphics.DrawImage(source, 0, 0, width, height);
        return result;
    }

    private static void SaveJpeg(Bitmap bitmap, string path, long quality)
    {
        var encoder = ImageCodecInfo.GetImageEncoders()
            .FirstOrDefault(codec => codec.FormatID == ImageFormat.Jpeg.Guid);

        if (encoder is null)
        {
            bitmap.Save(path, ImageFormat.Jpeg);
            return;
        }

        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        bitmap.Save(path, encoder, parameters);
    }

    private static bool IsSessionLocked()
    {
        var desktop = OpenInputDesktop(0, false, DesiredAccessDesktopReadObjects);
        if (desktop == IntPtr.Zero) return true;
        CloseDesktop(desktop);
        return false;
    }

    private const uint DesiredAccessDesktopReadObjects = 0x0001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint desiredAccess);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool CloseDesktop(IntPtr desktop);
}
