namespace ReachTether.Server.Services;

public interface ISmartyModeClient
{
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken);
}

