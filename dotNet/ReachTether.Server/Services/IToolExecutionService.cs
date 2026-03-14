namespace ReachTether.Server.Services;

public interface IToolExecutionService
{
    Task<RemoteToolExecutionResponse> ExecuteAsync(RemoteToolExecutionRequest request, CancellationToken cancellationToken);
}

