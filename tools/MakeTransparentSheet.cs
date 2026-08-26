using System;
using System.Drawing;
using System.Drawing.Imaging;

if (args.Length != 2) return;
using var source = new Bitmap(args[0]);
using var target = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
var rect = new Rectangle(0, 0, source.Width, source.Height);
var src = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
var dst = target.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
unsafe {
  for (var y = 0; y < source.Height; y++) {
    var s = (byte*)src.Scan0 + y * src.Stride;
    var d = (byte*)dst.Scan0 + y * dst.Stride;
    for (var x = 0; x < source.Width; x++) {
      var b = s[x * 4]; var g = s[x * 4 + 1]; var r = s[x * 4 + 2];
      var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
      var avg = (r + g + b) / 3;
      var transparent = max - min <= 7 && avg >= 218;
      d[x * 4] = b; d[x * 4 + 1] = g; d[x * 4 + 2] = r; d[x * 4 + 3] = (byte)(transparent ? 0 : 255);
    }
  }
}
source.UnlockBits(src); target.UnlockBits(dst);
Directory.CreateDirectory(System.IO.Path.GetDirectoryName(args[1])!);
target.Save(args[1], ImageFormat.Png);
