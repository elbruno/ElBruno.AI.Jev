using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElBruno.AI.Jev;

/// <summary>Registers validated Jev clients without capturing a short-lived factory HTTP client in a singleton.</summary>
public static class JevServiceCollectionExtensions
{
    /// <summary>The named factory transport, available for further handler configuration.</summary>
    public const string HttpClientName = "ElBruno.AI.Jev";

    /// <summary>Registers one default client. Additional independent clients can be constructed explicitly.</summary>
    public static IHttpClientBuilder AddJev(this IServiceCollection services, Action<JevClientOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);
        services.AddOptions<JevClientOptions>().Configure(configure).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<JevClientOptions>, JevOptionsValidator>());
        services.TryAddSingleton<JevClient>(provider => new JevClient(
            () => provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<IOptions<JevClientOptions>>().Value));
        services.TryAddSingleton<IJevDecisionClient>(provider => provider.GetRequiredService<JevClient>());
        return services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan)
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
    }

    private sealed class JevOptionsValidator : IValidateOptions<JevClientOptions>
    {
        public ValidateOptionsResult Validate(string? name, JevClientOptions options)
        {
            string[] errors = options.ValidationErrors();
            return errors.Length == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
        }
    }
}
