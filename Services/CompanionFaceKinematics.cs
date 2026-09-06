using System;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>
/// Projects the continuous companion pose into a nested face rig. The orb, head, eyes and mouth
/// share one coordinate hierarchy: the head carries every facial feature while the eyes retain a
/// smaller local gaze range. This keeps the face readable at every cursor direction.
/// </summary>
public readonly record struct CompanionFaceProjection(
    double OrbX,
    double OrbY,
    double OrbRotation,
    double HeadX,
    double HeadY,
    double HeadRotation,
    double EyeX,
    double EyeY,
    double MouthX,
    double MouthY,
    double MouthScaleX,
    double MouthScaleY,
    double EyeBaselineY,
    double MouthBaselineY);

public static class CompanionFaceKinematics
{
    internal const double EyeBaseline = 18.5;
    // The vector artwork uses the same baseline. Keeping this gap compact makes
    // the default face read as one expression instead of two detached rows.
    // Keep a clear two-unit reserve below the eye row while preserving the compact idle face.
    internal const double MouthBaseline = 29;
    internal const double FeatureGap = MouthBaseline - EyeBaseline;

    /// <summary>
    /// Gives processing feedback a small, deliberate vertical separation. The default face is
    /// intentionally compact at rest, but the thinking/working gaze and mouth animate at the
    /// same time; keeping the mouth a little lower prevents the two features from visually
    /// colliding during a request.
    /// </summary>
    public static double GetStateMouthVisualOffset(CompanionVisualState state) => state switch
    {
        CompanionVisualState.Thinking => 1.8,
        CompanionVisualState.Working => 1.8,
        CompanionVisualState.Listening => 0.8,
        CompanionVisualState.Curious => 0.5,
        CompanionVisualState.Warning => 0.8,
        CompanionVisualState.Error => 0.8,
        _ => 0
    };

    public static CompanionFaceProjection Project(CompanionPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        var gazeX = Math.Clamp(pose.GazeX, -8, 8);
        var gazeY = Math.Clamp(pose.GazeY, -5, 5);
        var headX = Math.Clamp(gazeX * 0.29, -2.32, 2.32);
        var headY = Math.Clamp(gazeY * 0.24, -1.2, 1.2);
        var eyeX = Math.Clamp(gazeX * 0.46, -3.68, 3.68);
        var eyeY = Math.Clamp(gazeY * 0.24, -1.2, 1.2);
        var mouthX = Math.Clamp(gazeX * 0.055, -0.44, 0.44);
        var mouthY = Math.Clamp(gazeY * 0.08, -0.4, 0.4) - Math.Clamp(pose.MouthCurve, -1.2, 1.2) * 0.34;
        var expressionStrength = Math.Min(1, Math.Abs(pose.MouthCurve));

        return new CompanionFaceProjection(
            OrbX: gazeX * 0.055,
            OrbY: gazeY * 0.045,
            OrbRotation: Math.Clamp(gazeX * 0.12, -0.95, 0.95),
            HeadX: headX,
            HeadY: headY,
            HeadRotation: Math.Clamp(gazeX * 0.25 - gazeY * 0.08, -2.4, 2.4),
            EyeX: eyeX,
            EyeY: eyeY,
            MouthX: mouthX,
            MouthY: mouthY,
            MouthScaleX: 1 + expressionStrength * 0.045,
            MouthScaleY: 1 + expressionStrength * 0.09,
            EyeBaselineY: EyeBaseline + headY + eyeY,
            MouthBaselineY: MouthBaseline + headY + mouthY);
    }
}
