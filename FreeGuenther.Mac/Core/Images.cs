using CoreGraphics;
using ImageIO;

namespace LmChat;

// Bilder für Modelle und Chat: alles wird als PNG gespeichert, große Bilder werden verkleinert
internal static class Images
{
    public const int MaxImageSide = 2048;

    public static readonly string[] Extensions = [".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff", ".heic"];

    public static bool IsImageFile(string path) => Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    // null = kein lesbares Bild
    public static byte[]? ToPng(byte[] data, int maxSide = int.MaxValue)
    {
        using var nsData = NSData.FromArray(data);
        using var source = CGImageSource.FromData(nsData);
        if (source == null || source.ImageCount == 0) return null;
        using var full = source.CreateImage(0, (CGImageOptions?)null);
        if (full == null) return null;

        CGImage? scaled = null;
        if (Math.Max(full.Width, full.Height) > maxSide)
            scaled = source.CreateThumbnail(0, new CGImageThumbnailOptions
            {
                MaxPixelSize = maxSide,
                CreateThumbnailFromImageAlways = true,
                CreateThumbnailWithTransform = true,
            });
        using (scaled)
        {
            using var output = new NSMutableData();
            using var target = CGImageDestination.Create(output, "public.png", 1);
            if (target == null) return null;
            target.AddImage(scaled ?? full);
            return target.Close() ? output.ToArray() : null;
        }
    }

    public static byte[]? LoadAsPng(string path, int maxSide = MaxImageSide) => ToPng(File.ReadAllBytes(path), maxSide);

    public static (int Width, int Height) PixelSize(byte[] data)
    {
        using var nsData = NSData.FromArray(data);
        using var source = CGImageSource.FromData(nsData);
        using var image = source?.CreateImage(0, (CGImageOptions?)null);
        return image == null ? (0, 0) : ((int)image.Width, (int)image.Height);
    }
}

// Bild, das bei einem Klick etwas auslöst
internal sealed class ClickImage : NSImageView
{
    public Action? Clicked;

    public override void MouseUp(NSEvent theEvent)
    {
        base.MouseUp(theEvent);
        Clicked?.Invoke();
    }

    public override void ResetCursorRects() => AddCursorRect(Bounds, NSCursor.PointingHandCursor);
}
