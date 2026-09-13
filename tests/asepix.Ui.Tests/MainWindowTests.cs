using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Headless.XUnit;
using asepix.Ui;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;
using ImageControl = Avalonia.Controls.Image;

[assembly: AvaloniaTestApplication(typeof(asepix.Ui.Tests.TestApp))]

namespace asepix.Ui.Tests;

public static class TestApp
{
    // Real Skia drawing, not the headless stub: with stubbed drawing every Bitmap decodes
    // to a 1x1 placeholder and any assertion about the images would be vacuous.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder
            .Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public class MainWindowTests
{
    /// <summary>
    /// Guards the whole open path, including that the named controls are actually wired up —
    /// a hand-written InitializeComponent silently leaves every one of them null.
    /// </summary>
    [AvaloniaFact]
    public async Task Opening_AnImage_FillsBothPanesAndEnablesSaving()
    {
        var window = new MainWindow();
        window.Show();

        await window.LoadAsync(SyntheticArt(), "art.png");

        var source = window.GetControl<ImageControl>("SourceImage");
        var result = window.GetControl<ImageControl>("ResultImage");

        var status = window.GetControl<TextBlock>("StatusText").Text;

        Assert.True(source.Source is not null, $"source pane empty; status: {status}");
        Assert.True(result.Source is not null, $"result pane empty; status: {status}");

        // 24x18 art upscaled by 14.5 must come back at its original size.
        var bitmap = Assert.IsType<Bitmap>(result.Source);
        Assert.Equal(new PixelSize(24, 18), bitmap.PixelSize);

        Assert.True(window.GetControl<Button>("SaveButton").IsEnabled);
        Assert.Equal("art.png", window.GetControl<TextBlock>("HeaderText").Text);
        Assert.Contains("24×18", window.GetControl<TextBlock>("ResultCaption").Text);
    }

    [AvaloniaFact]
    public async Task Opening_SomethingThatIsNotAnImage_ReportsItAndKeepsSavingDisabled()
    {
        var window = new MainWindow();
        window.Show();

        await window.LoadAsync(new MemoryStream("not an image"u8.ToArray()), "notes.txt");

        Assert.False(window.GetControl<Button>("SaveButton").IsEnabled);
        Assert.Contains("не изображение", window.GetControl<TextBlock>("StatusText").Text);
    }

    /// <summary>
    /// Pixel art upscaled by a fractional factor, the way a generated image arrives. Kept
    /// comfortably above the detector's minimum edge count — its limits are covered by the
    /// GridDetector tests, not here.
    /// </summary>
    private static Stream SyntheticArt()
    {
        Rgba32[] palette = [new(20, 20, 30), new(200, 160, 90), new(90, 140, 110)];

        using var art = new Image<Rgba32>(24, 18);
        var random = new Random(5);

        art.ProcessPixelRows(accessor =>
        {
            for (var y = 0; y < accessor.Height; y++)
            {
                var row = accessor.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = palette[random.Next(palette.Length)];
            }
        });

        art.Mutate(context => context.Resize(348, 261, KnownResamplers.Bicubic));

        var stream = new MemoryStream();
        art.SaveAsPng(stream);
        stream.Position = 0;

        return stream;
    }
}
