using asepix;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace asepix.Tests;

public class ImageConverterTests
{
    private static readonly Rgba32[] Palette =
    [
        new(28, 24, 40),
        new(150, 105, 61),
        new(233, 200, 140),
        new(70, 130, 90),
    ];

    /// <summary>
    /// The whole point of the tool: art that was upscaled by a fractional factor and
    /// smoothed — the shape a generated image arrives in — must come back exactly.
    /// </summary>
    [Theory]
    [InlineData(16, 12, 31.35)] // non-square, fractional scale
    [InlineData(24, 24, 9.8)]
    [InlineData(11, 19, 17.27)]
    public void ToPixelArt_RecoversArt_UpscaledByAFractionalFactorAndBlurred(
        int artWidth,
        int artHeight,
        double scale
    )
    {
        var art = BuildArt(artWidth, artHeight, seed: 1234);

        using var upscaled = Upscale(art, artWidth, artHeight, scale);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(Colors: Palette.Length)
        );

        Assert.Equal(artWidth, recovered.Width);
        Assert.Equal(artHeight, recovered.Height);
        AssertMatchesArt(art, recovered);
    }

    [Fact]
    public void ToPixelArt_UsesTheRequestedGrid_OnNonSquareImages()
    {
        var art = BuildArt(20, 8, seed: 77);

        using var upscaled = Upscale(art, 20, 8, 12.5);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(GridWidth: 20, GridHeight: 8, Colors: Palette.Length)
        );

        Assert.Equal(20, recovered.Width);
        Assert.Equal(8, recovered.Height);
        AssertMatchesArt(art, recovered);
    }

    [Fact]
    public void ToPixelArt_EmitsNoIntermediateColours()
    {
        var art = BuildArt(16, 16, seed: 9);

        using var upscaled = Upscale(art, 16, 16, 20.4);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(Colors: Palette.Length)
        );

        var distinct = new HashSet<Rgba32>();
        recovered.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                foreach (var pixel in accessor.GetRowSpan(y))
                    distinct.Add(pixel);
            }
        });

        Assert.True(distinct.Count <= Palette.Length, $"got {distinct.Count} colours");
    }

    [Fact]
    public void DefaultOptions_AreUsable()
    {
        var options = new ConversionOptions();

        options.Validate();

        Assert.Equal(8, options.Colors);
        Assert.Equal(0.25, options.Inset);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(300)]
    public void Validate_RejectsImpossiblePaletteSizes(int colors)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConversionOptions(Colors: colors).Validate()
        );
    }

    [Fact]
    public void Validate_RejectsAnInsetThatWouldLeaveNothingToSample()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConversionOptions(Inset: 0.5).Validate()
        );
    }

    private static Rgba32[] BuildArt(int width, int height, int seed)
    {
        var random = new Random(seed);
        var art = new Rgba32[width * height];

        for (var i = 0; i < art.Length; i++)
            art[i] = Palette[random.Next(Palette.Length)];

        return art;
    }

    /// <summary>Blows the art up by a fractional factor, smoothing edges the way a generator does.</summary>
    private static Image<Rgba32> Upscale(Rgba32[] art, int width, int height, double scale)
    {
        var image = Image.LoadPixelData<Rgba32>(art, width, height);

        image.Mutate(context =>
            context.Resize(
                (int)Math.Round(width * scale),
                (int)Math.Round(height * scale),
                KnownResamplers.Bicubic
            )
        );

        return image;
    }

    private static void AssertMatchesArt(Rgba32[] art, Image<Rgba32> recovered)
    {
        recovered.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    var expected = art[y * accessor.Width + x];
                    var actual = Nearest(row[x]);

                    Assert.True(
                        expected.Equals(actual),
                        $"cell ({x},{y}): expected {expected}, got {row[x]} (nearest {actual})"
                    );
                }
            }
        });
    }

    /// <summary>
    /// Quantisation picks its own representative colours, so compare by nearest palette
    /// entry rather than demanding byte-identical output.
    /// </summary>
    private static Rgba32 Nearest(Rgba32 colour)
    {
        var best = Palette[0];
        var bestDistance = int.MaxValue;

        foreach (var candidate in Palette)
        {
            int dr = candidate.R - colour.R,
                dg = candidate.G - colour.G,
                db = candidate.B - colour.B;

            var distance = dr * dr + dg * dg + db * db;

            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = candidate;
            }
        }

        return best;
    }
}
