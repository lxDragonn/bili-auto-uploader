using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace BilibiliUploader;

public static class CoverImage
{
    public const int MaxFileBytes = 5 * 1024 * 1024;

    public static byte[] Read(string path)
    {
        var file = new FileInfo(path);
        if (!file.Exists || file.Length == 0 || file.Length > MaxFileBytes)
            throw new InvalidOperationException("封面须为不超过 5 MB 的非空 JPG 或 PNG 图片。");
        if ((file.Attributes & FileAttributes.ReparsePoint) != 0 ||
            !new[] { ".jpg", ".jpeg", ".png" }.Contains(file.Extension.ToLowerInvariant()))
            throw new InvalidOperationException("请直接选择 JPG 或 PNG 封面文件，不使用文件链接。");
        try
        {
            using var stream = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length > MaxFileBytes) throw new InvalidOperationException("封面文件超过 5 MB。");
            using var original = Image.FromStream(stream, false, true);
            if (original.RawFormat.Guid != ImageFormat.Jpeg.Guid && original.RawFormat.Guid != ImageFormat.Png.Guid)
                throw new InvalidOperationException("封面内容必须是有效的 JPG 或 PNG 图片。");
            if ((long)original.Width * original.Height > 40_000_000)
                throw new InvalidOperationException("封面像素过大，请先缩小至 4000 万像素以内。");
            // Apply camera orientation, then draw into a new bitmap to discard EXIF/GPS metadata.
            if (original.PropertyIdList.Contains(0x112))
            {
                var orientation = original.GetPropertyItem(0x112)?.Value;
                if (orientation is { Length: >= 2 }) original.RotateFlip(orientation[0] switch
                {
                    2 => RotateFlipType.RotateNoneFlipX, 3 => RotateFlipType.Rotate180FlipNone,
                    4 => RotateFlipType.Rotate180FlipX, 5 => RotateFlipType.Rotate90FlipX,
                    6 => RotateFlipType.Rotate90FlipNone, 7 => RotateFlipType.Rotate270FlipX,
                    8 => RotateFlipType.Rotate270FlipNone, _ => RotateFlipType.RotateNoneFlipNone
                });
            }
            if (original.Width < 960 || original.Height < 600 ||
                (double)original.Width / original.Height < 4d / 3 || (double)original.Width / original.Height > 16d / 9)
                throw new InvalidOperationException("封面至少为 960×600 像素，宽高比须在 4:3 至 16:9 之间。");
            var ratio = Math.Min(1d, 1920d / Math.Max(original.Width, original.Height));
            using var normalized = new Bitmap(Math.Max(1, (int)(original.Width * ratio)), Math.Max(1, (int)(original.Height * ratio)), PixelFormat.Format24bppRgb);
            using (var graphics = Graphics.FromImage(normalized))
            {
                graphics.Clear(Color.White);
                graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                graphics.DrawImage(original, 0, 0, normalized.Width, normalized.Height);
            }
            using var output = new MemoryStream();
            using var quality = new EncoderParameters(1);
            quality.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 90L);
            normalized.Save(output, ImageCodecInfo.GetImageEncoders().Single(c => c.FormatID == ImageFormat.Jpeg.Guid), quality);
            if (output.Length > MaxFileBytes) throw new InvalidOperationException("处理后的封面超过 5 MB，请选择更小的图片。");
            return output.ToArray();
        }
        catch (Exception ex) when (ex is ArgumentException or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
        { throw new InvalidOperationException("无法解码封面，请选择完整有效的 JPG 或 PNG 图片。", ex); }
    }
}
