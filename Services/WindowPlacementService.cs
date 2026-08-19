using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace PromptFloat.Services;

public enum ExpansionDirection
{
    DownRight,
    DownLeft,
    UpRight,
    UpLeft,
    Right,
    Left,
    Down,
    Up
}

public readonly record struct ExpansionPlacement(
    Rect Bounds,
    ExpansionDirection Direction,
    bool WasReduced);

public static class WindowPlacementService
{
    [Obsolete("Use CalculateExpansionPlacement so the active monitor work area is respected.")]
    public static Point GetExpandedOrigin(Point floatingBallOrigin) => floatingBallOrigin;

    public static ExpansionPlacement CalculateExpansionPlacement(
        Rect ballBounds,
        Size desiredSize,
        Rect workArea,
        double safeMargin = 8)
    {
        safeMargin = Math.Max(0, safeMargin);
        var safeArea = new Rect(
            workArea.Left + safeMargin,
            workArea.Top + safeMargin,
            Math.Max(0, workArea.Width - safeMargin * 2),
            Math.Max(0, workArea.Height - safeMargin * 2));
        var actualSize = new Size(
            Math.Min(Math.Max(0, desiredSize.Width), safeArea.Width),
            Math.Min(Math.Max(0, desiredSize.Height), safeArea.Height));
        var wasReduced = actualSize.Width < desiredSize.Width || actualSize.Height < desiredSize.Height;
        var anchor = new Point(ballBounds.Left + ballBounds.Width / 2, ballBounds.Top + ballBounds.Height / 2);

        var candidates = new (ExpansionDirection Direction, Rect Bounds)[]
        {
            (ExpansionDirection.DownRight, new Rect(anchor.X, anchor.Y, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.DownLeft, new Rect(anchor.X - actualSize.Width, anchor.Y, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.UpRight, new Rect(anchor.X, anchor.Y - actualSize.Height, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.UpLeft, new Rect(anchor.X - actualSize.Width, anchor.Y - actualSize.Height, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.Right, new Rect(anchor.X, anchor.Y - actualSize.Height / 2, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.Left, new Rect(anchor.X - actualSize.Width, anchor.Y - actualSize.Height / 2, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.Down, new Rect(anchor.X - actualSize.Width / 2, anchor.Y, actualSize.Width, actualSize.Height)),
            (ExpansionDirection.Up, new Rect(anchor.X - actualSize.Width / 2, anchor.Y - actualSize.Height, actualSize.Width, actualSize.Height))
        };

        var best = candidates[0];
        var bestScore = ScoreCandidate(best.Bounds, safeArea);
        foreach (var candidate in candidates.AsSpan(1))
        {
            var score = ScoreCandidate(candidate.Bounds, safeArea);
            if (score.CompareTo(bestScore) > 0)
            {
                best = candidate;
                bestScore = score;
            }
        }

        return new ExpansionPlacement(ClampRect(best.Bounds, safeArea), best.Direction, wasReduced);
    }

    public static Point SnapBallOrigin(
        Point desiredOrigin,
        Size ballSize,
        Rect workArea,
        double threshold = 20,
        double inset = 6)
    {
        threshold = Math.Max(0, threshold);
        inset = Math.Max(0, inset);
        var maxLeft = Math.Max(workArea.Left, workArea.Right - ballSize.Width);
        var maxTop = Math.Max(workArea.Top, workArea.Bottom - ballSize.Height);
        var left = Math.Clamp(desiredOrigin.X, workArea.Left, maxLeft);
        var top = Math.Clamp(desiredOrigin.Y, workArea.Top, maxTop);

        var leftDistance = Math.Abs(desiredOrigin.X - workArea.Left);
        var rightDistance = Math.Abs(desiredOrigin.X + ballSize.Width - workArea.Right);
        var topDistance = Math.Abs(desiredOrigin.Y - workArea.Top);
        var bottomDistance = Math.Abs(desiredOrigin.Y + ballSize.Height - workArea.Bottom);

        if (Math.Min(leftDistance, rightDistance) <= threshold)
        {
            left = leftDistance <= rightDistance
                ? Math.Min(workArea.Right - ballSize.Width, workArea.Left + inset)
                : Math.Max(workArea.Left, workArea.Right - inset - ballSize.Width);
        }
        if (Math.Min(topDistance, bottomDistance) <= threshold)
        {
            top = topDistance <= bottomDistance
                ? Math.Min(workArea.Bottom - ballSize.Height, workArea.Top + inset)
                : Math.Max(workArea.Top, workArea.Bottom - inset - ballSize.Height);
        }

        return new Point(left, top);
    }

    private static CandidateScore ScoreCandidate(Rect candidate, Rect safeArea)
    {
        var intersection = Rect.Intersect(candidate, safeArea);
        var area = Math.Max(0, candidate.Width * candidate.Height);
        var visibleRatio = area <= 0 ? 0 : Math.Max(0, intersection.Width * intersection.Height) / area;
        var corrected = ClampRect(candidate, safeArea);
        var correction = Math.Abs(corrected.Left - candidate.Left) + Math.Abs(corrected.Top - candidate.Top);
        var fullyVisible = visibleRatio >= 0.999999;
        return new CandidateScore(fullyVisible, visibleRatio, -correction);
    }

    private readonly record struct CandidateScore(bool FullyVisible, double VisibleRatio, double NegativeCorrection)
        : IComparable<CandidateScore>
    {
        public int CompareTo(CandidateScore other)
        {
            var full = FullyVisible.CompareTo(other.FullyVisible);
            if (full != 0) return full;
            var visible = VisibleRatio.CompareTo(other.VisibleRatio);
            return visible != 0 ? visible : NegativeCorrection.CompareTo(other.NegativeCorrection);
        }
    }

    public static Point GetRestoredBallOrigin(Point originalBallOrigin, Point clampedExpandedOrigin)
    {
        _ = clampedExpandedOrigin;
        return originalBallOrigin;
    }

    public static Point GetDockedCompanionOrigin(Rect ownerBounds, Size companionSize, double gap, Rect workArea)
    {
        gap = Math.Max(0, gap);
        var right = ownerBounds.Right + gap;
        var left = ownerBounds.Left - gap - companionSize.Width;
        var x = right + companionSize.Width <= workArea.Right
            ? right
            : left >= workArea.Left ? left : Math.Clamp(right, workArea.Left, workArea.Right - companionSize.Width);
        var y = Math.Clamp(ownerBounds.Top, workArea.Top, workArea.Bottom - companionSize.Height);
        return new Point(x, y);
    }

    private const int WmDpiChanged = 0x02E0;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    public static Rect ClampRect(Rect desired, Rect workArea)
    {
        var left = desired.Width >= workArea.Width
            ? workArea.Left
            : Math.Clamp(desired.Left, workArea.Left, workArea.Right - desired.Width);
        var top = desired.Height >= workArea.Height
            ? workArea.Top
            : Math.Clamp(desired.Top, workArea.Top, workArea.Bottom - desired.Height);
        return new Rect(left, top, desired.Width, desired.Height);
    }

    public static void Attach(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        window.SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(window) is HwndSource source)
            {
                source.AddHook((IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) =>
                {
                    if (message == WmDpiChanged)
                    {
                        window.Dispatcher.BeginInvoke(() => ClampToCurrentWorkArea(window));
                    }
                    return IntPtr.Zero;
                });
            }
        };
        window.Loaded += (_, _) => ClampToCurrentWorkArea(window);
        window.Activated += (_, _) => ClampToCurrentWorkArea(window);
        window.AddHandler(Mouse.MouseUpEvent, new MouseButtonEventHandler((_, _) => ClampToCurrentWorkArea(window)), true);
    }

    public static void ClampToCurrentWorkArea(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero || !GetWindowRect(handle, out var windowRect)) return;
            var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
            var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo)) return;

            var desired = new Rect(
                windowRect.Left,
                windowRect.Top,
                windowRect.Right - windowRect.Left,
                windowRect.Bottom - windowRect.Top);
            var workArea = new Rect(
                monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Top,
                monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left,
                monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top);
            var clamped = ClampRect(desired, workArea);
            if (clamped.Left == desired.Left && clamped.Top == desired.Top) return;
            SetWindowPos(
                handle,
                IntPtr.Zero,
                (int)Math.Round(clamped.Left),
                (int)Math.Round(clamped.Top),
                0,
                0,
                SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }
        catch
        {
            // 显示器正在断开或窗口尚未完成创建时安全降级，下一次激活会再次校正。
        }
    }

    public static Rect GetCurrentWorkAreaDip(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return SystemParameters.WorkArea;

        var monitor = MonitorFromWindow(handle, MonitorDefaultToNearest);
        var monitorInfo = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref monitorInfo))
        {
            return SystemParameters.WorkArea;
        }

        var dpi = GetDpiForWindow(handle);
        var scale = dpi == 0 ? 1d : dpi / 96d;
        return new Rect(
            monitorInfo.WorkArea.Left / scale,
            monitorInfo.WorkArea.Top / scale,
            (monitorInfo.WorkArea.Right - monitorInfo.WorkArea.Left) / scale,
            (monitorInfo.WorkArea.Bottom - monitorInfo.WorkArea.Top) / scale);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect WorkArea;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo monitorInfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);
}
