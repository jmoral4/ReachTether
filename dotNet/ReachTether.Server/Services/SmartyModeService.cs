namespace ReachTether.Server.Services;

public sealed class SmartyModeService(ISmartyModeClient client) : ISmartyModeService
{
    public Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken)
        => client.ExecuteAsync(prompt, cancellationToken);
}

