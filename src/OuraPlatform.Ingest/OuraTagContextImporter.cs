using System.Text.Json;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Models;
using OuraPlatform.Storage;

namespace OuraPlatform.Ingest;

/// <summary>
/// Projects Oura's own <c>enhanced_tag</c> events into <c>context</c>.
/// </summary>
/// <remarks>
/// These are already in <c>oura_raw</c> and already projected into <c>tags</c>, so nothing is
/// fetched here. What they are not is *joinable*: the distribution dashboards ask "what was
/// readiness like on days with X", and X has to live in one table for that question to have one
/// query. A caffeine tag and a calendar meeting are the same kind of fact.
/// <para>
/// Oura's tag codes are namespaced (<c>generic_alcohol</c>, <c>tag_generic_stress</c>). They are
/// normalised to bare kinds so `context.kind` reads the same whether a row came from a calendar or
/// from the ring.
/// </para>
/// </remarks>
public sealed class OuraTagContextImporter
{
    private const string Source = "oura_tag";

    private readonly RawDocumentRepository _raw;
    private readonly ContextRepository _context;
    private readonly ILogger<OuraTagContextImporter> _logger;

    public OuraTagContextImporter(
        RawDocumentRepository raw,
        ContextRepository context,
        ILogger<OuraTagContextImporter> logger)
    {
        _raw = raw;
        _context = context;
        _logger = logger;
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var stored = await _raw.ReadAsync("enhanced_tag", from: null, to: null, cancellationToken).ConfigureAwait(false);

        var events = new List<ContextEvent>(stored.Count);
        foreach (var document in stored)
        {
            EnhancedTag? tag;
            try
            {
                tag = JsonSerializer.Deserialize<EnhancedTag>(document.Payload, OuraJson.Options);
            }
            catch (JsonException exception)
            {
                _logger.LogWarning(exception, "Could not read enhanced_tag {DocId}; skipping.", document.DocId);
                continue;
            }

            if (tag is null)
            {
                continue;
            }

            events.Add(new ContextEvent(
                Source,
                tag.Id,
                tag.StartTime,
                tag.EndTime,
                NormaliseKind(tag.TagTypeCode ?? tag.CustomName),
                tag.Comment ?? tag.CustomName ?? tag.TagTypeCode,
                JsonSerializer.Serialize(new
                {
                    tag_type_code = tag.TagTypeCode,
                    custom_name = tag.CustomName,
                })));
        }

        var written = await _context.UpsertAsync(events, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Oura tags: {Count} event(s) upserted into context.", written);
        return written;
    }

    /// <summary>
    /// <c>generic_alcohol</c> and <c>tag_generic_alcohol</c> are both "alcohol". Left as-is when the
    /// code is not recognised — an unknown kind is still a fact, and inventing a bucket for it would
    /// lose the distinction.
    /// </summary>
    internal static string NormaliseKind(string? tagTypeCode)
    {
        if (string.IsNullOrWhiteSpace(tagTypeCode))
        {
            return "tag";
        }

        var bare = tagTypeCode.Trim().ToLowerInvariant();
        foreach (var prefix in (string[])["tag_generic_", "generic_", "tag_"])
        {
            if (bare.StartsWith(prefix, StringComparison.Ordinal))
            {
                bare = bare[prefix.Length..];
                break;
            }
        }

        return bare.Length == 0 ? "tag" : bare;
    }
}
