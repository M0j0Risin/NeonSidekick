#:package Svg.Skia@5.2.3

// IconGen: renders assets/icon.svg to the multi-size .ico the csproj embeds (<ApplicationIcon>).
//
// A .NET 10 file-based app: no csproj, not in the solution, not run by build.ps1. The .ico is
// committed; run this only when the SVG changes, from the repository root:
//
//     dotnet run tools/IconGen.cs                       # writes src/NeonSidekick/Assets/app.ico
//     dotnet run tools/IconGen.cs -- --preview <dir>    # also 256 / 32 / 16 px PNGs for eyeballing
//     dotnet run tools/IconGen.cs -- --svg <icon.svg> --out <dir>
//     dotnet run tools/IconGen.cs -- --web              # the Web UI's icons (2026-10-15): assets/webui/icon-192.png, icon-512.png,
//                                                       # apple-touch-icon.png (180), on the page's ground, the picture at 80%
//
// Svg.Skia pins its own SkiaSharp version; do not add a separate SkiaSharp package.
// Frames are PNG-compressed, valid inside a .ico at every size since Windows Vista.

using System.Buffers.Binary;
using SkiaSharp;
using Svg.Skia;

int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];

var svgPath = Arg("--svg") ?? "assets/icon.svg";
var outDir = Arg("--out") ?? "src/NeonSidekick/Assets";
var previewDir = Arg("--preview");
bool web = args.Contains("--web");

if (!File.Exists(svgPath))
{
    Console.Error.WriteLine($"SVG not found: {svgPath} (run from the repository root)");
    return 1;
}

using var svg = new SKSvg();
if (svg.Load(svgPath) is not { } picture)
{
    Console.Error.WriteLine($"Could not parse SVG: {svgPath}");
    return 1;
}

if (web)
{
    // Add to Home Screen (2026-10-15): an opaque square (iOS fills a transparent icon with black), the page's own dark ground.
    var ground = new SKColor(0x12, 0x0b, 0x1f);
    foreach (var (name, size) in new[] { ("icon-192.png", 192), ("icon-512.png", 512), ("apple-touch-icon.png", 180) })
    {
        string path = Path.Combine("assets", "webui", name);
        File.WriteAllBytes(path, Render(picture, size, ground, 0.8f));
        Console.WriteLine($"Wrote {Path.GetFullPath(path)}");
    }

    return 0;
}

Directory.CreateDirectory(outDir);
var frames = sizes.Select(s => (Size: s, Png: Render(picture, s))).ToList();
var icoPath = Path.Combine(outDir, "app.ico");
File.WriteAllBytes(icoPath, BuildIco(frames));
Console.WriteLine($"Wrote {Path.GetFullPath(icoPath)} ({frames.Count} frames, {new FileInfo(icoPath).Length:N0} bytes)");

if (previewDir is not null)
{
    Directory.CreateDirectory(previewDir);
    foreach (var (size, png) in frames.Where(f => f.Size is 256 or 32 or 16))
    {
        File.WriteAllBytes(Path.Combine(previewDir, $"app-{size}.png"), png);
    }

    Console.WriteLine($"Previews written to {Path.GetFullPath(previewDir)}");
}

return 0;

string? Arg(string name)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
}

static byte[] Render(SKPicture picture, int size, SKColor? ground = null, float fill = 1f)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(ground ?? SKColors.Transparent);

    var bounds = picture.CullRect;
    var scale = Math.Min(size / bounds.Width, size / bounds.Height) * fill;
    canvas.Translate((size - bounds.Width * scale) / 2, (size - bounds.Height * scale) / 2);
    canvas.Scale(scale);
    canvas.Translate(-bounds.Left, -bounds.Top);
    canvas.DrawPicture(picture);
    canvas.Flush();

    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    return data.ToArray();
}

// ICONDIR (6 bytes) + one ICONDIRENTRY (16 bytes) per frame + the PNG payloads.
static byte[] BuildIco(IReadOnlyList<(int Size, byte[] Png)> frames)
{
    const int headerSize = 6;
    const int entrySize = 16;

    var dataOffset = headerSize + (entrySize * frames.Count);
    var buffer = new byte[dataOffset + frames.Sum(f => f.Png.Length)];
    var span = buffer.AsSpan();

    BinaryPrimitives.WriteUInt16LittleEndian(span[0..], 0); // reserved
    BinaryPrimitives.WriteUInt16LittleEndian(span[2..], 1); // 1 = icon
    BinaryPrimitives.WriteUInt16LittleEndian(span[4..], (ushort)frames.Count);

    var offset = dataOffset;
    for (var i = 0; i < frames.Count; i++)
    {
        var (size, png) = frames[i];
        var entry = span[(headerSize + (i * entrySize))..];
        entry[0] = (byte)(size >= 256 ? 0 : size); // 0 means 256
        entry[1] = (byte)(size >= 256 ? 0 : size);
        entry[2] = 0; // colour count (0 = no palette)
        entry[3] = 0; // reserved
        BinaryPrimitives.WriteUInt16LittleEndian(entry[4..], 1);  // planes
        BinaryPrimitives.WriteUInt16LittleEndian(entry[6..], 32); // bits per pixel
        BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)png.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)offset);

        png.CopyTo(span[offset..]);
        offset += png.Length;
    }

    return buffer;
}
