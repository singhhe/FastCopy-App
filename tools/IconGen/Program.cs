using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

string outDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
    "src", "FastCopy.App", "Assets"));
Directory.CreateDirectory(outDir);

int[] icoSizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };
var pngs = new byte[icoSizes.Length][];
for (int i = 0; i < icoSizes.Length; i++)
    pngs[i] = RenderIconPng(icoSizes[i]);

File.WriteAllBytes(Path.Combine(outDir, "Logo.png"), RenderIconPng(1024));
WriteIco(Path.Combine(outDir, "AppIcon.ico"), icoSizes, pngs);

Console.WriteLine($"Wrote AppIcon.ico ({icoSizes.Length} sizes) and Logo.png to {outDir}");

static byte[] RenderIconPng(int size)
{
    var visual = new DrawingVisual();
    using (DrawingContext dc = visual.RenderOpen())
    {
        double scale = size / 256.0;
        dc.PushTransform(new ScaleTransform(scale, scale));

        // Tile: the app's accent blue, as a subtle diagonal gradient for a touch of depth.
        var tileBrush = new LinearGradientBrush(
            Color.FromRgb(0x22, 0x86, 0xD6), Color.FromRgb(0x0B, 0x5A, 0x9E),
            new Point(0, 0), new Point(1, 1));
        dc.DrawGeometry(tileBrush, null, new RectangleGeometry(new Rect(0, 0, 256, 256), 52, 52));

        // The page: a document silhouette with a folded top-right corner (the universal "file" mark).
        var pageRect = new Rect(94, 58, 98, 150);
        const double fold = 28;
        var pageGeometry = new RectangleGeometry(pageRect, 10, 10);

        var cutCorner = new StreamGeometry();
        using (StreamGeometryContext ctx = cutCorner.Open())
        {
            ctx.BeginFigure(new Point(pageRect.Right - fold, pageRect.Top), true, true);
            ctx.LineTo(new Point(pageRect.Right + 1, pageRect.Top), true, false);
            ctx.LineTo(new Point(pageRect.Right + 1, pageRect.Top + fold), true, false);
            ctx.LineTo(new Point(pageRect.Right - fold, pageRect.Top), true, false);
        }

        dc.DrawGeometry(Brushes.White, null, new CombinedGeometry(GeometryCombineMode.Exclude, pageGeometry, cutCorner));

        // Folded flap: a small tinted triangle suggesting the corner has folded down onto the tile.
        var flap = new StreamGeometry();
        using (StreamGeometryContext ctx = flap.Open())
        {
            ctx.BeginFigure(new Point(pageRect.Right - fold, pageRect.Top), true, true);
            ctx.LineTo(new Point(pageRect.Right, pageRect.Top + fold), true, false);
            ctx.LineTo(new Point(pageRect.Right - fold, pageRect.Top + fold), true, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(110, 0x0B, 0x5A, 0x9E)), null, flap);

        // Speed lines: a converging trail reads as "fast" and echoes the app's own play-triangle
        // language (Start/Resume) without resorting to a generic lightning-bolt cliche.
        DrawSpeedLine(dc, 96, 62, 90, 28, 200);
        DrawSpeedLine(dc, 133, 40, 90, 50, 255);
        DrawSpeedLine(dc, 170, 62, 90, 28, 200);

        dc.Pop();
    }

    var rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    rtb.Render(visual);

    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(rtb));
    using var ms = new MemoryStream();
    encoder.Save(ms);
    return ms.ToArray();
}

static void DrawSpeedLine(DrawingContext dc, double y, double startX, double endX, double length, byte alpha)
{
    var pen = new Pen(new SolidColorBrush(Color.FromArgb(alpha, 255, 255, 255)), 16)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round,
    };
    dc.DrawLine(pen, new Point(endX - length, y), new Point(endX, y));
}

static void WriteIco(string path, int[] sizes, byte[][] pngData)
{
    using var fs = new FileStream(path, FileMode.Create);
    using var bw = new BinaryWriter(fs);

    bw.Write((short)0);
    bw.Write((short)1);
    bw.Write((short)sizes.Length);

    int offset = 6 + 16 * sizes.Length;
    for (int i = 0; i < sizes.Length; i++)
    {
        int sz = sizes[i];
        bw.Write((byte)(sz >= 256 ? 0 : sz));
        bw.Write((byte)(sz >= 256 ? 0 : sz));
        bw.Write((byte)0);
        bw.Write((byte)0);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(pngData[i].Length);
        bw.Write(offset);
        offset += pngData[i].Length;
    }

    foreach (byte[] p in pngData)
        bw.Write(p);
}
