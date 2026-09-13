using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
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

    /// <summary>
    /// The tileset case: a 2x2 sheet detected at a lopsided 24x18, whose every tile must come
    /// out 6x6, giving a square 12x12 sheet. Converting again must reuse the image already
    /// open — the stream is long gone by then.
    /// </summary>
    [AvaloniaFact]
    public async Task Setting_ATileSize_RedrawsEveryTileOfTheOpenSheetAtThatSize()
    {
        var window = new MainWindow();
        window.Show();

        await window.LoadAsync(SyntheticArt(), "art.png");

        window.GetControl<NumericUpDown>("SheetTilesInput").Value = 2;
        window.GetControl<NumericUpDown>("TileSizeInput").Value = 6;

        await window.ConvertAsync();

        var result = window.GetControl<ImageControl>("ResultImage");
        var status = window.GetControl<TextBlock>("StatusText").Text;

        var bitmap = Assert.IsType<Bitmap>(result.Source);

        Assert.True(new PixelSize(12, 12) == bitmap.PixelSize, $"got {bitmap.PixelSize}; status: {status}");
        Assert.Contains("12×12", window.GetControl<TextBlock>("ResultCaption").Text);
        Assert.True(window.GetControl<Button>("SaveButton").IsEnabled);
    }

    /// <summary>
    /// Converting used to disable the size boxes, which dropped the focus on every keystroke
    /// and made the user click back into the box to type the next digit.
    /// </summary>
    [AvaloniaFact]
    public async Task Typing_ASize_KeepsTheFocusInTheBox()
    {
        var window = new MainWindow();
        window.Show();

        await window.LoadAsync(SyntheticArt(), "art.png");

        var input = window.GetControl<NumericUpDown>("TileSizeInput");

        input.Focus();
        Assert.True(input.IsKeyboardFocusWithin, "the box never took focus to begin with");

        input.Value = 1;
        await window.ConvertAsync();
        input.Value = 12;
        await window.ConvertAsync();

        Assert.True(input.IsKeyboardFocusWithin, "focus left the box while converting");
        Assert.True(input.IsEnabled, "the box was left disabled");
    }

    /// <summary>
    /// The spinner buttons used to claim the whole control, leaving a text field a few pixels
    /// wide with no room to type a size into.
    /// </summary>
    [AvaloniaFact]
    public void The_SizeInputs_HaveRoomToTypeIn()
    {
        var window = new MainWindow();
        window.Show();

        window.Measure(new Avalonia.Size(1040, 680));
        window.Arrange(new Rect(0, 0, 1040, 680));

        string[] inputs = ["SheetTilesInput", "TileSizeInput"];

        foreach (var name in inputs)
        {
            var field = window
                .GetControl<NumericUpDown>(name)
                .GetVisualDescendants()
                .OfType<TextBox>()
                .Single();

            Assert.True(field.Bounds.Width >= 40, $"{name}: text field is {field.Bounds.Width}px");
        }
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
