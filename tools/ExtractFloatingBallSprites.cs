using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

if (args.Length < 2) throw new ArgumentException("用法：ExtractFloatingBallSprites <源图> <皮肤资源根目录>");

var sourcePath = Path.GetFullPath(args[0]);
var skinRoot = Path.GetFullPath(args[1]);
using var source = new Bitmap(sourcePath);
const int sourceColumns = 8;
const int sourceRows = 6;
const int frameSize = 181;
const int safetyMargin = 14;
const int effectRadius = 38;

if (source.Width != sourceColumns * frameSize || source.Height != sourceRows * frameSize)
    throw new InvalidDataException($"素材尺寸必须为 {sourceColumns * frameSize}×{sourceRows * frameSize}，当前为 {source.Width}×{source.Height}。");

static bool IsBackgroundCandidate(Color color)
{
    var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
    var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
    return minimum >= 190 && maximum - minimum <= 22;
}

var background = new bool[source.Width, source.Height];
var queue = new Queue<Point>();
void EnqueueBackground(int x, int y)
{
    if (x < 0 || y < 0 || x >= source.Width || y >= source.Height || background[x, y] || !IsBackgroundCandidate(source.GetPixel(x, y))) return;
    background[x, y] = true;
    queue.Enqueue(new Point(x, y));
}

for (var x = 0; x < source.Width; x++) { EnqueueBackground(x, 0); EnqueueBackground(x, source.Height - 1); }
for (var y = 0; y < source.Height; y++) { EnqueueBackground(0, y); EnqueueBackground(source.Width - 1, y); }
var fourDirections = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1) };
while (queue.Count > 0)
{
    var point = queue.Dequeue();
    foreach (var direction in fourDirections) EnqueueBackground(point.X + direction.X, point.Y + direction.Y);
}

var components = FindComponents(background, source.Width, source.Height);
var skins = new[]
{
    (Id: "BlueWhaleMaid", StartRow: 0),
    (Id: "StarSailor", StartRow: 2),
    (Id: "YongWeiXiaoFei", StartRow: 4)
};

// 状态顺序：Idle, Sleeping, Happy, Curious, Thinking, Listening,
// Working, Surprised, Warning, Error, Dragging, Expanding。
// 原图上排 8 个状态；下排使用愤怒、哭泣、拖动、欢呼四个状态。
var sourceAnchors = new[]
{
    (Column: 0, RowOffset: 0), (Column: 1, RowOffset: 0), (Column: 2, RowOffset: 0), (Column: 3, RowOffset: 0),
    (Column: 4, RowOffset: 0), (Column: 5, RowOffset: 0), (Column: 6, RowOffset: 0), (Column: 7, RowOffset: 0),
    (Column: 0, RowOffset: 1), (Column: 1, RowOffset: 1), (Column: 5, RowOffset: 1), (Column: 6, RowOffset: 1)
};

foreach (var skin in skins)
{
    var directory = Path.Combine(skinRoot, skin.Id);
    Directory.CreateDirectory(directory);
    using var spriteSheet = CreateSheet(source, background, components, skin.StartRow, sourceAnchors, 4);
    using var idleSheet = CreateSheet(source, background, components, skin.StartRow, sourceAnchors[..4], 4);
    spriteSheet.Save(Path.Combine(directory, "companion-sprite-sheet.png"), ImageFormat.Png);
    idleSheet.Save(Path.Combine(directory, "companion-idle-variants.png"), ImageFormat.Png);
}

static List<Component> FindComponents(bool[,] background, int width, int height)
{
    var seen = new bool[width, height];
    var directions = new[] { new Point(1, 0), new Point(-1, 0), new Point(0, 1), new Point(0, -1), new Point(1, 1), new Point(1, -1), new Point(-1, 1), new Point(-1, -1) };
    var components = new List<Component>();
    for (var y = 0; y < height; y++) for (var x = 0; x < width; x++)
    {
        if (background[x, y] || seen[x, y]) continue;
        var queue = new Queue<Point>();
        queue.Enqueue(new Point(x, y));
        seen[x, y] = true;
        var pixels = new List<Point>();
        var minX = x; var maxX = x; var minY = y; var maxY = y;
        while (queue.Count > 0)
        {
            var point = queue.Dequeue(); pixels.Add(point);
            minX = Math.Min(minX, point.X); maxX = Math.Max(maxX, point.X);
            minY = Math.Min(minY, point.Y); maxY = Math.Max(maxY, point.Y);
            foreach (var direction in directions)
            {
                var nextX = point.X + direction.X; var nextY = point.Y + direction.Y;
                if (nextX < 0 || nextY < 0 || nextX >= width || nextY >= height || seen[nextX, nextY] || background[nextX, nextY]) continue;
                seen[nextX, nextY] = true; queue.Enqueue(new Point(nextX, nextY));
            }
        }
        if (pixels.Count >= 80) components.Add(new Component(pixels, new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1)));
    }
    return components;
}

static Bitmap CreateSheet(Bitmap source, bool[,] background, List<Component> components, int startRow, (int Column, int RowOffset)[] anchors, int columns)
{
    var rows = (int)Math.Ceiling(anchors.Length / (double)columns);
    var sheet = new Bitmap(frameSize * columns, frameSize * rows, PixelFormat.Format32bppArgb);
    using var canvas = Graphics.FromImage(sheet);
    canvas.Clear(Color.Transparent);
    canvas.CompositingMode = CompositingMode.SourceCopy;
    for (var index = 0; index < anchors.Length; index++)
    {
        var anchor = anchors[index];
        var center = new Point(anchor.Column * frameSize + frameSize / 2, (startRow + anchor.RowOffset) * frameSize + frameSize / 2);
        var main = components
            .OrderBy(component => DistanceSquared(component.Bounds, center))
            .ThenByDescending(component => component.Pixels.Count)
            .First(component => DistanceSquared(component.Bounds, center) < 110 * 110);
        var crop = Rectangle.Inflate(main.Bounds, effectRadius, effectRadius);
        crop.Intersect(new Rectangle(Point.Empty, source.Size));
        var allowedPixels = new HashSet<Point>(main.Pixels);
        foreach (var component in components)
        {
            if (ReferenceEquals(component, main) || component.Pixels.Count > 2000 || !component.Bounds.IntersectsWith(crop)) continue;
            foreach (var pixel in component.Pixels) allowedPixels.Add(pixel);
        }
        using var cutout = new Bitmap(crop.Width, crop.Height, PixelFormat.Format32bppArgb);
        for (var y = crop.Top; y < crop.Bottom; y++) for (var x = crop.Left; x < crop.Right; x++)
        {
            var pixel = source.GetPixel(x, y);
            if (background[x, y] || !allowedPixels.Contains(new Point(x, y))) continue;
            cutout.SetPixel(x - crop.Left, y - crop.Top, Color.FromArgb(255, pixel.R, pixel.G, pixel.B));
        }

        var bounds = AlphaBounds(cutout);
        if (bounds.IsEmpty) throw new InvalidDataException($"无法提取状态 {index}。");
        var maxContent = frameSize - safetyMargin * 2;
        var scale = Math.Min((double)maxContent / bounds.Width, (double)maxContent / bounds.Height);
        var drawWidth = Math.Max(1, (int)Math.Round(bounds.Width * scale));
        var drawHeight = Math.Max(1, (int)Math.Round(bounds.Height * scale));
        var destination = new Rectangle((index % columns) * frameSize + (frameSize - drawWidth) / 2, (index / columns) * frameSize + (frameSize - drawHeight) / 2, drawWidth, drawHeight);
        canvas.InterpolationMode = InterpolationMode.HighQualityBicubic;
        canvas.PixelOffsetMode = PixelOffsetMode.HighQuality;
        canvas.DrawImage(cutout, destination, bounds, GraphicsUnit.Pixel);
    }
    return sheet;
}

static Rectangle AlphaBounds(Bitmap bitmap)
{
    var bounds = Rectangle.Empty;
    for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++)
    {
        if (bitmap.GetPixel(x, y).A < 16) continue;
        bounds = bounds.IsEmpty ? new Rectangle(x, y, 1, 1) : Rectangle.Union(bounds, new Rectangle(x, y, 1, 1));
    }
    return bounds;
}

static int DistanceSquared(Rectangle bounds, Point point)
{
    var dx = point.X < bounds.Left ? bounds.Left - point.X : point.X > bounds.Right ? point.X - bounds.Right : 0;
    var dy = point.Y < bounds.Top ? bounds.Top - point.Y : point.Y > bounds.Bottom ? point.Y - bounds.Bottom : 0;
    return dx * dx + dy * dy;
}

record Component(List<Point> Pixels, Rectangle Bounds);
