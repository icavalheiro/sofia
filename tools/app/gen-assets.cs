// Rasterises the web art for the Avalonia app (spec 039): copies the three logo PNGs and turns the chat wallpaper SVG
// into a 400x400 transparent tile. The paper grain in ChatMessages.vue is an feTurbulence fractal-noise SVG at 2%
// opacity; here it is regenerated procedurally as a 140x140 tile with the same visual weight (documented deviation).
//
// Run from the repository root: dotnet run tools/app/gen-assets.cs

#:package Svg.Skia@5.2.3

using SkiaSharp;
using Svg.Skia;

var root = Directory.GetCurrentDirectory();
var assets = Path.Combine(root, "src", "Sofia.App", "Assets");
Directory.CreateDirectory(Path.Combine(assets, "Logos"));
Directory.CreateDirectory(Path.Combine(assets, "Wallpaper"));

// --- logos ----------------------------------------------------------------------------------------------

foreach (var (source, target) in new[]
         {
             (Path.Combine(root, "brand", "logo_42.png"), "Logos/logo-42.png"),
             (Path.Combine(root, "brand", "logo_128.png"), "Logos/logo-128.png"),
             (Path.Combine(root, "brand", "logo_31.png"), "Logos/logo-31.png"),
         })
{
    File.Copy(source, Path.Combine(assets, target), overwrite: true);
    Console.WriteLine($"{target} ({new FileInfo(source).Length} bytes)");
}

// --- wallpaper tile -------------------------------------------------------------------------------------

var svgPath = Path.Combine(root, "src", "Sofia.UI", "src", "assets", "chat-wallpaper.svg");
using (var svg = new SKSvg())
{
    svg.Load(svgPath);
    var picture = svg.Picture ?? throw new InvalidOperationException("wallpaper SVG did not load");
    using var surface = SKSurface.Create(new SKImageInfo(400, 400, SKColorType.Rgba8888, SKAlphaType.Premul));
    surface.Canvas.Clear(SKColors.Transparent);
    surface.Canvas.DrawPicture(picture);
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(Path.Combine(assets, "Wallpaper", "chat-wallpaper.png"));
    data.SaveTo(file);
    Console.WriteLine($"Wallpaper/chat-wallpaper.png ({data.Size} bytes)");
}

// --- paper grain ----------------------------------------------------------------------------------------

// feTurbulence(baseFrequency .85, numOctaves 2) at 2% opacity: two octaves of value noise, white, alpha <= 5/255.
static double Hash(int x, int y, int seed)
{
    var h = (uint)(x * 374761393 + y * 668265263 + seed * 1442695040888963407);
    h = (h ^ (h >> 13)) * 1274126177;
    return ((h ^ (h >> 16)) & 0xFFFF) / 65535.0;
}

static double ValueNoise(double x, double y, int seed)
{
    var xi = (int)Math.Floor(x);
    var yi = (int)Math.Floor(y);
    var xf = x - xi;
    var yf = y - yi;
    double Smooth(double t) => t * t * (3 - 2 * t);
    var sx = Smooth(xf);
    var sy = Smooth(yf);
    var a = Hash(xi, yi, seed);
    var b = Hash(xi + 1, yi, seed);
    var c = Hash(xi, yi + 1, seed);
    var d = Hash(xi + 1, yi + 1, seed);
    return (a + (b - a) * sx) + ((c + (d - c) * sx) - (a + (b - a) * sx)) * sy;
}

using (var surface = SKSurface.Create(new SKImageInfo(140, 140, SKColorType.Rgba8888, SKAlphaType.Premul)))
{
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    for (var y = 0; y < 140; y++)
    for (var x = 0; x < 140; x++)
    {
        var noise = ValueNoise(x * 0.85, y * 0.85, 1) * 0.62 + ValueNoise(x * 1.7, y * 1.7, 2) * 0.38;
        var alpha = (byte)Math.Clamp((int)(noise * 0.02 * 255), 0, 8);
        canvas.DrawPoint(x, y, new SKColor(255, 255, 255, alpha));
    }
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(Path.Combine(assets, "Wallpaper", "grain.png"));
    data.SaveTo(file);
    Console.WriteLine($"Wallpaper/grain.png ({data.Size} bytes)");
}
