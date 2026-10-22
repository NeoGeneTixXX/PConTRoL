using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PConTRoL.App.Services;

public static class IconLoader
{
    private static readonly Uri IconUri = new("pack://application:,,,/app.ico");

    public static ImageSource WindowIcon() => BitmapFrame.Create(IconUri);

    /// <summary>Tray-Symbol: passende Größe aus app.ico, über PNG in ein GDI-Icon umgewandelt.</summary>
    public static System.Drawing.Icon TrayIcon()
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        var stream = System.Windows.Application.GetResourceStream(IconUri)!.Stream;
        var decoder = new IconBitmapDecoder(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames.OrderBy(f => Math.Abs(f.PixelWidth - size)).First();

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(frame));
        using var ms = new MemoryStream();
        encoder.Save(ms);
        ms.Position = 0;
        using var bitmap = new System.Drawing.Bitmap(ms);
        return System.Drawing.Icon.FromHandle(bitmap.GetHicon());
    }
}
