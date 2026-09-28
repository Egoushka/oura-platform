using Dapper;

namespace OuraPlatform.Storage.Tests;

/// <summary>
/// `context` is the table this project exists for. A duplicate here silently doubles every count it
/// is joined into, and nothing downstream would show that it happened.
/// </summary>
[Collection(TimescaleCollection.Name)]
public sealed class ContextTests(TimescaleFixture timescale) : IAsyncLifetime
{
    public Task InitializeAsync() => timescale.ResetAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    private ContextRepository Repository() => new(timescale.DataSource);

    private static ContextEvent Meeting(string id, string start, string end, string label = "Standup") =>
        new("calendar:work", id, DateTimeOffset.Parse(start), DateTimeOffset.Parse(end), "meeting", label, null);

    [Fact]
    public async Task Re_importing_the_same_feed_converges()
    {
        var repository = Repository();
        var events = new[]
        {
            Meeting("uid-1:2026-08-04T09:00:00Z", "2026-08-04T09:00:00Z", "2026-08-04T09:15:00Z"),
            Meeting("uid-2:2026-08-04T18:00:00Z", "2026-08-04T18:00:00Z", "2026-08-04T20:30:00Z", "Retro"),
        };

        await repository.UpsertAsync(events, CancellationToken.None);
        await repository.UpsertAsync(events, CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        Assert.Equal(2, await connection.ExecuteScalarAsync<int>("select count(*) from context"));
    }

    [Fact]
    public async Task An_edited_event_updates_rather_than_duplicates()
    {
        var repository = Repository();
        await repository.UpsertAsync(
            [Meeting("uid-1:2026-08-04T09:00:00Z", "2026-08-04T09:00:00Z", "2026-08-04T09:15:00Z")],
            CancellationToken.None);

        // Same occurrence, moved later and renamed.
        await repository.UpsertAsync(
            [Meeting("uid-1:2026-08-04T09:00:00Z", "2026-08-04T09:00:00Z", "2026-08-04T10:00:00Z", "Standup (long)")],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        Assert.Equal(1, await connection.ExecuteScalarAsync<int>("select count(*) from context"));
        Assert.Equal("Standup (long)", await connection.ExecuteScalarAsync<string>("select label from context"));
    }

    /// <summary>
    /// A cancelled meeting stops appearing in the feed rather than being marked deleted. Without a
    /// prune it stays in the warehouse forever, still being counted as a thing that happened.
    /// </summary>
    [Fact]
    public async Task An_event_that_left_the_feed_is_pruned()
    {
        var repository = Repository();
        await repository.UpsertAsync(
            [
                Meeting("uid-1:2026-08-04T09:00:00Z", "2026-08-04T09:00:00Z", "2026-08-04T09:15:00Z"),
                Meeting("uid-2:2026-08-04T18:00:00Z", "2026-08-04T18:00:00Z", "2026-08-04T20:30:00Z"),
            ],
            CancellationToken.None);

        // The second import only sees uid-1.
        var pruned = await repository.PruneMissingAsync(
            "calendar:work",
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-08T00:00:00Z"),
            ["uid-1:2026-08-04T09:00:00Z"],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        Assert.Equal(1, pruned);
        Assert.Equal("uid-1:2026-08-04T09:00:00Z",
            await connection.ExecuteScalarAsync<string>("select source_id from context"));
    }

    [Fact]
    public async Task Pruning_one_source_leaves_the_others_alone()
    {
        var repository = Repository();
        await repository.UpsertAsync(
            [
                Meeting("uid-1:2026-08-04T09:00:00Z", "2026-08-04T09:00:00Z", "2026-08-04T09:15:00Z"),
                new("oura_tag", "tag-1", DateTimeOffset.Parse("2026-08-04T21:00:00Z"), null, "alcohol", "wine", null),
            ],
            CancellationToken.None);

        await repository.PruneMissingAsync(
            "calendar:work",
            DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            DateTimeOffset.Parse("2026-08-08T00:00:00Z"),
            [],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        Assert.Equal("oura_tag", await connection.ExecuteScalarAsync<string>("select source from context"));
    }

    /// <summary>
    /// The whole point: a meeting from 18:00 to 20:30 does not *start* after 19:00 but certainly
    /// ends after it. `last_end_hour` is what the "did the evening run late" question needs, and it
    /// has to be local — the row is stored in UTC and Kyiv is three hours ahead in August.
    /// </summary>
    [Fact]
    public async Task Context_daily_reports_local_end_hour_and_total_hours()
    {
        await Repository().UpsertAsync(
            [
                Meeting("uid-1:2026-08-04T06:00:00Z", "2026-08-04T06:00:00Z", "2026-08-04T07:00:00Z"),
                Meeting("uid-2:2026-08-04T15:00:00Z", "2026-08-04T15:00:00Z", "2026-08-04T17:30:00Z", "Retro"),
            ],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<DailyContext>(
            """
            select day        as "Day",
                   events::int as "Events",
                   hours      as "Hours",
                   last_end_hour as "LastEndHour"
            from context_daily where kind = 'meeting'
            """);

        Assert.Equal(new DateOnly(2026, 8, 4), row.Day);
        Assert.Equal(2, row.Events);
        Assert.Equal(3.5m, row.Hours);
        // 17:30 UTC is 20:30 in Kyiv — the answer is 20, not 17.
        Assert.Equal(20m, row.LastEndHour);
    }

    [Fact]
    public async Task A_point_in_time_event_has_no_duration_and_still_counts()
    {
        await Repository().UpsertAsync(
            [new("oura_tag", "tag-1", DateTimeOffset.Parse("2026-08-04T18:00:00Z"), null, "caffeine", "espresso", null)],
            CancellationToken.None);

        await using var connection = await timescale.DataSource.OpenConnectionAsync();
        var row = await connection.QuerySingleAsync<DailyContext>(
            """
            select day as "Day", events::int as "Events", hours as "Hours", last_end_hour as "LastEndHour"
            from context_daily where kind = 'caffeine'
            """);

        Assert.Equal(1, row.Events);
        // No end time, so no hours — but it still has a position in the day.
        Assert.Null(row.Hours);
        Assert.Equal(21m, row.LastEndHour);
    }

    private sealed record DailyContext(DateOnly Day, int Events, decimal? Hours, decimal? LastEndHour);
}
