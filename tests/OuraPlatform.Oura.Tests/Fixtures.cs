using System.Collections;
using System.Text.Json;
using System.Text.Json.Serialization;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Models;

namespace OuraPlatform.Oura.Tests;

/// <summary>
/// Responses recorded from <c>https://api.ouraring.com/v2/sandbox/usercollection/*</c> on
/// 2026-08-01. Re-record with <c>tests/record-sandbox-fixtures.sh</c>.
/// </summary>
internal static class Fixtures
{
    private static readonly string Root =
        Path.Combine(AppContext.BaseDirectory, "fixtures");

    public static string Read(string relativePath) =>
        File.ReadAllText(Path.Combine(Root, relativePath));

    public static string Sandbox(OuraCollection collection) =>
        Read(Path.Combine("sandbox", $"{collection.Path}.json"));

    /// <summary>Deserializes a recorded page into <c>OuraPage&lt;TDocument&gt;</c> and returns the
    /// documents as a non-generic list, so a theory can walk every collection.</summary>
    public static IReadOnlyList<object> Documents(OuraCollection collection)
    {
        var pageType = typeof(OuraPage<>).MakeGenericType(collection.DocumentType);
        var page = JsonSerializer.Deserialize(Sandbox(collection), pageType, OuraJson.Options)
                   ?? throw new InvalidOperationException($"{collection.Name}: page deserialized to null.");

        var data = (IEnumerable)pageType.GetProperty(nameof(OuraPage<object>.Data))!.GetValue(page)!;
        return data.Cast<object>().ToArray();
    }

    /// <summary>Same options as production, except nulls are written. Used to enumerate every
    /// property the model actually knows about.</summary>
    public static readonly JsonSerializerOptions VerboseOptions = new(OuraJson.Options)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>Collections with a working sandbox endpoint and a recorded fixture.</summary>
    public static TheoryData<string> RecordedCollectionNames()
    {
        var data = new TheoryData<string>();
        foreach (var c in OuraCollections.All.Where(c => c.HasSandbox))
        {
            data.Add(c.Name);
        }

        return data;
    }
}
