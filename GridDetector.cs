using SixLabors.ImageSharp.PixelFormats;

namespace asepix;

public enum Axis
{
    Horizontal,
    Vertical,
}

/// <summary>
/// A uniform pixel grid along one axis: cell boundaries lie at <c>Phase + k * Period</c>.
/// Both values are fractional — generated images rarely land on whole pixels.
/// </summary>
public readonly record struct Lattice(double Period, double Phase);

/// <summary>
/// Recovers the pixel grid of an upscaled pixel-art image from its edge structure.
/// </summary>
public static class GridDetector
{
    private const int PhaseSamples = 64;
    private const double CoarseStep = 0.02;
    private const double FineStep = 0.002;
    private const double FineWindow = 0.05;
    private const double MinPeriod = 4.0;

    /// <summary>Alpha at or above which a pixel counts as part of the art.</summary>
    public const byte OpaqueAlpha = 128;

    /// <summary>How close to the best fit a coarser period must be to be preferred over it.</summary>
    private const double HarmonicMargin = 0.001;

    /// <summary>
    /// Accumulates absolute neighbour differences perpendicular to <paramref name="axis"/>,
    /// giving one energy value per column (or row). Grid boundaries show up as peaks.
    /// </summary>
    public static double[] ComputeEdgeEnergy(
        ReadOnlySpan<Rgba32> pixels,
        int width,
        int height,
        Axis axis,
        int sampleStep = 3
    )
    {
        var length = axis == Axis.Horizontal ? width : height;
        var energy = new double[length];

        if (axis == Axis.Horizontal)
        {
            for (var y = 0; y < height; y += sampleStep)
            {
                var row = y * width;
                for (var x = 1; x < width; x++)
                    energy[x] += Difference(pixels[row + x - 1], pixels[row + x]);
            }
        }
        else
        {
            for (var y = 1; y < height; y++)
            {
                var row = y * width;
                var previous = row - width;
                for (var x = 0; x < width; x += sampleStep)
                    energy[y] += Difference(pixels[previous + x], pixels[row + x]);
            }
        }

        return energy;
    }

    /// <summary>
    /// Recovers the grid of an image, in its own pixel coordinates.
    /// </summary>
    /// <remarks>
    /// Measured over the opaque content alone. A generated sprite usually arrives centred in
    /// a mostly empty canvas, and an empty region carries no edges: left in, it contributes
    /// nothing but a lower threshold, so noise ripples elsewhere pass for grid lines.
    /// </remarks>
    public static (Lattice Horizontal, Lattice Vertical) Detect(
        ReadOnlySpan<Rgba32> pixels,
        int width,
        int height
    )
    {
        var (content, left, top, cropWidth, cropHeight) = ContentBox(pixels, width, height);

        var (horizontal, vertical) = Fit(
            ComputeEdgeEnergy(content, cropWidth, cropHeight, Axis.Horizontal),
            ComputeEdgeEnergy(content, cropWidth, cropHeight, Axis.Vertical)
        );

        // Back into full-image coordinates: the lattice keeps its period, and its boundaries
        // move with the crop.
        return (
            horizontal with { Phase = horizontal.Phase + left },
            vertical with { Phase = vertical.Phase + top }
        );
    }

    /// <summary>
    /// Fits the lattice pair that best explains the strong edges in both energy profiles.
    /// </summary>
    /// <remarks>
    /// One period for both axes: an art pixel is square, however lopsided the grid it fills.
    /// Fitting each axis on its own lets a marginal reading of one of them — a half-period
    /// harmonic is only a hair worse than the truth on a soft image — settle that axis alone,
    /// and the result comes out with the wrong aspect ratio. Scored together, the axis that
    /// does see the grid clearly carries the one the noise is drowning.
    ///
    /// The residual is normalised by the period. Without that, shrinking the period
    /// trivially shrinks the residual (it is bounded by half a period) and the search
    /// collapses towards <see cref="MinPeriod"/> instead of finding the real grid.
    /// </remarks>
    public static (Lattice Horizontal, Lattice Vertical) Fit(
        double[] horizontalEnergy,
        double[] verticalEnergy
    )
    {
        var peaks = new[] { FindStrongPeaks(horizontalEnergy), FindStrongPeaks(verticalEnergy) };

        if (peaks.Any(axis => axis.Count < 4))
        {
            throw new InvalidOperationException(
                "Could not detect a pixel grid: the image has too few distinct edges. "
                    + "Pass an explicit grid size instead."
            );
        }

        var maxPeriod = Math.Min(horizontalEnergy.Length, verticalEnergy.Length) / 6.0;

        if (maxPeriod <= MinPeriod)
            throw new InvalidOperationException("Image is too small to detect a pixel grid.");

        var coarse = Search(peaks, MinPeriod, maxPeriod, CoarseStep);

        var (period, phases) = Search(
            peaks,
            Math.Max(MinPeriod, coarse.Period - FineWindow),
            Math.Min(maxPeriod, coarse.Period + FineWindow),
            FineStep
        );

        return (new Lattice(period, phases[0]), new Lattice(period, phases[1]));
    }

    /// <summary>
    /// Turns a lattice into cell boundaries clamped to <c>[0, length]</c>.
    /// </summary>
    /// <remarks>
    /// A generated image is often cropped mid-pixel, leaving a partial cell at either end.
    /// A remnant narrower than half a period is not a real pixel, so it is merged into its
    /// neighbour rather than emitted as a sliver column.
    /// </remarks>
    public static double[] CellBounds(Lattice lattice, int length)
    {
        var (period, phase) = lattice;
        var bounds = new List<double> { 0.0 };

        for (var k = (long)Math.Floor(-phase / period); ; k++)
        {
            var line = phase + k * period;

            if (line <= 0)
                continue;
            if (line >= length)
                break;

            bounds.Add(line);
        }

        bounds.Add(length);

        if (bounds.Count > 2 && bounds[1] < period / 2)
            bounds.RemoveAt(1);
        if (bounds.Count > 2 && length - bounds[^2] < period / 2)
            bounds.RemoveAt(bounds.Count - 2);

        return [.. bounds];
    }

    /// <summary>Evenly spaced boundaries, for when the caller specifies the grid directly.</summary>
    public static double[] UniformBounds(int cells, int length)
    {
        if (cells < 1)
            throw new ArgumentOutOfRangeException(nameof(cells), "Grid size must be positive.");

        var bounds = new double[cells + 1];

        for (var i = 0; i <= cells; i++)
            bounds[i] = (double)i * length / cells;

        return bounds;
    }

    /// <remarks>
    /// Every sub-multiple of the true period also puts each grid line on a lattice point,
    /// so it fits at least as well and the plain minimum lands on the smallest harmonic.
    /// Normalising by the period penalises sub-multiples whenever edges are soft, but on
    /// a crisp grid the residuals are zero and they tie exactly — so a near-minimal multiple
    /// of the best fit is preferred to it. Only an exact multiple: any coarser period within
    /// the margin would do on a soft image, and the fit would creep a few percent coarse,
    /// spreading the grid lines off the art pixels they are meant to land on.
    /// </remarks>
    private static (double Period, double[] Phases) Search(
        List<int>[] peaks,
        double from,
        double to,
        double step
    )
    {
        var candidates = new List<(double Period, double[] Phases, double Cost)>();

        for (var period = from; period <= to; period += step)
        {
            var phases = new double[peaks.Length];
            var cost = 0.0;

            for (var axis = 0; axis < peaks.Length; axis++)
            {
                var bestCost = double.MaxValue;

                for (var i = 0; i < PhaseSamples; i++)
                {
                    var phase = period * i / PhaseSamples;
                    var axisCost = MeanAbsResidual(peaks[axis], period, phase) / period;

                    if (axisCost < bestCost)
                    {
                        bestCost = axisCost;
                        phases[axis] = phase;
                    }
                }

                cost += bestCost / peaks.Length;
            }

            candidates.Add((period, phases, cost));
        }

        if (candidates.Count == 0)
            throw new InvalidOperationException("Image is too small to detect a pixel grid.");

        var best = candidates.MinBy(candidate => candidate.Cost);
        var floor = best.Cost + HarmonicMargin;
        var chosen = best;

        for (var multiple = 2; ; multiple++)
        {
            var index = (int)Math.Round((best.Period * multiple - from) / step);

            if (index >= candidates.Count)
                break;

            if (candidates[index].Cost <= floor)
                chosen = candidates[index];
        }

        return (chosen.Period, chosen.Phases);
    }

    private static double MeanAbsResidual(List<int> peaks, double period, double phase)
    {
        var total = 0.0;

        foreach (var peak in peaks)
        {
            var k = Math.Round((peak - phase) / period);
            total += Math.Abs(peak - (phase + k * period));
        }

        return total / peaks.Count;
    }

    /// <summary>
    /// Local maxima standing at least one standard deviation above the mean. Thresholding
    /// discards the low-level gradient noise that diffusion models leave across flat areas.
    /// </summary>
    private static List<int> FindStrongPeaks(double[] energy)
    {
        var mean = energy.Average();
        var variance = energy.Sum(value => (value - mean) * (value - mean)) / energy.Length;
        var threshold = mean + Math.Sqrt(variance);

        var peaks = new List<int>();

        for (var i = 1; i < energy.Length - 1; i++)
        {
            if (energy[i] > threshold && energy[i] >= energy[i - 1] && energy[i] >= energy[i + 1])
                peaks.Add(i);
        }

        return peaks;
    }

    /// <summary>
    /// The pixels inside the image's opaque bounding box, and where that box sits. A fully
    /// opaque image is its own content box.
    /// </summary>
    private static (Rgba32[] Pixels, int Left, int Top, int Width, int Height) ContentBox(
        ReadOnlySpan<Rgba32> pixels,
        int width,
        int height
    )
    {
        int left = width,
            top = height,
            right = -1,
            bottom = -1;

        for (var y = 0; y < height; y++)
        {
            var row = y * width;

            for (var x = 0; x < width; x++)
            {
                if (pixels[row + x].A < OpaqueAlpha)
                    continue;

                left = Math.Min(left, x);
                right = Math.Max(right, x);
                top = Math.Min(top, y);
                bottom = Math.Max(bottom, y);
            }
        }

        if (right < left || bottom < top)
            throw new InvalidOperationException("Image is fully transparent: nothing to convert.");

        int cropWidth = right - left + 1,
            cropHeight = bottom - top + 1;

        var content = new Rgba32[cropWidth * cropHeight];

        for (var y = 0; y < cropHeight; y++)
            pixels.Slice((top + y) * width + left, cropWidth).CopyTo(content.AsSpan(y * cropWidth));

        return (content, left, top, cropWidth, cropHeight);
    }

    private static double Difference(Rgba32 a, Rgba32 b) =>
        Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
