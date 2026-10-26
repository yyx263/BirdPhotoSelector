using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace BirdPhotoSelector;

/// <summary>
/// Describes the background using a coarse color layout, color histogram, and texture.
/// The detected bird (plus a small margin) is excluded so a moving bird does not split a burst.
/// This deliberately does not depend on filenames, EXIF time, or the bird's own appearance.
/// </summary>
public static class BackgroundSimilarity
{
    private const int Columns = 8;
    private const int Rows = 6;
    private const int SampleWidth = 160;
    private const int SampleHeight = 120;

    public static BackgroundDescriptor Describe(BitmapSource source, BirdBox? bird = null) =>
        DescribeAll(source, bird is null ? Array.Empty<BirdBox>() : new[] { bird });

    public static BackgroundDescriptor DescribeAll(BitmapSource source, IEnumerable<BirdBox> birds)
    {
        ArgumentNullException.ThrowIfNull(source);
        var scaled = new TransformedBitmap(source, new ScaleTransform(
            (double)SampleWidth / source.PixelWidth, (double)SampleHeight / source.PixelHeight));
        var rgb = new FormatConvertedBitmap(scaled, PixelFormats.Bgr24, null, 0);
        int width = rgb.PixelWidth, height = rgb.PixelHeight, stride = width * 3;
        var pixels = new byte[stride * height];
        rgb.CopyPixels(pixels, stride, 0);

        var means = new double[Columns * Rows * 3];
        var texture = new double[Columns * Rows];
        var counts = new int[Columns * Rows];
        var histogram = new double[64];
        double total = 0;
        var excluded = birds.Where(bird => bird.IsValid && bird.Width * bird.Height < .65)
            .Select(bird => (Left: bird.X - bird.Width * .12, Right: bird.X + bird.Width * 1.12,
                Top: bird.Y - bird.Height * .12, Bottom: bird.Y + bird.Height * 1.12)).ToArray();
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double nx = (x + 0.5) / width, ny = (y + 0.5) / height;
                if (excluded.Any(box => nx >= box.Left && nx <= box.Right && ny >= box.Top && ny <= box.Bottom)) continue;
                int index = y * stride + x * 3;
                double b = pixels[index] / 255.0, g = pixels[index + 1] / 255.0, r = pixels[index + 2] / 255.0;
                int cell = Math.Min(Rows - 1, y * Rows / height) * Columns + Math.Min(Columns - 1, x * Columns / width);
                counts[cell]++;
                means[cell * 3] += r;
                means[cell * 3 + 1] += g;
                means[cell * 3 + 2] += b;
                AddColor(histogram, r, g, b);
                total++;
                if (x > 0)
                {
                    // Horizontal local variation distinguishes flat sky/water from foliage.
                    texture[cell] += (Math.Abs(pixels[index] - pixels[index - 3]) +
                        Math.Abs(pixels[index + 1] - pixels[index - 2]) +
                        Math.Abs(pixels[index + 2] - pixels[index - 1])) / 765.0;
                }
            }
        }
        for (int cell = 0; cell < counts.Length; cell++)
        {
            if (counts[cell] == 0) continue;
            for (int channel = 0; channel < 3; channel++) means[cell * 3 + channel] /= counts[cell];
            texture[cell] /= counts[cell];
        }
        if (total > 0) for (int i = 0; i < histogram.Length; i++) histogram[i] /= total;
        return new BackgroundDescriptor(means, texture, counts, histogram,
            (double)source.PixelWidth / source.PixelHeight);
    }

    private static void AddColor(double[] histogram, double r, double g, double b)
    {
        // Trilinear voting avoids abrupt changes at color-bin boundaries when exposure changes slightly.
        double red = r * 3, green = g * 3, blue = b * 3;
        int ri = (int)red, gi = (int)green, bi = (int)blue;
        double rf = red - ri, gf = green - gi, bf = blue - bi;
        for (int dr = 0; dr <= 1; dr++)
            for (int dg = 0; dg <= 1; dg++)
                for (int db = 0; db <= 1; db++)
                    histogram[Math.Min(3, ri + dr) * 16 + Math.Min(3, gi + dg) * 4 + Math.Min(3, bi + db)] +=
                        (dr == 0 ? 1 - rf : rf) * (dg == 0 ? 1 - gf : gf) * (db == 0 ? 1 - bf : bf);
    }

    /// <summary>Returns 0..1, where 1 is an identical background and a higher grouping threshold is stricter.</summary>
    public static double Compare(BackgroundDescriptor first, BackgroundDescriptor second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);
        // Rotation/cropping changes normally indicate a different composition.
        double aspectDifference = Math.Abs(Math.Log(first.AspectRatio / second.AspectRatio));
        if (aspectDifference > 0.35) return 0;
        double spatial = 0, texture = 0, weightSum = 0;
        for (int cell = 0; cell < first.Counts.Length; cell++)
        {
            double weight = Math.Min(first.Counts[cell], second.Counts[cell]);
            if (weight < 4) continue;
            double cellDistance = 0;
            for (int channel = 0; channel < 3; channel++)
            {
                double delta = first.Means[cell * 3 + channel] - second.Means[cell * 3 + channel];
                cellDistance += delta * delta;
            }
            spatial += weight * Math.Sqrt(cellDistance / 3);
            texture += weight * Math.Abs(first.Texture[cell] - second.Texture[cell]);
            weightSum += weight;
        }
        if (weightSum == 0) return 0;
        spatial /= weightSum;
        texture /= weightSum;
        double histogramDistance = 0;
        for (int i = 0; i < first.Histogram.Length; i++)
        {
            double delta = Math.Sqrt(first.Histogram[i]) - Math.Sqrt(second.Histogram[i]);
            histogramDistance += delta * delta;
        }
        histogramDistance = Math.Sqrt(histogramDistance / 2); // Hellinger distance in [0,1].
        double distance = 0.58 * spatial + 0.34 * histogramDistance + 0.08 * Math.Min(1, texture * 3);
        return Math.Clamp(1 - distance - aspectDifference * 0.15, 0, 1);
    }
}

public sealed class BackgroundDescriptor
{
    internal BackgroundDescriptor(double[] means, double[] texture, int[] counts, double[] histogram, double aspectRatio)
    {
        Means = means;
        Texture = texture;
        Counts = counts;
        Histogram = histogram;
        AspectRatio = aspectRatio;
    }

    internal double[] Means { get; }
    internal double[] Texture { get; }
    internal int[] Counts { get; }
    internal double[] Histogram { get; }
    internal double AspectRatio { get; }
}
