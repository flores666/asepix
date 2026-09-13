using asepix;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace asepix.Tests;

public class GridDetectorTests
{
    [Fact]
    public void CellBounds_SpanTheWholeAxis_WithoutDriftingOnFractionalPeriods()
    {
        var bounds = GridDetector.CellBounds(new Lattice(31.318, 0), 1254);

        Assert.Equal(0, bounds[0]);
        Assert.Equal(1254, bounds[^1]);

        // Every interior boundary sits exactly on the lattice: no accumulated rounding.
        for (var i = 1; i < bounds.Length - 1; i++)
            Assert.Equal(i * 31.318, bounds[i], precision: 9);
    }

    [Fact]
    public void CellBounds_MergesLeadingSliver_WhenGridIsPhaseShifted()
    {
        // A grid offset by 1.57px leaves a sub-pixel strip at the left edge.
        var bounds = GridDetector.CellBounds(new Lattice(31.318, 1.57), 1254);

        Assert.Equal(0, bounds[0]);
        Assert.Equal(40, bounds.Length - 1);
        Assert.All(
            Widths(bounds),
            width => Assert.True(width >= 31.318 / 2, $"sliver cell of width {width}")
        );
    }

    [Fact]
    public void CellBounds_MergesTrailingSliver()
    {
        // 100 = 3 * 30 + 10, so the final 10px remnant is under half a period.
        var bounds = GridDetector.CellBounds(new Lattice(30, 0), 100);

        Assert.Equal(3, bounds.Length - 1);
        Assert.Equal(100, bounds[^1]);
        Assert.All(Widths(bounds), width => Assert.True(width >= 15));
    }

    [Fact]
    public void CellBounds_KeepsPartialCell_WhenItIsWiderThanHalfAPeriod()
    {
        // 100 = 3 * 30 + 10, but phase 20 leaves a 20px head and a 20px tail.
        var bounds = GridDetector.CellBounds(new Lattice(30, 20), 100);

        Assert.Equal(4, bounds.Length - 1);
        Assert.Equal(new[] { 0.0, 20.0, 50.0, 80.0, 100.0 }, bounds);
    }

    [Theory]
    [InlineData(31.318, 1.57)]
    [InlineData(9.797, 0.0)]
    [InlineData(16.0, 7.25)]
    public void Fit_RecoversPeriodAndPhase_FromASyntheticGrid(double period, double phase)
    {
        var energy = new double[1254];

        for (var line = phase; line < energy.Length; line += period)
        {
            if (line >= 1)
                energy[(int)Math.Round(line)] = 100;
        }

        var fit = GridDetector.Fit(energy);

        Assert.Equal(period, fit.Period, precision: 1);

        // What actually matters downstream is that the grid resolves to the right cell count.
        Assert.Equal(
            (int)Math.Round((1254 - phase) / period),
            GridDetector.CellBounds(fit, 1254).Length - 1
        );
        // Phase is only defined modulo the period.
        var offset = Math.Abs(fit.Phase - phase) % period;
        Assert.True(
            Math.Min(offset, period - offset) < 1.0,
            $"phase {fit.Phase} does not match {phase} (mod {period})"
        );
    }

    [Fact]
    public void Fit_Throws_WhenTheImageHasNoGrid()
    {
        Assert.Throws<InvalidOperationException>(() => GridDetector.Fit(new double[1254]));
    }

    [Fact]
    public void ComputeEdgeEnergy_IsMeasuredPerAxis_OnNonSquareImages()
    {
        // 8x4 image split down the middle: a single vertical seam at x = 4.
        const int width = 8;
        const int height = 4;

        var pixels = new Rgba32[width * height];

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = x < 4 ? new Rgba32(0, 0, 0) : new Rgba32(255, 255, 255);
        }

        var horizontal = GridDetector.ComputeEdgeEnergy(pixels, width, height, Axis.Horizontal, 1);
        var vertical = GridDetector.ComputeEdgeEnergy(pixels, width, height, Axis.Vertical, 1);

        Assert.Equal(width, horizontal.Length);
        Assert.Equal(height, vertical.Length);

        // The seam lands on its own column, and nothing leaks into the other axis.
        Assert.Equal(4, Array.IndexOf(horizontal, horizontal.Max()));
        Assert.All(vertical, value => Assert.Equal(0, value));
    }

    private static IEnumerable<double> Widths(double[] bounds) =>
        bounds.Zip(bounds.Skip(1), (from, to) => to - from);
}
