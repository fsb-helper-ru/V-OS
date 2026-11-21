using VOS.UI.Theme;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.UI;

namespace VOS.UI;

/// <summary>
/// Picks a vibrant, readable accent from artwork or wallpaper pixels: downsample, bucket by hue, weight by saturation and
/// mid-range lightness, then tune lightness for the current theme.
/// </summary>
public static class ArtworkColor
{
    public static async Task<Color?> ExtractAsync(byte[] encoded)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var w = new DataWriter(stream.GetOutputStreamAt(0))) { w.WriteBytes(encoded); await w.StoreAsync(); }
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            var transform = new BitmapTransform { ScaledWidth = 32, ScaledHeight = 32, InterpolationMode = BitmapInterpolationMode.Linear };
            var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            return FromPixels(data.DetachPixelData());
        }
        catch { return null; }
    }

    public static Color? FromPixels(byte[] bgra)
    {
        var buckets = new double[24];
        var sums = new (double r, double g, double b)[24];
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), l = (max + min) / 2, d = max - min;
            if (d < 0.08 || l < 0.12 || l > 0.92) continue;
            double s = d / (1 - Math.Abs(2 * l - 1));
            double h = max == r ? ((g - b) / d % 6) : max == g ? ((b - r) / d + 2) : ((r - g) / d + 4);
            int bucket = (int)((h * 60 + 360) % 360 / 15);
            double weight = s * (1 - Math.Abs(l - 0.55) * 1.4);
            if (weight <= 0) continue;
            buckets[bucket] += weight;
            sums[bucket].r += r * weight; sums[bucket].g += g * weight; sums[bucket].b += b * weight;
        }
        int best = Array.IndexOf(buckets, buckets.Max());
        if (buckets[best] < 0.6) return null;
        var c = Color.FromArgb(255, (byte)(sums[best].r / buckets[best] * 255), (byte)(sums[best].g / buckets[best] * 255), (byte)(sums[best].b / buckets[best] * 255));
        var p = ThemeService.Current.Palette;
        return Colors2.EnsureContrast(p.IsDark ? Colors2.Lighten(c, 0.15) : Colors2.Darken(c, 0.1), p.IsDark ? p.Ink : p.Surface1, 3.2);
    }
}
