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

    /// <summary>
    /// The game-asset case: the art on screen is a 32x32 grid, the game wants a 16x16 tile.
    /// Every 2x2 block here is one colour, so the shrunk tile must be that block art exactly —
    /// no blended in-between colours, no half-dropped pixels.
    /// </summary>
    [Fact]
    public void ToPixelArt_ShrinksTheDetectedGridToTheRequestedTileSize()
    {
        var blocks = BuildArt(16, 16, seed: 42);
        var art = Enlarge(blocks, 16, 16, factor: 2);

        using var upscaled = Upscale(art, 32, 32, 9.7);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(TileSize: 16, Colors: Palette.Length)
        );

        Assert.Equal(16, recovered.Width);
        Assert.Equal(16, recovered.Height);
        AssertMatchesArt(blocks, recovered);
    }

    /// <summary>A tile larger than the art repeats whole pixels rather than interpolating.</summary>
    [Fact]
    public void ToPixelArt_GrowsTheDetectedGridByRepeatingPixels()
    {
        var art = BuildArt(16, 16, seed: 8);

        using var upscaled = Upscale(art, 16, 16, 18.4);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(TileSize: 32, Colors: Palette.Length)
        );

        Assert.Equal(32, recovered.Width);
        Assert.Equal(32, recovered.Height);
        AssertMatchesArt(Enlarge(art, 16, 16, factor: 2), recovered);
    }

    /// <summary>
    /// A tileset: 2x2 tiles of 11x11 art, each tile painted from its own pair of colours, resized
    /// to 7x7 tiles. 22 cells do not divide into 14 output pixels, so this is exactly where a
    /// naive resize would drag a neighbour's art across a tile boundary — every output tile
    /// must still hold only the colours of the tile it came from.
    /// </summary>
    [Fact]
    public void ToPixelArt_ResizesEachTileOfASheet_WithoutBleedingAcrossTileBoundaries()
    {
        const int tile = 11,
            resized = 7;

        var art = BuildSheet(tile, seed: 21);

        using var upscaled = Upscale(art, 2 * tile, 2 * tile, 12.4);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(SheetTiles: 2, TileSize: resized, Colors: 8)
        );

        Assert.Equal(2 * resized, recovered.Width);
        Assert.Equal(2 * resized, recovered.Height);

        recovered.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);

                for (var x = 0; x < row.Length; x++)
                {
                    var owner = TileOf(x / resized, y / resized);
                    var actual = Nearest(row[x], SheetPalette);

                    Assert.True(
                        owner.Contains(actual),
                        $"pixel ({x},{y}) shows {row[x]} (nearest {actual}), which belongs to another tile"
                    );
                }
            }
        });
    }

    [Fact]
    public void Validate_RejectsAnEmptySheet()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConversionOptions(SheetTiles: 0).Validate()
        );
    }

    /// <summary>
    /// Tiles are square whatever the source was: a lopsided detected grid — the usual case,
    /// a sheet is rarely cropped to the exact pixel — still has to come out NxN.
    /// </summary>
    [Fact]
    public void ToPixelArt_GivesSquareTiles_FromANonSquareGrid()
    {
        var art = BuildArt(20, 14, seed: 3);

        using var upscaled = Upscale(art, 20, 14, 13.6);
        using var recovered = ImageConverter.ToPixelArt(
            upscaled,
            new ConversionOptions(TileSize: 10, Colors: Palette.Length)
        );

        Assert.Equal(10, recovered.Width);
        Assert.Equal(10, recovered.Height);
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
    public void Validate_RejectsAnEmptyTile()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConversionOptions(TileSize: 0).Validate()
        );
    }

    [Fact]
    public void Validate_RejectsAnInsetThatWouldLeaveNothingToSample()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ConversionOptions(Inset: 0.5).Validate()
        );
    }

    /// <summary>Eight colours, two per tile, so a tile's own art is identifiable in the output.</summary>
    private static readonly Rgba32[] SheetPalette =
    [
        new(20, 20, 28),
        new(60, 60, 76),
        new(150, 40, 40),
        new(220, 120, 100),
        new(30, 110, 60),
        new(120, 200, 140),
        new(40, 70, 160),
        new(120, 160, 240),
    ];

    /// <summary>The two palette entries painting the tile at that position of a 2x2 sheet.</summary>
    private static ReadOnlySpan<Rgba32> TileOf(int column, int row) =>
        SheetPalette.AsSpan((row * 2 + column) * 2, 2);

    /// <summary>A 2x2 sheet of square tiles, each textured from its own pair of colours.</summary>
    private static Rgba32[] BuildSheet(int tile, int seed)
    {
        var random = new Random(seed);
        var art = new Rgba32[4 * tile * tile];

        for (var y = 0; y < 2 * tile; y++)
        {
            for (var x = 0; x < 2 * tile; x++)
                art[y * 2 * tile + x] = TileOf(x / tile, y / tile)[random.Next(2)];
        }

        return art;
    }

    private static Rgba32[] BuildArt(int width, int height, int seed)
    {
        var random = new Random(seed);
        var art = new Rgba32[width * height];

        for (var i = 0; i < art.Length; i++)
            art[i] = Palette[random.Next(Palette.Length)];

        return art;
    }

    /// <summary>Repeats every art pixel into a <paramref name="factor"/>-square block.</summary>
    private static Rgba32[] Enlarge(Rgba32[] art, int width, int height, int factor)
    {
        var enlarged = new Rgba32[width * factor * height * factor];

        for (var y = 0; y < height * factor; y++)
        {
            for (var x = 0; x < width * factor; x++)
                enlarged[y * width * factor + x] = art[y / factor * width + x / factor];
        }

        return enlarged;
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
    private static Rgba32 Nearest(Rgba32 colour) => Nearest(colour, Palette);

    private static Rgba32 Nearest(Rgba32 colour, ReadOnlySpan<Rgba32> palette)
    {
        var best = palette[0];
        var bestDistance = int.MaxValue;

        foreach (var candidate in palette)
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
