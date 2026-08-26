using Huaxiazi.Models;

namespace Huaxiazi.Services;

/// <summary>角色动画的统一优先级规则；呈现器和交互层共享这套判定。</summary>
public static class VectorAnimationController
{
    public static int Priority(CompanionVisualState state) => state switch
    {
        CompanionVisualState.Error or CompanionVisualState.Warning => 5,
        CompanionVisualState.Working or CompanionVisualState.Thinking => 4,
        CompanionVisualState.Dragging or CompanionVisualState.Expanding => 3,
        CompanionVisualState.Happy or CompanionVisualState.Surprised => 2,
        CompanionVisualState.Curious or CompanionVisualState.Listening => 1,
        _ => 0
    };

    public static bool CanInterrupt(CompanionVisualState current, CompanionVisualState next) =>
        Priority(next) >= Priority(current) || current is CompanionVisualState.Idle or CompanionVisualState.Sleeping;
}
