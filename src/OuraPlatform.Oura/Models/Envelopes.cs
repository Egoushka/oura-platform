namespace OuraPlatform.Oura.Models;

/// <summary>Envelope returned by every collection endpoint. Spec 1.34 removed the per-document
/// <c>meta</c> wrapper; only <c>data</c> and <c>next_token</c> remain.</summary>
public sealed record OuraPage<T>(IReadOnlyList<T> Data, string? NextToken);

/// <summary>Regularly sampled series embedded in a document (<c>met</c>, <c>hrv</c>,
/// <c>heart_rate</c>, <c>motion_count</c>).</summary>
/// <param name="Interval">Seconds between items. Sandbox returns 60; production returns 300.</param>
/// <param name="Items">One value per interval, <c>null</c> where the ring produced no reading.</param>
/// <param name="Timestamp">Instant of <c>Items[0]</c>, carrying the user's local UTC offset.</param>
public sealed record PublicSample(double Interval, IReadOnlyList<double?> Items, DateTimeOffset Timestamp);
