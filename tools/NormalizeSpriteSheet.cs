using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 2) return;
using var source = new Bitmap(args[0]);
var columns = args.Length > 2 ? int.Parse(args[2]) : 4;
var rows = args.Length > 3 ? int.Parse(args[3]) : 3;
var cellW = source.Width / columns; var cellH = source.Height / rows;
using var output = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
using var canvas = Graphics.FromImage(output);
canvas.Clear(Color.Transparent);
canvas.CompositingMode = CompositingMode.SourceCopy;
canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
for (var row = 0; row < rows; row++) for (var col = 0; col < columns; col++) {
  var cell = new Rectangle(col * cellW, row * cellH, cellW, cellH);
  var bounds = Rectangle.Empty;
  for (var y = cell.Top; y < cell.Bottom; y++) for (var x = cell.Left; x < cell.Right; x++) {
    if (source.GetPixel(x, y).A < 8) continue;
    var p = new Rectangle(x, y, 1, 1); bounds = bounds.IsEmpty ? p : Rectangle.Union(bounds, p);
  }
  if (bounds.IsEmpty) continue;
  var margin = 18; var maxW = cellW - margin * 2; var maxH = cellH - margin * 2;
  var scale = Math.Min((double)maxW / bounds.Width, (double)maxH / bounds.Height);
  var drawW = (int)Math.Round(bounds.Width * scale); var drawH = (int)Math.Round(bounds.Height * scale);
  var dest = new Rectangle(cell.Left + (cellW - drawW) / 2, cell.Top + (cellH - drawH) / 2, drawW, drawH);
  canvas.DrawImage(source, dest, bounds, GraphicsUnit.Pixel);
}
output.Save(args[1], ImageFormat.Png);
