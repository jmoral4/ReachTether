namespace ReachTether.Server.Services;

public interface ISmartyModeService
{
    Task<string> ExecuteAsync(string prompt, CancellationToken cancellationToken);
}

