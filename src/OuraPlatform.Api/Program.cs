using OuraPlatform.Api;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Auth;
using OuraPlatform.Storage;
using Serilog;
using Serilog.Formatting.Compact;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog((context, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .Enrich.FromLogContext()
    .WriteTo.Console(new CompactJsonFormatter()));

builder.Services.AddOuraApi(builder.Configuration);
builder.Services.AddOuraStorage(builder.Configuration);
builder.Services.AddSingleton<OAuthStateStore>();
builder.Services.AddHealthChecks().AddCheck<DatabaseHealthCheck>("database");

var app = builder.Build();

app.UseSerilogRequestLogging();
app.MapHealthChecks("/healthz");

// --------------------------------------------------------------------------
// OAuth handshake. Personal Access Tokens were deprecated in December 2025, so
// this browser round-trip is the only way to obtain a token.
// --------------------------------------------------------------------------

// AbsoluteUri, not ToString(): ToString() returns the "human readable" form and unescapes the
// %20 separating the scopes, which puts raw spaces in the Location header.
app.MapGet("/oauth/start", (IOuraOAuthClient oauth, OAuthStateStore states) =>
    Results.Redirect(oauth.BuildAuthorizationUri(states.Issue()).AbsoluteUri));

app.MapGet("/oauth/callback", async (
    HttpRequest request,
    IOuraOAuthClient oauth,
    IOuraTokenStore tokens,
    OAuthStateStore states,
    ILogger<OAuthCallback> logger,
    CancellationToken cancellationToken) =>
{
    var error = request.Query["error"].ToString();
    if (!string.IsNullOrEmpty(error))
    {
        logger.LogWarning("Oura rejected the authorization request: {Error}.", error);
        return Results.Content(
            Pages.Failure($"Oura rejected the authorization request: {error}. {request.Query["error_description"]}"),
            "text/html");
    }

    var code = request.Query["code"].ToString();
    if (string.IsNullOrEmpty(code))
    {
        return Results.Content(Pages.Failure("No authorization code in the callback."), "text/html");
    }

    if (!states.Consume(request.Query["state"].ToString()))
    {
        logger.LogWarning("Rejected an OAuth callback with an unknown, expired or reused state value.");
        return Results.Content(Pages.Failure("Unknown, expired or already-used state value."), "text/html");
    }

    // The authorization code is valid for 10 minutes and single-use, as is the refresh token it buys.
    var exchanged = await oauth.ExchangeCodeAsync(code, cancellationToken);
    var stored = await tokens.SaveAsync(exchanged, cancellationToken);

    logger.LogInformation("Oura authorization completed; access token expires {ExpiresAt:o}.", stored.ExpiresAt);
    return Results.Content(Pages.Success(stored.ExpiresAt, stored.Scope), "text/html");
});

// --------------------------------------------------------------------------
// Stage 3. The route exists so the callback URL can be registered with Oura up
// front, but it is deliberately unimplemented. Note what it must never return:
// replying 410 auto-cancels the subscription at Oura's end.
// --------------------------------------------------------------------------
app.MapMethods("/webhooks/oura", ["GET", "POST"], () =>
    Results.StatusCode(StatusCodes.Status501NotImplemented));

app.Run();
