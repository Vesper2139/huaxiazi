using PromptFloat.Models;
using PromptFloat.Services;
using Xunit;

namespace PromptFloat.Tests;

public sealed class FloatingUiStateMachineTests
{
    [Fact]
    public void ExpandAndCollapse_UseExplicitTransitionStates()
    {
        var machine = new FloatingUiStateMachine();

        Assert.Equal(FloatingUiState.Ball, machine.State);
        machine.BeginExpand();
        Assert.Equal(FloatingUiState.Expanding, machine.State);
        machine.CompleteExpand();
        Assert.Equal(FloatingUiState.Window, machine.State);
        machine.BeginCollapse();
        Assert.Equal(FloatingUiState.Collapsing, machine.State);
        machine.CompleteCollapse();
        Assert.Equal(FloatingUiState.Ball, machine.State);
    }

    [Fact]
    public void DraggingBall_ReturnsToBallWithoutLosingItsSemanticState()
    {
        var machine = new FloatingUiStateMachine();

        machine.BeginDrag();
        Assert.Equal(FloatingUiState.Dragging, machine.State);
        machine.CompleteDrag();

        Assert.Equal(FloatingUiState.Ball, machine.State);
    }

    [Fact]
    public void InvalidTransition_IsRejectedInsteadOfSilentlyCorruptingState()
    {
        var machine = new FloatingUiStateMachine();

        Assert.Throws<InvalidOperationException>(machine.BeginCollapse);
        Assert.Equal(FloatingUiState.Ball, machine.State);
    }

    [Fact]
    public void DirectWindowEntry_FromTray_CanStillCollapseSafely()
    {
        var machine = new FloatingUiStateMachine();

        machine.ShowWindowDirect();
        Assert.Equal(FloatingUiState.Window, machine.State);
        machine.BeginCollapse();
        machine.CompleteCollapse();

        Assert.Equal(FloatingUiState.Ball, machine.State);
    }

    [Fact]
    public void ResetToBall_RecoversFromAnInterruptedCollapse()
    {
        var machine = new FloatingUiStateMachine();
        machine.ShowWindowDirect();
        machine.BeginCollapse();

        machine.ResetToBall();
        machine.BeginExpand();

        Assert.Equal(FloatingUiState.Expanding, machine.State);
    }

    [Fact]
    public void CancelExpand_ReturnsToBallAndAllowsASecondAttempt()
    {
        var machine = new FloatingUiStateMachine();
        machine.BeginExpand();

        machine.CancelExpand();
        machine.BeginExpand();

        Assert.Equal(FloatingUiState.Expanding, machine.State);
    }
}
