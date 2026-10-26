using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace BirdPhotoSelector;

/// <summary>
/// BioCLIP 2 evaluation preprocessing: SquareContext20-v1 preserves the complete bird with
/// 20% context and edge extension, then RGB/shortest-side bicubic 224/center crop 224 and
/// OpenCLIP mean/std normalization. The square makes the official center crop a no-op.
/// The bicubic kernel uses a=-0.5,
/// antialiases downsampling, and rounds each RGB8 resize pass as Pillow does.
/// </summary>
public static class SpeciesImagePreprocessor
{
    public const int InputSize = 224;
    public const string CropPolicy = "SquareContext20-v1";
    private static readonly float[] Mean = { .48145466f, .4578275f, .40821073f };
    private static readonly float[] Std = { .26862954f, .26130258f, .27577711f };

    public static DenseTensor<float> CreateInput(BitmapSource orientedImage, BirdBox box,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(orientedImage);
        ArgumentNullException.ThrowIfNull(box);
        if (!box.IsValid) throw new ArgumentException("鸟类框无效。", nameof(box));
        cancellationToken.ThrowIfCancellationRequested();
        int width = orientedImage.PixelWidth, height = orientedImage.PixelHeight;
        int side = (int)Math.Clamp(Math.Ceiling(Math.Max(box.Width * width, box.Height * height) * 1.2), 1, int.MaxValue);
        int virtualLeft = CropOrigin((box.X + box.Width / 2) * width, side, width);
        int virtualTop = CropOrigin((box.Y + box.Height / 2) * height, side, height);
        int left = Math.Max(0, virtualLeft), top = Math.Max(0, virtualTop);
        int right = Math.Min(width, virtualLeft + side), bottom = Math.Min(height, virtualTop + side);
        int cropWidth = right - left, cropHeight = bottom - top;
        int stride = checked(cropWidth * 3);
        var pixels = new byte[checked(stride * cropHeight)];
        var rgb = new FormatConvertedBitmap(orientedImage, PixelFormats.Rgb24, null, 0);
        rgb.CopyPixels(new Int32Rect(left, top, cropWidth, cropHeight), pixels, stride, 0);
        byte[] resized = ResizeSquareBicubic(pixels, cropWidth, cropHeight, side,
            virtualLeft - left, virtualTop - top, cancellationToken);
        int plane = InputSize * InputSize;
        var result = new float[plane * 3];
        for (int y = 0; y < InputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < InputSize; x++)
            {
                int source = (y * InputSize + x) * 3, target = y * InputSize + x;
                for (int channel = 0; channel < 3; channel++)
                    result[channel * plane + target] = (resized[source + channel] / 255f - Mean[channel]) / Std[channel];
            }
        }
        return new DenseTensor<float>(result, new[] { 1, 3, InputSize, InputSize });
    }

    private static int CropOrigin(double center, int side, int extent) => side <= extent
        ? Math.Clamp((int)Math.Floor(center - side / 2.0), 0, extent - side)
        : (int)Math.Floor((extent - side) / 2.0);

    private static byte[] ResizeSquareBicubic(byte[] source, int width, int height, int side,
        int offsetX, int offsetY, CancellationToken cancellationToken)
    {
        var horizontal = Coefficients(side, InputSize);
        var vertical = horizontal;
        var intermediate = new byte[checked(InputSize * height * 3)];
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < InputSize; x++)
            {
                var weights = horizontal[x];
                for (int channel = 0; channel < 3; channel++)
                {
                    long sum = 1 << 21;
                    for (int i = 0; i < weights.Values.Length; i++)
                        sum += (long)source[(y * width + Math.Clamp(weights.Start + i + offsetX, 0, width - 1)) * 3 + channel] * weights.Values[i];
                    intermediate[(y * InputSize + x) * 3 + channel] = (byte)Math.Clamp(sum >> 22, 0, 255);
                }
            }
        }
        var result = new byte[InputSize * InputSize * 3];
        for (int y = 0; y < InputSize; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var weights = vertical[y];
            for (int x = 0; x < InputSize; x++)
                for (int channel = 0; channel < 3; channel++)
                {
                    long sum = 1 << 21;
                    for (int i = 0; i < weights.Values.Length; i++)
                        sum += (long)intermediate[(Math.Clamp(weights.Start + i + offsetY, 0, height - 1) * InputSize + x) * 3 + channel] * weights.Values[i];
                    result[(y * InputSize + x) * 3 + channel] = (byte)Math.Clamp(sum >> 22, 0, 255);
                }
        }
        return result;
    }

    private static ResampleWeights[] Coefficients(int input, int output)
    {
        var result = new ResampleWeights[output];
        double scale = (double)input / output, filterScale = Math.Max(1, scale), support = 2 * filterScale;
        for (int x = 0; x < output; x++)
        {
            double center = (x + .5) * scale;
            int start = Math.Max(0, (int)(center - support + .5));
            int end = Math.Min(input, (int)(center + support + .5));
            var values = new double[end - start];
            double sum = 0;
            for (int i = 0; i < values.Length; i++) { values[i] = Cubic((start + i - center + .5) / filterScale); sum += values[i]; }
            var fixedValues = new int[values.Length];
            for (int i = 0; i < values.Length; i++)
            {
                double value = values[i] / sum * (1 << 22);
                fixedValues[i] = (int)(value + (value < 0 ? -.5 : .5));
            }
            result[x] = new ResampleWeights(start, fixedValues);
        }
        return result;
    }
    private static double Cubic(double distance)
    {
        double x = Math.Abs(distance);
        if (x < 1) return ((1.5 * x - 2.5) * x) * x + 1;
        return x < 2 ? ((-.5 * x + 2.5) * x - 4) * x + 2 : 0;
    }
    private sealed record ResampleWeights(int Start, int[] Values);
}
