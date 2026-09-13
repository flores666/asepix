using asepix;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

if (args.Length == 0 || args[0] is "-h" or "--help")
{
    Console.WriteLine(
        """
        asepix — converts AI-generated pixel-art-like images into true pixel art.

          asepix <input> [options]

          -o, --output <path>  Output file (default: <input>_converted.png)
              --grid <W>x<H>   Output grid size; detected from the image when omitted
              --colors <n>     Palette size, 2-256 (default: 8)
              --inset <f>      Cell fraction trimmed before voting, 0-0.49 (default: 0.25)
        """
    );

    return args.Length == 0 ? 1 : 0;
}

string input = args[0],
    output = DefaultOutput(input);

var options = new ConversionOptions();

try
{
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "-o" or "--output":
                output = Next(args, ref i);
                break;
            case "--grid":
                var (gridWidth, gridHeight) = ParseGrid(Next(args, ref i));
                options = options with { GridWidth = gridWidth, GridHeight = gridHeight };
                break;
            case "--colors":
                options = options with { Colors = int.Parse(Next(args, ref i)) };
                break;
            case "--inset":
                options = options with { Inset = double.Parse(Next(args, ref i)) };
                break;
            default:
                throw new ArgumentException($"Unknown option \"{args[i]}\".");
        }
    }

    options.Validate();
}
catch (Exception e) when (e is ArgumentException or FormatException)
{
    Console.Error.WriteLine($"asepix: {e.Message}");
    return 1;
}

try
{
    using var source = await Image.LoadAsync<Rgba32>(input);
    using var converted = ImageConverter.ToPixelArt(source, options);

    await converted.SaveAsPngAsync(output);

    Console.WriteLine($"{source.Width}x{source.Height} -> {converted.Width}x{converted.Height}  {output}");
    return 0;
}
catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException
    or UnknownImageFormatException or InvalidOperationException)
{
    Console.Error.WriteLine($"asepix: {e.Message}");
    return 1;
}

static string Next(string[] args, ref int i) =>
    ++i < args.Length ? args[i] : throw new ArgumentException($"Option \"{args[i - 1]}\" needs a value.");

static string DefaultOutput(string input) =>
    Path.Combine(
        Path.GetDirectoryName(input) ?? string.Empty,
        Path.GetFileNameWithoutExtension(input) + "_converted.png"
    );

static (int Width, int Height) ParseGrid(string value)
{
    var parts = value.Split('x', 'X');

    if (parts.Length != 2 || !int.TryParse(parts[0], out var w) || !int.TryParse(parts[1], out var h))
        throw new ArgumentException($"Grid must look like 64x64, got \"{value}\".");

    return (w, h);
}
