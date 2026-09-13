using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SharpImage = SixLabors.ImageSharp.Image;
using PixelArt = SixLabors.ImageSharp.Image<SixLabors.ImageSharp.PixelFormats.Rgba32>;

namespace asepix.Ui;

public partial class MainWindow : Window
{
    private PixelArt? source;
    private PixelArt? result;
    private Task? conversion;
    private bool queued;
    private string sourceName = "pixel-art";

    private readonly string? initialFile;

    public MainWindow()
        : this(null) { }

    /// <param name="initialFile">
    /// Opened on startup, so the window can be launched from a file manager or the shell
    /// with a path already in hand.
    /// </param>
    public MainWindow(string? initialFile)
    {
        this.initialFile = initialFile;

        InitializeComponent();

        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override async void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        if (initialFile is null)
            return;

        if (await StorageProvider.TryGetFileFromPathAsync(initialFile) is { } file)
            await LoadAsync(file);
        else
            StatusText.Text = $"File not found: {initialFile}";
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Choose an image",
                AllowMultiple = false,
                FileTypeFilter = [FilePickerFileTypes.ImageAll],
            }
        );

        if (files.Count > 0)
            await LoadAsync(files[0]);
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (result is null)
            return;

        var file = await StorageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = "Save pixel art",
                SuggestedFileName = $"{sourceName}_converted.png",
                DefaultExtension = "png",
                FileTypeChoices = [FilePickerFileTypes.ImagePng],
            }
        );

        if (file is null)
            return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            await result.SaveAsPngAsync(stream);

            StatusText.Text = $"Saved: {file.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save: {exception.Message}";
        }
    }

    private void OnDragOver(object? sender, DragEventArgs e) =>
        e.DragEffects = Dropped(e) is null ? DragDropEffects.None : DragDropEffects.Copy;

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (Dropped(e) is { } file)
            await LoadAsync(file);
    }

    private static IStorageFile? Dropped(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.OfType<IStorageFile>().FirstOrDefault();

    private async Task LoadAsync(IStorageFile file)
    {
        await using var stream = await file.OpenReadAsync();

        await LoadAsync(stream, file.Name);
    }

    private async void OnLayoutChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        await ConvertAsync();

    /// <summary>
    /// The whole open-and-convert path, driven by a stream so it can run without a picker.
    /// </summary>
    internal async Task LoadAsync(Stream stream, string name)
    {
        SetBusy(true);
        StatusText.Text = "Working…";

        try
        {
            var loaded = await SharpImage.LoadAsync<Rgba32>(stream);

            // Kept open: changing the tile size reconverts, and re-reading the stream is
            // not an option once the picker handed it over.
            source?.Dispose();
            source = loaded;

            SourceImage.Source = ToBitmap(source);
            SourceCaption.Text = $"Source — {source.Width}×{source.Height}";

            sourceName = Path.GetFileNameWithoutExtension(name);
            HeaderText.Text = name;
        }
        catch (Exception exception)
        {
            SetBusy(false);
            StatusText.Text = Describe(exception);
            return;
        }

        await ConvertAsync();
    }

    /// <summary>Converts the image already open, at whatever tile size is set now.</summary>
    /// <remarks>
    /// Every keystroke in a size box asks for a conversion. Disabling the boxes while one runs
    /// would pull the focus out from under the typing, so they stay live and a request that
    /// arrives mid-run is folded into a single re-run once the current one finishes.
    /// </remarks>
    internal Task ConvertAsync()
    {
        // Awaiting the call always means "settled", whether it started the work or joined it.
        // Only an unfinished conversion absorbs the request: a finished one is last time's,
        // still parked here because the assignment below lands after the call it stores.
        if (conversion is { IsCompleted: false } running)
        {
            queued = true;
            return running;
        }

        return conversion = DrainAsync();
    }

    private async Task DrainAsync()
    {
        do
        {
            queued = false;
            await ConvertOnceAsync();
        } while (queued);
    }

    private async Task ConvertOnceAsync()
    {
        if (source is not { } image)
            return;

        SetBusy(true);
        SaveButton.IsEnabled = false;
        StatusText.Text = "Working…";

        try
        {
            var options = new ConversionOptions(
                SheetTiles: (int?)SheetTilesInput.Value ?? 1,
                TileSize: (int?)TileSizeInput.Value
            );

            // Detection and voting take about a second on a large image; the UI thread
            // must not be the one doing it.
            var converted = await Task.Run(() => ImageConverter.ToPixelArt(image, options));

            result?.Dispose();
            result = converted;

            ResultImage.Source = ToBitmap(result);
            ResultCaption.Text = $"Pixel art — {result.Width}×{result.Height}";
            StatusText.Text = options.TileSize is int tile
                ? $"Sheet of {options.SheetTiles}×{options.SheetTiles} tiles at {tile}×{tile} — {result.Width}×{result.Height} in all, 8-colour palette."
                : $"Grid detected automatically: {result.Width}×{result.Height}, 8-colour palette.";

            SaveButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            StatusText.Text = Describe(exception);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        OpenButton.IsEnabled = !busy;
        Cursor = new Cursor(busy ? StandardCursorType.Wait : StandardCursorType.Arrow);
    }

    private static string Describe(Exception exception) =>
        exception switch
        {
            UnknownImageFormatException => "That is not an image — PNG, JPEG, BMP, GIF and WebP are supported.",
            InvalidOperationException => exception.Message,
            _ => $"Could not open: {exception.Message}",
        };

    private static Bitmap ToBitmap(PixelArt image)
    {
        using var stream = new MemoryStream();

        image.SaveAsPng(stream);
        stream.Position = 0;

        return new Bitmap(stream);
    }
}
