using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length != 5) return;
var sources = new[] { new Bitmap(args[0]), new Bitmap(args[1]), new Bitmap(args[2]), new Bitmap(args[3]) };
var outputPath = args[4];
const int columns = 4, rows = 3, cellW = 362, cellH = 362;
using var output = new Bitmap(cellW * columns, cellH * rows, PixelFormat.Format32bppArgb);
using var canvas = Graphics.FromImage(output);
canvas.Clear(Color.Transparent);
canvas.CompositingMode = CompositingMode.SourceCopy;
canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
// 状态顺序严格对应 CompanionVisualState；仅复用用户提供的四张原始素材。
var stateSource = new[] { 2, 0, 1, 1, 2, 2, 3, 3, 0, 0, 2, 3 };
for (var i = 0; i < stateSource.Length; i++) {
    var source = sources[stateSource[i]];
    var bounds = FindAlphaBounds(source);
    if (bounds.IsEmpty) continue;
    var margin = 18;
    var scale = Math.Min((double)(cellW - margin * 2) / bounds.Width, (double)(cellH - margin * 2) / bounds.Height);
    var drawW = (int)Math.Round(bounds.Width * scale);
    var drawH = (int)Math.Round(bounds.Height * scale);
    var cellX = (i % columns) * cellW;
    var cellY = (i / columns) * cellH;
    var dest = new Rectangle(cellX + (cellW - drawW) / 2, cellY + (cellH - drawH) / 2, drawW, drawH);
    canvas.DrawImage(source, dest, bounds, GraphicsUnit.Pixel);
}
foreach (var source in sources) source.Dispose();
output.Save(outputPath, ImageFormat.Png);

static Rectangle FindAlphaBounds(Bitmap bitmap) {
    var bounds = Rectangle.Empty;
    for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) {
        if (bitmap.GetPixel(x, y).A < 8) continue;
        var pixel = new Rectangle(x, y, 1, 1);
        bounds = bounds.IsEmpty ? pixel : Rectangle.Union(bounds, pixel);
    }
    return bounds;
}
