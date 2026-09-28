namespace OuraPlatform.Oura;

/// <summary>
/// One document exactly as Oura returned it. This is what lands in <c>oura_raw</c>; every typed
/// table is projected from <see cref="Payload"/> and can be rebuilt from it without re-calling the
/// API.
/// </summary>
/// <param name="DocType">Webhook <c>data_type</c> spelling — see <see cref="OuraCollection.Name"/>.</param>
/// <param name="DocId">
/// The API's <c>id</c> where there is one. Time-series rows have none, so a deterministic id is
/// synthesised from the row's timestamp: re-fetching an overlapping window collapses onto the same
/// primary key instead of duplicating.
/// </param>
/// <param name="Day">Local calendar day the document belongs to; null for time-series rows.</param>
public sealed record OuraRawDocument(string DocType, string DocId, DateOnly? Day, string Payload);

/// <summary>A closed date range, both ends inclusive.</summary>
public readonly record struct DateWindow(DateOnly Start, DateOnly End)
{
    public override string ToString() => $"{Start:yyyy-MM-dd}..{End:yyyy-MM-dd}";
}

public static class OuraWindows
{
    /// <summary>Daily documents are chunked monthly; time series weekly, because a week of
    /// heart-rate rows is already a few thousand and paging through more per request buys
    /// nothing.</summary>
    public static IEnumerable<DateWindow> Split(OuraCollection collection, DateOnly from, DateOnly to)
    {
        if (to < from)
        {
            yield break;
        }

        if (collection.Style is OuraQueryStyle.Undated or OuraQueryStyle.Singleton)
        {
            // No date filter is accepted; one pass covers everything.
            yield return new DateWindow(from, to);
            yield break;
        }

        var cursor = from;
        while (cursor <= to)
        {
            var end = collection.Style == OuraQueryStyle.TimeSeries
                ? cursor.AddDays(6)
                : cursor.AddMonths(1).AddDays(-1);

            if (end > to)
            {
                end = to;
            }

            yield return new DateWindow(cursor, end);
            cursor = end.AddDays(1);
        }
    }
}
