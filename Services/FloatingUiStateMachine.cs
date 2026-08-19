using System;
using PromptFloat.Models;

namespace PromptFloat.Services;

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

    private void Transition(FloatingUiState expected, FloatingUiState next)
    {
        if (State != expected)
        {
            throw new InvalidOperationException($"Cannot transition from {State} to {next}; expected {expected}.");
        }

        State = next;
    }
}
