using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;

if (args.Length != 2) throw new ArgumentException("用法：NormalizeSkinCompanionAssets <皮肤目录> <输出目录>");

var sourceDirectory = Path.GetFullPath(args[0]);
var outputDirectory = Path.GetFullPath(args[1]);
var states = new[]
{
    "Idle", "Sleeping", "Happy", "Curious", "Thinking", "Listening",
    "Working", "Surprised", "Warning", "Error", "Dragging", "Expanding"
};
const int frameSize = 181;
const int columns = 4;
const int rows = 3;
const int margin = 18;

var loadedSourceFrames = LoadSourceFrames(sourceDirectory, states, frameSize, columns);
// Clone source pixels before writing in-place to <skin>/States. Keeping a Bitmap
// backed by the destination file open makes GDI+ fail nondeterministically.
var sourceFrames = loadedSourceFrames.Select(frame => new Bitmap(frame)).ToList();
foreach (var frame in loadedSourceFrames) frame.Dispose();
try
{
    var sourceBounds = sourceFrames.Select(FindAlphaBounds).ToArray();
    if (sourceBounds.Any(bounds => bounds.IsEmpty))
        throw new InvalidDataException("状态素材没有透明主体。");

    // 每个状态都以同一目标包围盒归一化，避免姿态切换时角色忽大忽小。
    var targetContentSize = frameSize - margin * 2;

    Directory.CreateDirectory(outputDirectory);
    var statesDirectory = Path.Combine(outputDirectory, "States");
    Directory.CreateDirectory(statesDirectory);

    var normalizedFrames = new List<Bitmap>(states.Length);
    try
    {
        for (var index = 0; index < states.Length; index++)
        {
            var normalized = new Bitmap(frameSize, frameSize, PixelFormat.Format32bppArgb);
            using (var graphics = CreateGraphics(normalized))
                DrawCentered(graphics, sourceFrames[index], sourceBounds[index],
                    new Rectangle(0, 0, frameSize, frameSize),
                    Math.Min((double)targetContentSize / sourceBounds[index].Width,
                             (double)targetContentSize / sourceBounds[index].Height));
            normalized.Save(Path.Combine(statesDirectory, states[index] + ".png"), ImageFormat.Png);
            normalizedFrames.Add(normalized);
        }

        var anchors = states.Select((state, index) =>
        {
            var bounds = FindAlphaBounds(normalizedFrames[index]);
            return new
            {
                state,
                left = bounds.Left,
                top = bounds.Top,
                width = bounds.Width,
                height = bounds.Height,
                centerX = Math.Round(bounds.Left + bounds.Width / 2d, 2),
                centerY = Math.Round(bounds.Top + bounds.Height / 2d, 2)
            };
        });
        File.WriteAllText(
            Path.Combine(outputDirectory, "anchor.json"),
            JsonSerializer.Serialize(anchors, new JsonSerializerOptions { WriteIndented = true }));

        // 精灵表只作为旧版本兼容和素材审阅入口；运行时使用 States 下的独立图片。
        using var sheet = new Bitmap(frameSize * columns, frameSize * rows, PixelFormat.Format32bppArgb);
        using var idleSheet = new Bitmap(frameSize * columns, frameSize, PixelFormat.Format32bppArgb);
        using (var sheetGraphics = CreateGraphics(sheet))
        using (var idleGraphics = CreateGraphics(idleSheet))
        {
            for (var index = 0; index < normalizedFrames.Count; index++)
            {
                sheetGraphics.DrawImageUnscaled(normalizedFrames[index], (index % columns) * frameSize, (index / columns) * frameSize);
                if (index < columns)
                    idleGraphics.DrawImageUnscaled(normalizedFrames[index], index * frameSize, 0);
            }
        }
        sheet.Save(Path.Combine(outputDirectory, "companion-sprite-sheet.png"), ImageFormat.Png);
        idleSheet.Save(Path.Combine(outputDirectory, "companion-idle-variants.png"), ImageFormat.Png);

        var idleBounds = FindAlphaBounds(normalizedFrames[0]);
        using var portrait = new Bitmap(512, 512, PixelFormat.Format32bppArgb);
        using (var portraitGraphics = CreateGraphics(portrait))
        {
            var portraitScale = Math.Min(456d / idleBounds.Width, 456d / idleBounds.Height);
            DrawCentered(portraitGraphics, normalizedFrames[0], idleBounds,
                new Rectangle(0, 0, 512, 512), portraitScale);
        }
        foreach (var name in new[] { "portrait.png", "companion.png", "preview.png", "app-icon-source.png" })
            portrait.Save(Path.Combine(outputDirectory, name), ImageFormat.Png);
    }
    finally
    {
        foreach (var frame in normalizedFrames) frame.Dispose();
    }
}
finally
{
    foreach (var frame in sourceFrames) frame.Dispose();
}

static List<Bitmap> LoadSourceFrames(string sourceDirectory, IReadOnlyList<string> states, int frameSize, int columns)
{
    var rootStatePaths = states.Select(state => Path.Combine(sourceDirectory, state + ".png")).ToArray();
    if (rootStatePaths.All(File.Exists))
        return rootStatePaths.Select(path => new Bitmap(path)).ToList();

    var normalizedStatePaths = states
        .Select(state => Path.Combine(sourceDirectory, "States", state + ".png"))
        .ToArray();
    if (normalizedStatePaths.All(File.Exists))
        return normalizedStatePaths.Select(path => new Bitmap(path)).ToList();

    var sheetPath = Path.Combine(sourceDirectory, "companion-sprite-sheet.png");
    if (!File.Exists(sheetPath))
        throw new FileNotFoundException("缺少独立状态源文件和 companion-sprite-sheet.png。", sheetPath);

    using var sheet = new Bitmap(sheetPath);
    if (sheet.Width != frameSize * columns || sheet.Height != frameSize * 3)
        throw new InvalidDataException($"源精灵表必须是 {frameSize * columns}×{frameSize * 3}。");

    return states.Select((_, index) => sheet.Clone(
        new Rectangle((index % columns) * frameSize, (index / columns) * frameSize, frameSize, frameSize),
        PixelFormat.Format32bppArgb)).ToList();
}

static Graphics CreateGraphics(Bitmap bitmap)
{
    var graphics = Graphics.FromImage(bitmap);
    graphics.CompositingMode = CompositingMode.SourceCopy;
    graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
    graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
    graphics.SmoothingMode = SmoothingMode.HighQuality;
    graphics.Clear(Color.Transparent);
    return graphics;
}

static void DrawCentered(Graphics graphics, Bitmap source, Rectangle sourceBounds, Rectangle destination, double scale)
{
    var width = Math.Max(1, (int)Math.Round(sourceBounds.Width * scale));
    var height = Math.Max(1, (int)Math.Round(sourceBounds.Height * scale));
    var target = new Rectangle(
        destination.Left + (destination.Width - width) / 2,
        destination.Top + (destination.Height - height) / 2,
        width,
        height);
    graphics.DrawImage(source, target, sourceBounds, GraphicsUnit.Pixel);
}

static Rectangle FindAlphaBounds(Bitmap bitmap)
{
    var bounds = Rectangle.Empty;
    for (var y = 0; y < bitmap.Height; y++)
    for (var x = 0; x < bitmap.Width; x++)
    {
        if (bitmap.GetPixel(x, y).A < 16) continue;
        var pixel = new Rectangle(x, y, 1, 1);
        bounds = bounds.IsEmpty ? pixel : Rectangle.Union(bounds, pixel);
    }
    return bounds;
}
