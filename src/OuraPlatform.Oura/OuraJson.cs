using System.Text.Json;
using System.Text.Json.Serialization;

namespace OuraPlatform.Oura;

/// <summary>
/// Single serializer configuration for everything read from the Oura API.
/// </summary>
public static class OuraJson
{
    /// <remarks>
    /// <see cref="JsonNamingPolicy.SnakeCaseLower"/> covers every property name except the ones
    /// containing digits — it renders <c>SleepPhase5Min</c> as <c>sleep_phase5_min</c>. Those few
    /// carry an explicit <see cref="JsonPropertyNameAttribute"/>.
    /// Unknown members are ignored on purpose: Oura adds fields without notice and the verbatim
    /// payload is kept in <c>oura_raw</c> regardless.
    /// </remarks>
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
