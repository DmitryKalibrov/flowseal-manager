using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 2)
{
    throw new ArgumentException("Укажите пути для ICO и PNG.");
}

var iconPath = Path.GetFullPath(args[0]);
var previewPath = Path.GetFullPath(args[1]);
Directory.CreateDirectory(Path.GetDirectoryName(iconPath)!);
Directory.CreateDirectory(Path.GetDirectoryName(previewPath)!);

var sizes = new[] { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var images = sizes.Select(RenderPng).ToArray();

using (var stream = File.Create(iconPath))
using (var writer = new BinaryWriter(stream))
{
    writer.Write((ushort)0);
    writer.Write((ushort)1);
    writer.Write((ushort)images.Length);

    var offset = 6 + (16 * images.Length);
    for (var index = 0; index < images.Length; index++)
    {
        var size = sizes[index];
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)(size == 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)1);
        writer.Write((ushort)32);
        writer.Write(images[index].Length);
        writer.Write(offset);
        offset += images[index].Length;
    }

    foreach (var image in images)
    {
        writer.Write(image);
    }
}

File.WriteAllBytes(previewPath, images[^1]);
Console.WriteLine(iconPath);

static byte[] RenderPng(int size)
{
    using var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var graphics = Graphics.FromImage(bitmap);
    graphics.Clear(Color.Transparent);
    graphics.SmoothingMode = size >= 48 ? SmoothingMode.AntiAlias : SmoothingMode.None;
    graphics.PixelOffsetMode = PixelOffsetMode.Half;

    var inset = Math.Max(1, size / 32f);
    var radius = Math.Max(2, size * 0.14f);
    using var tilePath = RoundedRectangle(new RectangleF(inset, inset, size - (2 * inset), size - (2 * inset)), radius);
    using var blue = new SolidBrush(Color.FromArgb(255, 20, 61, 255));
    graphics.FillPath(blue, tilePath);

    if (size >= 32)
    {
        using var gridPen = new Pen(Color.FromArgb(46, 255, 255, 255), Math.Max(1, size / 128f));
        for (var line = 1; line < 8; line++)
        {
            var coordinate = line * size / 8f;
            graphics.DrawLine(gridPen, coordinate, inset, coordinate, size - inset);
            graphics.DrawLine(gridPen, inset, coordinate, size - inset, coordinate);
        }
    }

    var pixels = new[]
    {
        (2, 2), (3, 2), (4, 2), (5, 2),
        (2, 3),
        (2, 4), (3, 4), (4, 4),
        (2, 5), (2, 6),
        (5, 5), (6, 6)
    };
    var cell = size / 8f;
    var gap = Math.Max(0.5f, cell * 0.14f);
    using var white = new SolidBrush(Color.FromArgb(255, 255, 253, 252));
    using var ice = new SolidBrush(Color.FromArgb(255, 189, 215, 244));
    for (var index = 0; index < pixels.Length; index++)
    {
        var (column, row) = pixels[index];
        var brush = index >= pixels.Length - 2 ? ice : white;
        graphics.FillRectangle(
            brush,
            (column * cell) + gap,
            (row * cell) + gap,
            cell - (2 * gap),
            cell - (2 * gap));
    }

    using var memory = new MemoryStream();
    bitmap.Save(memory, ImageFormat.Png);
    return memory.ToArray();
}

static GraphicsPath RoundedRectangle(RectangleF rectangle, float radius)
{
    var diameter = radius * 2;
    var path = new GraphicsPath();
    path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
    path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
    path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
    path.CloseFigure();
    return path;
}
