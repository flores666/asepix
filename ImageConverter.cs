using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing.Processors.Quantization;

namespace asepix;

/// <param name="GridWidth">Width of the grid read out of the source, in cells; detected when null.</param>
/// <param name="GridHeight">Height of the grid read out of the source, in cells; detected when null.</param>
/// <param name="SheetTiles">Tiles along each side of the sheet; 1 for a single-tile image.</param>
/// <param name="TileSize">
/// Side of one tile in pixels; the detected grid is kept as-is when null. Tiles are square —
/// a sheet of 4 tiles at 16 pixels is a 64x64 image however lopsided the detected grid was.
/// </param>
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
    int SheetTiles = 1,
    int? TileSize = null,
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
        if (TileSize is < 1)
            throw new ArgumentOutOfRangeException(nameof(TileSize), "Tile size must be positive.");
        if (SheetTiles < 1)
            throw new ArgumentOutOfRangeException(
                nameof(SheetTiles),
                "A sheet holds at least one tile."
            );
    }
}

public static class ImageConverter
{
    /// <summary>
    /// Collapses an upscaled, anti-aliased, colour-noisy image back to true pixel art:
    /// one output pixel per detected grid cell, drawn from a quantised palette, then
    /// redrawn so that every tile of the sheet comes out at the requested tile size.
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

        var cells = new byte[cellsX * cellsY];
        var counts = new int[palette.Length];

        for (var cell = 0; cell < cells.Length; cell++)
        {
            Array.Clear(counts);

            for (var i = offsets[cell]; i < offsets[cell + 1]; i++)
                counts[indices[i]]++;

            cells[cell] = (byte)Winner(counts);
        }

        var xSpans = TileSpans(cellsX, options.SheetTiles, options.TileSize);
        var ySpans = TileSpans(cellsY, options.SheetTiles, options.TileSize);

        return Image.LoadPixelData<Rgba32>(
            Rescale(cells, cellsX, xSpans, ySpans, palette),
            xSpans.Length,
            ySpans.Length
        );
    }

    /// <summary>
    /// Draws the voted cells through the given spans. Each output pixel takes the colour
    /// holding the majority of the cells it covers, so a tile smaller than the art drops whole
    /// art pixels instead of blending neighbours into colours the palette never had — and a
    /// larger one repeats them, the way a pixel is meant to grow.
    /// </summary>
    private static Rgba32[] Rescale(
        byte[] cells,
        int cellsX,
        (int Start, int End)[] xSpans,
        (int Start, int End)[] ySpans,
        Rgba32[] palette
    )
    {
        int width = xSpans.Length,
            height = ySpans.Length;

        var result = new Rgba32[width * height];
        var counts = new int[palette.Length];

        for (var y = 0; y < height; y++)
        {
            var (top, bottom) = ySpans[y];

            for (var x = 0; x < width; x++)
            {
                var (left, right) = xSpans[x];

                Array.Clear(counts);

                for (var cellY = top; cellY < bottom; cellY++)
                {
                    var row = cellY * cellsX;
                    for (var cellX = left; cellX < right; cellX++)
                        counts[cells[row + cellX]]++;
                }

                result[y * width + x] = palette[Winner(counts)];
            }
        }

        return result;
    }

    /// <summary>
    /// The cells covered by each output pixel along one axis of a sheet, laying every tile out
    /// at <paramref name="perTile"/> pixels. Passing <see langword="null"/> keeps the detected
    /// grid, cell for cell.
    /// </summary>
    /// <remarks>
    /// One flat split is enough to keep tiles apart: tile <c>t</c> starts at cell
    /// <c>floor(t * cells / tiles)</c>, and because there are <c>tiles * perTile</c> outputs
    /// that is exactly where output <c>t * perTile</c> starts too. Every tile boundary is
    /// therefore also an output boundary, so no tile's art can bleed into its neighbour —
    /// even when the detected grid does not divide evenly by the tile count.
    /// </remarks>
    private static (int Start, int End)[] TileSpans(int cells, int tiles, int? perTile) =>
        CellSpans(cells, perTile is int size ? tiles * size : cells);

    /// <summary>
    /// Splits <paramref name="cells"/> across <paramref name="outputs"/> as half-open ranges
    /// tiling the axis without gaps or overlap. Truncating divisions rather than rounded
    /// bounds: a rounded bound can cross its neighbour and leave an output pixel reading a
    /// cell that is not under it.
    /// </summary>
    private static (int Start, int End)[] CellSpans(int cells, int outputs)
    {
        var spans = new (int Start, int End)[outputs];

        for (var i = 0; i < outputs; i++)
        {
            var start = Math.Min(i * cells / outputs, cells - 1);

            spans[i] = (start, Math.Max(start + 1, (i + 1) * cells / outputs));
        }

        return spans;
    }

    /// <summary>The most-voted palette entry; ties go to the lower index.</summary>
    private static int Winner(int[] counts)
    {
        var winner = 0;

        for (var i = 1; i < counts.Length; i++)
        {
            if (counts[i] > counts[winner])
                winner = i;
        }

        return winner;
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
