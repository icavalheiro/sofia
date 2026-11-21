#:package System.Drawing.Common@10.0.15

// Crop + magnify a region of a PNG for reviewing parity evidence.
// Usage: dotnet run .sofia/scripts/crop.cs -- <source.png> <x> <y> <w> <h> <scale> <out.png>
using System.Drawing;
using System.Drawing.Drawing2D;

if (args.Length < 7)
{
    Console.Error.WriteLine("usage: crop.cs <src> <x> <y> <w> <h> <scale> <out>");
    return 1;
}

using var source = Image.FromFile(args[0]);
var rect = new Rectangle(int.Parse(args[1]), int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]));
var scale = float.Parse(args[5]);
using var output = new Bitmap((int)(rect.Width * scale), (int)(rect.Height * scale));
using (var graphics = Graphics.FromImage(output))
{
    graphics.InterpolationMode = InterpolationMode.NearestNeighbor;
    graphics.PixelOffsetMode = PixelOffsetMode.Half;
    graphics.DrawImage(source, new Rectangle(0, 0, output.Width, output.Height), rect, GraphicsUnit.Pixel);
}
output.Save(args[6]);
return 0;
