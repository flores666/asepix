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
    private PixelArt? result;
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
            StatusText.Text = $"Файл не найден: {initialFile}";
    }

    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = "Выберите изображение",
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
                Title = "Сохранить пиксель-арт",
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

            StatusText.Text = $"Сохранено: {file.Name}";
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Не удалось сохранить: {exception.Message}";
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

    /// <summary>
    /// The whole open-and-convert path, driven by a stream so it can run without a picker.
    /// </summary>
    internal async Task LoadAsync(Stream stream, string name)
    {
        SetBusy(true);
        StatusText.Text = "Обработка…";

        try
        {
            using (var source = await SharpImage.LoadAsync<Rgba32>(stream))
            {
                SourceImage.Source = ToBitmap(source);
                SourceCaption.Text = $"Оригинал — {source.Width}×{source.Height}";

                // Detection and voting take about a second on a large image; the UI thread
                // must not be the one doing it.
                var converted = await Task.Run(
                    () => ImageConverter.ToPixelArt(source, new ConversionOptions())
                );

                result?.Dispose();
                result = converted;
            }

            ResultImage.Source = ToBitmap(result);
            ResultCaption.Text = $"Пиксель-арт — {result.Width}×{result.Height}";

            sourceName = Path.GetFileNameWithoutExtension(name);
            HeaderText.Text = name;
            StatusText.Text = $"Сетка определена автоматически: {result.Width}×{result.Height}, палитра 8 цветов.";

            SetBusy(false);
            SaveButton.IsEnabled = true;
        }
        catch (Exception exception)
        {
            SetBusy(false);
            StatusText.Text = Describe(exception);
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
            UnknownImageFormatException => "Это не изображение — поддерживаются PNG, JPEG, BMP, GIF, WebP.",
            InvalidOperationException => exception.Message,
            _ => $"Не удалось открыть: {exception.Message}",
        };

    private static Bitmap ToBitmap(PixelArt image)
    {
        using var stream = new MemoryStream();

        image.SaveAsPng(stream);
        stream.Position = 0;

        return new Bitmap(stream);
    }
}
