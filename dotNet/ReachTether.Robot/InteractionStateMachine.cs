using Microsoft.Extensions.Logging;

internal enum InteractionState
{
    Idle,
    Listening,
    Thinking,
    Speaking,
    Interrupted
}

internal interface IInteractionStateMachine
{
    InteractionState Current { get; }
    void TransitionTo(InteractionState next, string reason);
}

internal sealed class InteractionStateMachine(ILogger<InteractionStateMachine> logger) : IInteractionStateMachine
{
    public InteractionState Current { get; private set; } = InteractionState.Idle;

    public void TransitionTo(InteractionState next, string reason)
    {
        if (Current == next)
        {
            return;
        }

        logger.LogInformation("[State] {From} -> {To} ({Reason})", Current, next, reason);
        Current = next;
    }
}
