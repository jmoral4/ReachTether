using OpenAI.RealtimeConversation;

internal interface IRealtimeEventHandler
{
    int Order { get; }

    ValueTask<bool> HandleAsync(
        ConversationUpdate update,
        RealtimeTurnContext context,
        CancellationToken ct);
}
