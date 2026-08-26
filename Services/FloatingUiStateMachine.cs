using System;
using Huaxiazi.Models;

namespace Huaxiazi.Services;

public sealed class FloatingUiStateMachine
{
    public FloatingUiState State { get; private set; } = FloatingUiState.Ball;

    public void BeginExpand() => Transition(FloatingUiState.Ball, FloatingUiState.Expanding);
    public void CompleteExpand() => Transition(FloatingUiState.Expanding, FloatingUiState.Window);
    public void BeginCollapse() => Transition(FloatingUiState.Window, FloatingUiState.Collapsing);
    public void CompleteCollapse() => Transition(FloatingUiState.Collapsing, FloatingUiState.Ball);
    public void BeginDrag() => Transition(FloatingUiState.Ball, FloatingUiState.Dragging);
    public void CompleteDrag() => Transition(FloatingUiState.Dragging, FloatingUiState.Ball);
    public void ShowWindowDirect() => Transition(FloatingUiState.Ball, FloatingUiState.Window);
    public void CancelExpand() => Transition(FloatingUiState.Expanding, FloatingUiState.Ball);

    /// <summary>动画回调丢失或窗口生命周期被中断时，恢复到可再次交互的稳定球态。</summary>
    public void ResetToBall() => State = FloatingUiState.Ball;

    private void Transition(FloatingUiState expected, FloatingUiState next)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Cannot transition from {State} to {next}; expected {expected}.");
        }

        State = next;
    }
}
