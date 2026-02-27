using Microsoft.Extensions.Options;
using ReachyMini.Sdk.Configuration;
using ReachyMini.Sdk.Models;

namespace ReachyMini.Sdk.Clients;

/// <summary>
/// Client for HuggingFace authentication management.
/// </summary>
public class AuthClient : BaseClient
{
    public AuthClient(HttpClient httpClient, IOptions<ReachyMiniOptions> options) 
        : base(httpClient, options)
    {
    }

    /// <summary>
    /// Save HuggingFace token after validation.
    /// </summary>
    public Task<TokenResponse> SaveTokenAsync(
        TokenRequest request, 
        CancellationToken cancellationToken = default)
        => PostAsync<TokenRequest, TokenResponse>("/api/hf-auth/save-token", request, cancellationToken);

    /// <summary>
    /// Check if user is authenticated with HuggingFace.
    /// </summary>
    public Task<Dictionary<string, object>> GetAuthStatusAsync(CancellationToken cancellationToken = default)
        => GetAsync<Dictionary<string, object>>("/api/hf-auth/status", cancellationToken);

    /// <summary>
    /// Delete stored HuggingFace token.
    /// </summary>
    public Task<Dictionary<string, string>> DeleteTokenAsync(CancellationToken cancellationToken = default)
        => DeleteAsync<Dictionary<string, string>>("/api/hf-auth/token", cancellationToken);
}
