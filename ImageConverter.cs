using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace asepix;

/// <param name="GridWidth">Output width in pixels; detected from the image when null.</param>
/// <param name="GridHeight">Output height in pixels; detected from the image when null.</param>
/// <param name="Colors">Palette size. Cannot be inferred reliably, so it stays a choice.</param>
/// <param name="Inset">
/// Fraction trimmed from each side of a cell before voting, to skip the anti-aliased ramp
/// that generated images leave along block edges.
/// </param>
/// <remarks>
/// A record <em>class</em>, deliberately: on a record struct <c>new ConversionOptions()</c>
/// binds to the implicit parameterless constructor and zeroes every field, silently
/// discarding the defaults declared here.
/// </remarks>
public sealed record ConversionOptions(
    int? GridWidth = null,
    int? GridHeight = null,
    int Colors = 8,
    double Inset = 0.25
)
{
    public void Validate()
    {
        if (Colors is < 2 or > 256)
            throw new ArgumentOutOfRangeException(nameof(Colors), "Palette must be 2-256 colours.");
        if (Inset is < 0 or >= 0.5)
            throw new ArgumentOutOfRangeException(nameof(Inset), "Inset must be within [0, 0.5).");
        if (GridWidth is < 1 || GridHeight is < 1)
            throw new ArgumentOutOfRangeException(nameof(GridWidth), "Grid size must be positive.");
    }
}

public static class ImageConverter
{
    /// <summary>
    /// Collapses an upscaled, anti-aliased, colour-noisy image back to true pixel art:
    /// one output pixel per detected grid cell, drawn from a quantised palette.
    /// </summary>
    public static Image<Rgba32> ToPixelArt(Image<Rgba32> source, ConversionOptions options)
    {
        options.Validate();

        int width = source.Width,
            height = source.Height;

        var pixels = new Rgba32[width * height];
        source.CopyPixelDataTo(pixels);

        var xBounds = BoundsFor(options.GridWidth, pixels, width, height, Axis.Horizontal);
        var yBounds = BoundsFor(options.GridHeight, pixels, width, height, Axis.Vertical);

        var xRanges = SampleRanges(xBounds, options.Inset, width);
        var yRanges = SampleRanges(yBounds, options.Inset, height);

        var (samples, offsets) = CollectInteriors(pixels, width, xRanges, yRanges);

        // The palette is built from cell interiors alone. Feeding it the whole image lets the
        // anti-aliased ramps — a large share of the area on a heavily upscaled picture — win
        // palette slots, and those blends then leak straight into the output.
        var (indices, palette) = Quantize(samples, options.Colors);

        int cellsX = xRanges.Length,
            cellsY = yRanges.Length;

        var result = new Rgba32[cellsX * cellsY];
        var counts = new int[palette.Length];

        for (var cell = 0; cell < result.Length; cell++)
        {
            Array.Clear(counts);

            for (var i = offsets[cell]; i < offsets[cell + 1]; i++)
                counts[indices[i]]++;

            var winner = 0;
            for (var i = 1; i < counts.Length; i++)
            {
                if (counts[i] > counts[winner])
                    winner = i;
            }

            result[cell] = palette[winner];
        }

        return Image.LoadPixelData<Rgba32>(result, cellsX, cellsY);
    }

    /// <summary>Per-cell pixel ranges, trimmed inwards to skip the anti-aliased ramp.</summary>
    private static (int Start, int End)[] SampleRanges(double[] bounds, double inset, int limit)
    {
        var ranges = new (int Start, int End)[bounds.Length - 1];

        for (var i = 0; i < ranges.Length; i++)
            ranges[i] = SampleRange(bounds[i], bounds[i + 1], inset, limit);

        return ranges;
    }

    /// <summary>
    /// Gathers every cell's interior pixels into one contiguous run, cell by cell, so the
    /// palette and the per-cell votes read the same samples.
    /// </summary>
    private static (Rgba32[] Samples, int[] Offsets) CollectInteriors(
        ReadOnlySpan<Rgba32> pixels,
        int width,
        (int Start, int End)[] xRanges,
        (int Start, int End)[] yRanges
    )
    {
        var offsets = new int[xRanges.Length * yRanges.Length + 1];
        var cell = 0;

        foreach (var (top, bottom) in yRanges)
        {
            foreach (var (left, right) in xRanges)
            {
                offsets[cell + 1] = offsets[cell] + (bottom - top) * (right - left);
                cell++;
            }
        }

        var samples = new Rgba32[offsets[^1]];
        var at = 0;

        foreach (var (top, bottom) in yRanges)
        {
            foreach (var (left, right) in xRanges)
            {
                for (var y = top; y < bottom; y++)
                {
                    var row = y * width;
                    for (var x = left; x < right; x++)
                        samples[at++] = pixels[row + x];
                }
            }
        }

        return (samples, offsets);
    }

    private static double[] BoundsFor(
        int? requested,
        ReadOnlySpan<Rgba32> pixels,
        int width,
        int height,
        Axis axis
    )
    {
        var length = axis == Axis.Horizontal ? width : height;

        if (requested is int cells)
            return GridDetector.UniformBounds(cells, length);

        var energy = GridDetector.ComputeEdgeEnergy(pixels, width, height, axis);

        return GridDetector.CellBounds(GridDetector.Fit(energy), length);
    }

    /// <summary>Maps every sample to an index into a shared palette.</summary>
    private static (byte[] Indices, Rgba32[] Palette) Quantize(Rgba32[] samples, int colors)
    {
        if (samples.Length == 0)
            throw new InvalidOperationException("Detected grid has no pixels to sample.");

        // Dithering scatters pixels between palette entries, which is exactly the noise
        // the vote is meant to remove — it must stay off.
        var quantizer = new WuQuantizer(
            new QuantizerOptions { MaxColors = colors, Dither = null }
        );

        using var frameQuantizer = quantizer.CreatePixelSpecificQuantizer<Rgba32>(
            Configuration.Default
        );
        using var strip = Image.LoadPixelData<Rgba32>(samples, samples.Length, 1);
        using var indexed = frameQuantizer.BuildPaletteAndQuantizeFrame(
            strip.Frames.RootFrame,
            strip.Bounds
        );

        return (indexed.DangerousGetRowSpan(0).ToArray(), indexed.Palette.ToArray());
    }

    /// <summary>The pixel range actually sampled for a cell, after trimming the edge ramp.</summary>
    private static (int Start, int End) SampleRange(double from, double to, double inset, int limit)
    {
        var trim = (to - from) * inset;

        var start = Math.Clamp((int)Math.Round(from + trim), 0, limit - 1);
        var end = Math.Clamp((int)Math.Round(to - trim), start + 1, limit);

        return (start, end);
    }
}
