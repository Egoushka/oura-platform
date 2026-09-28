using System.ComponentModel.DataAnnotations;

namespace OuraPlatform.Oura;

public sealed class OuraOptions
{
    public const string SectionName = "Oura";

    [Required]
    public string ClientId { get; set; } = string.Empty;

    [Required]
    public string ClientSecret { get; set; } = string.Empty;

    /// <summary>Must match one of the redirect URIs registered with the app exactly — Oura
    /// allowlists them verbatim.</summary>
    [Required]
    public string RedirectUri { get; set; } = "http://localhost:8080/oauth/callback";

    /// <summary>
    /// Space-separated. <c>email</c> is deliberately not requested.
    /// </summary>
    /// <remarks>
    /// These are the names Oura's API actually enforces, established by reading the 401 bodies —
    /// <c>{"detail":"Token is not authorized access spo2 scope."}</c> — not by reading the spec.
    /// The spec's OAuth2 flow declares <c>spo2Daily</c> and omits <c>stress</c> and
    /// <c>heart_health</c> entirely, and the authorize endpoint silently drops scope names it does
    /// not recognise, so a wrong entry here costs four collections and no error at consent time.
    /// See docs/oura-api-notes.md.
    /// </remarks>
    public string Scopes { get; set; } =
        "personal daily heartrate workout tag session spo2 stress heart_health ring_configuration";

    /// <summary>Points at the sandbox when true. The sandbox needs no credentials and returns
    /// fabricated data — see docs/oura-api-notes.md before trusting anything it says.</summary>
    public bool UseSandbox { get; set; }

    public Uri AuthorizeEndpoint { get; set; } = new("https://cloud.ouraring.com/oauth/authorize");

    public Uri TokenEndpoint { get; set; } = new("https://api.ouraring.com/oauth/token");

    public Uri ApiBaseAddress => UseSandbox
        ? new Uri("https://api.ouraring.com/v2/sandbox/usercollection/")
        : new Uri("https://api.ouraring.com/v2/usercollection/");

    /// <summary>Refresh this far ahead of <c>expires_at</c> rather than waiting for a 401.</summary>
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Earliest day the backfill will ask for. Oura returns empty pages before the ring
    /// existed, so this only bounds how much empty range gets walked.</summary>
    public DateOnly BackfillFrom { get; set; } = new(2020, 1, 1);

    /// <summary>Days of already-ingested history the reconcile job re-fetches on every run, because
    /// Oura amends recent days after the fact.</summary>
    public int ReconcileTrailingDays { get; set; } = 7;

    /// <summary>Spec revision the models were written against; recorded on every raw document so a
    /// re-projection can tell which shape a payload was parsed under.</summary>
    public string SpecVersion { get; set; } = "1.37";
}
