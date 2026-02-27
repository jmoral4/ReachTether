using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ReachyMini.Sdk.Configuration;

namespace ReachyMini.Sdk;

/// <summary>
/// Extension methods for configuring ReachyMini SDK services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds the ReachyMini SDK to the service collection.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="baseUrl">The base URL of the Reachy Mini API (default: http://localhost:8080).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddReachyMiniClient(
        this IServiceCollection services, 
        string baseUrl = "http://localhost:8080")
    {
        return services.AddReachyMiniClient(options =>
        {
            options.BaseUrl = baseUrl;
        });
    }

    /// <summary>
    /// Adds the ReachyMini SDK to the service collection with configuration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configureOptions">Configuration action for ReachyMiniOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddReachyMiniClient(
        this IServiceCollection services,
        Action<ReachyMiniOptions> configureOptions)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (configureOptions == null)
            throw new ArgumentNullException(nameof(configureOptions));

        // Configure options
        services.Configure(configureOptions);

        // Register HttpClient with typed client
        services.AddHttpClient<ReachyMiniClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReachyMiniOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = options.Timeout;
        });

        return services;
    }

    /// <summary>
    /// Adds the ReachyMini SDK to the service collection with configuration from app settings.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The configuration section containing ReachyMiniOptions.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddReachyMiniClient(
        this IServiceCollection services,
        Microsoft.Extensions.Configuration.IConfiguration configuration)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        if (configuration == null)
            throw new ArgumentNullException(nameof(configuration));

        // Bind configuration
        services.Configure<ReachyMiniOptions>(configuration);

        // Register HttpClient with typed client
        services.AddHttpClient<ReachyMiniClient>((serviceProvider, client) =>
        {
            var options = serviceProvider.GetRequiredService<Microsoft.Extensions.Options.IOptions<ReachyMiniOptions>>().Value;
            client.BaseAddress = new Uri(options.BaseUrl);
            client.Timeout = options.Timeout;
        });

        return services;
    }
}
