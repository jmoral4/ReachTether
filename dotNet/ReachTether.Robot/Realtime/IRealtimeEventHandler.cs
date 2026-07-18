internal interface IRealtimeEventHandler
{
    int Order { get; }

    ValueTask<bool> HandleAsync(
        RealtimeServerEvent update,
        RealtimeTurnContext context,
        CancellationToken ct);
}
