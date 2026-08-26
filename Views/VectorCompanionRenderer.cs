using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Huaxiazi.Models;
using Huaxiazi.Services;

namespace Huaxiazi.Views;

/// <summary>纯 WPF 分层矢量角色呈现器。素材只提供几何和关键帧数据。</summary>
public sealed class VectorCompanionRenderer : Canvas
{
    private readonly Dictionary<string, FrameworkElement> _layers = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TranslateTransform> _gazeTransforms = new(StringComparer.OrdinalIgnoreCase);
    private readonly TransformGroup _motion = new();
    private readonly ScaleTransform _motionScale = new(1, 1);
    private readonly RotateTransform _motionRotation = new();
    private readonly TranslateTransform _motionOffset = new();
    private VectorCharacterManifest? _manifest;
    private VectorAnimationClip? _activeClip;

    public VectorCompanionRenderer()
    {
        Width = Height = 88;
        // 设计稿使用 88×88 坐标，悬浮球运行时为 44×44；保持坐标系不变，
        // 由统一的呈现器缩放，避免每个图层自行换算导致中心漂移。
        LayoutTransform = new ScaleTransform(.5, .5);
        RenderTransformOrigin = new Point(.5, .5);
        _motion.Children.Add(_motionScale);
        _motion.Children.Add(_motionRotation);
        _motion.Children.Add(_motionOffset);
        RenderTransform = _motion;
        IsHitTestVisible = false;
    }

    public bool IsLoadedProfile => _manifest is not null;

    public void Load(VectorCharacterManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        manifest.Validate();
        _manifest = manifest;
        Children.Clear();
        _layers.Clear();
        _gazeTransforms.Clear();
        foreach (var definition in manifest.Layers)
        {
            var element = CreateLayer(definition);
            _layers[definition.Id] = element;
            Children.Add(element);
        }
        ApplyState(CompanionVisualState.Idle, animate: false);
    }

    public void ApplyState(CompanionVisualState state, bool animate)
    {
        if (_manifest is null || !_manifest.Animations.TryGetValue(state.ToString(), out var clip)) return;
        _activeClip = clip;
        var middle = clip.Frames[Math.Min(1, clip.Frames.Count - 1)];
        if (_layers.TryGetValue("mouth", out var mouth) && mouth is Path mouthPath && !string.IsNullOrWhiteSpace(middle.MouthPath))
            mouthPath.Data = Geometry.Parse(middle.MouthPath);

        if (!animate || !App.Settings.AnimationsEnabled)
        {
            ApplyFrame(clip.Frames[^1]);
            return;
        }

        AnimateProperty(_motionScale, ScaleTransform.ScaleXProperty, clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.Scale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
        AnimateProperty(_motionScale, ScaleTransform.ScaleYProperty, clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.Scale, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
        AnimateProperty(_motionOffset, TranslateTransform.XProperty, clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.OffsetX, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
        AnimateProperty(_motionOffset, TranslateTransform.YProperty, clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.OffsetY, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
        AnimateProperty(_motionRotation, RotateTransform.AngleProperty, clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.Rotation, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
        foreach (var id in new[] { "leftEyeWhite", "rightEyeWhite", "leftPupil", "rightPupil" })
            if (_layers.TryGetValue(id, out var eye) && eye.RenderTransform is TransformGroup group && group.Children[0] is ScaleTransform scale)
                AnimateProperty(scale, ScaleTransform.ScaleYProperty,
                    clip.Frames.Select(frame => new EasingDoubleKeyFrame(frame.EyeScale,
                        KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(frame.TimeMs)))));
    }

    public void ApplyGaze(double x, double y)
    {
        foreach (var transform in _gazeTransforms.Values)
        {
            transform.X = Math.Clamp(x * .72, -6, 6);
            transform.Y = Math.Clamp(y * .72, -4, 4);
        }
    }

    private FrameworkElement CreateLayer(VectorLayerDefinition definition)
    {
        FrameworkElement element;
        var centerX = 44d;
        var centerY = 44d;
        if (string.Equals(definition.Type, "ellipse", StringComparison.OrdinalIgnoreCase))
        {
            var bounds = definition.Bounds!;
            element = new Ellipse { Width = bounds[2], Height = bounds[3] };
            Canvas.SetLeft(element, bounds[0]);
            Canvas.SetTop(element, bounds[1]);
            centerX = bounds[0] + bounds[2] / 2;
            centerY = bounds[1] + bounds[3] / 2;
        }
        else
        {
            element = new Path { Data = Geometry.Parse(definition.Path!) };
        }
        if (element is Shape shape)
        {
            shape.Fill = ToBrush(definition.Fill);
            shape.Stroke = ToBrush(definition.Stroke);
            shape.StrokeThickness = definition.StrokeWidth;
            shape.StrokeStartLineCap = PenLineCap.Round;
            shape.StrokeEndLineCap = PenLineCap.Round;
            shape.StrokeLineJoin = PenLineJoin.Round;
        }
        var group = new TransformGroup();
        var scale = new ScaleTransform(1, 1, centerX, centerY);
        group.Children.Add(scale);
        var gaze = new TranslateTransform();
        group.Children.Add(gaze);
        element.RenderTransform = group;
        if (definition.Gaze) _gazeTransforms[definition.Id] = gaze;
        return element;
    }

    private void ApplyFrame(VectorKeyframe frame)
    {
        _motionScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        _motionScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        _motionOffset.BeginAnimation(TranslateTransform.XProperty, null);
        _motionOffset.BeginAnimation(TranslateTransform.YProperty, null);
        _motionRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        _motionScale.ScaleX = _motionScale.ScaleY = frame.Scale;
        _motionOffset.X = frame.OffsetX;
        _motionOffset.Y = frame.OffsetY;
        _motionRotation.Angle = frame.Rotation;
        foreach (var id in new[] { "leftEyeWhite", "rightEyeWhite", "leftPupil", "rightPupil" })
            if (_layers.TryGetValue(id, out var eye) && eye.RenderTransform is TransformGroup group && group.Children[0] is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleY = frame.EyeScale;
            }
        if (_layers.TryGetValue("mouth", out var mouth) && mouth is Path mouthPath && !string.IsNullOrWhiteSpace(frame.MouthPath))
            mouthPath.Data = Geometry.Parse(frame.MouthPath);
    }

    private static void AnimateProperty<T>(Animatable target, DependencyProperty property, IEnumerable<T> frames)
        where T : DoubleKeyFrame
    {
        var keyframes = new DoubleAnimationUsingKeyFrames();
        foreach (var frame in frames) keyframes.KeyFrames.Add(frame);
        if (keyframes.KeyFrames.Count > 0)
            keyframes.Duration = keyframes.KeyFrames[^1].KeyTime.TimeSpan;
        target.BeginAnimation(property, keyframes);
    }

    private static Brush? ToBrush(string? value) => string.IsNullOrWhiteSpace(value)
        ? null
        : new SolidColorBrush((Color)ColorConverter.ConvertFromString(value)!);
}
