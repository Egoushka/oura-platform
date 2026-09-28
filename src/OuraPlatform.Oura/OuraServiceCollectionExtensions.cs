using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.Extensions.Options;
using OuraPlatform.Oura.Auth;
using Polly;
using Polly.Retry;

namespace OuraPlatform.Oura;

public static class OuraServiceCollectionExtensions
{
    public static IServiceCollection AddOuraApi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<OuraOptions>()
            .Bind(configuration.GetSection(OuraOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.TryAddSingletonTimeProvider();
        services.AddTransient<OuraAuthenticationHandler>();

        services.AddHttpClient<IOuraOAuthClient, OuraOAuthClient>(OuraOAuthClient.HttpClientName);

        var api = services.AddHttpClient<IOuraApiClient, OuraApiClient>(OuraApiClient.HttpClientName, (provider, http) =>
        {
            http.BaseAddress = provider.GetRequiredService<IOptions<OuraOptions>>().Value.ApiBaseAddress;
            http.Timeout = TimeSpan.FromSeconds(100);
        });

        // Handlers run in registration order, outermost first. Retries go outside authentication so
        // that every attempt re-enters the auth handler and carries a currently-valid bearer token.
        api.AddResilienceHandler("oura", (builder, context) =>
        {
            var logger = context.ServiceProvider.GetRequiredService<ILoggerFactory>()
                .CreateLogger("OuraPlatform.Oura.Resilience");

            builder.AddRetry(new HttpRetryStrategyOptions
            {
                MaxRetryAttempts = 6,
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true,
                Delay = TimeSpan.FromSeconds(2),
                ShouldHandle = static arguments => ValueTask.FromResult(
                    arguments.Outcome.Exception is HttpRequestException or TimeoutException ||
                    arguments.Outcome.Result is { } response && IsTransient(response.StatusCode)),

                // Oura's rate limiting is two-tier and header-driven. The frequently quoted
                // "5000 requests per 5 minutes" is stale, so nothing here assumes a budget:
                // Retry-After is obeyed when present and exponential backoff covers the rest.
                DelayGenerator = static arguments =>
                {
                    var retryAfter = arguments.Outcome.Result?.Headers.RetryAfter;
                    var delay = retryAfter?.Delta
                        ?? (retryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);

                    return ValueTask.FromResult<TimeSpan?>(
                        delay is { } value && value > TimeSpan.Zero ? value : null);
                },

                OnRetry = arguments =>
                {
                    var status = arguments.Outcome.Result?.StatusCode;
                    if (status == HttpStatusCode.TooManyRequests)
                    {
                        logger.LogWarning(
                            "Oura rate limit hit (tier {Tier}); retry {Attempt} in {Delay}.",
                            Header(arguments.Outcome.Result, "X-RateLimit-Tier"),
                            arguments.AttemptNumber + 1,
                            arguments.RetryDelay);
                    }
                    else
                    {
                        logger.LogWarning(
                            "Oura request failed ({Status}); retry {Attempt} in {Delay}.",
                            status is null ? arguments.Outcome.Exception?.GetType().Name : ((int)status).ToString(),
                            arguments.AttemptNumber + 1,
                            arguments.RetryDelay);
                    }

                    return ValueTask.CompletedTask;
                },
            });

            builder.AddTimeout(TimeSpan.FromSeconds(90));
        });

        // Innermost: bearer token, proactive refresh, one reactive refresh on 401.
        api.AddHttpMessageHandler<OuraAuthenticationHandler>();

        return services;
    }

    private static bool IsTransient(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests
            or HttpStatusCode.RequestTimeout
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static string Header(HttpResponseMessage? response, string name) =>
        response is not null && response.Headers.TryGetValues(name, out var values)
            ? string.Join(',', values)
            : "-";

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
