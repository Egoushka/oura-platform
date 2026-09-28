using System.Text.Json;
using OuraPlatform.Oura;
using OuraPlatform.Oura.Models;

namespace OuraPlatform.Oura.Tests;

/// <summary>
/// Every model is exercised against a recorded sandbox response. These run with zero credentials
/// and zero network access.
/// </summary>
public sealed class SandboxFixtureTests
{
    /// <summary>Fields present in the sandbox payload that no model binds, with the reason.</summary>
    private static readonly Dictionary<string, string> IgnoredFields = new()
    {
        // The spec declares timestamp_unix; the sandbox sends producer_timestamp (always null)
        // instead. Neither is needed — `timestamp` is authoritative and the verbatim payload is
        // stored in oura_raw either way.
        ["producer_timestamp"] = "sandbox-only alias for the spec's timestamp_unix",
    };

    [Theory]
    [MemberData(nameof(Fixtures.RecordedCollectionNames), MemberType = typeof(Fixtures))]
    public void Fixture_deserializes_into_typed_documents(string collectionName)
    {
        var collection = OuraCollections.ByName(collectionName);

        var documents = Fixtures.Documents(collection);

        Assert.NotEmpty(documents);
        Assert.All(documents, Assert.NotNull);
    }

    /// <summary>
    /// Guards against silent spec drift: if Oura adds a field to a collection, re-recording the
    /// fixtures fails this test until the model binds it or it is added to
    /// <see cref="IgnoredFields"/>.
    /// </summary>
    [Theory]
    [MemberData(nameof(Fixtures.RecordedCollectionNames), MemberType = typeof(Fixtures))]
    public void Model_binds_every_field_the_sandbox_returns(string collectionName)
    {
        var collection = OuraCollections.ByName(collectionName);

        using var recorded = JsonDocument.Parse(Fixtures.Sandbox(collection));
        var payloadFields = recorded.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .SelectMany(document => document.EnumerateObject().Select(property => property.Name))
            .Distinct()
            .Except(IgnoredFields.Keys)
            .ToArray();

        var boundFields = Fixtures.Documents(collection)
            .SelectMany(document =>
            {
                var json = JsonSerializer.Serialize(document, collection.DocumentType, Fixtures.VerboseOptions);
                using var reserialized = JsonDocument.Parse(json);
                return reserialized.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            })
            .ToHashSet();

        var unbound = payloadFields.Where(field => !boundFields.Contains(field)).ToArray();
        Assert.Empty(unbound);
    }

    [Fact]
    public void Every_data_type_with_a_rest_endpoint_has_a_model()
    {
        // 17 webhook data types minus `meal` (no endpoint), plus `tag` and `personal_info`.
        Assert.Equal(19, OuraCollections.All.Count);
        Assert.All(OuraCollections.All, c => Assert.True(c.DocumentType.IsSealed));
        Assert.DoesNotContain(OuraCollections.All, c => c.Name == "meal");
    }

    [Fact]
    public void Vo2_max_is_named_for_the_webhook_enum_but_pathed_for_rest()
    {
        var collection = OuraCollections.ByName("vo2_max");

        Assert.Equal("vo2_max", collection.Name);
        Assert.Equal("vO2_max", collection.Path);
    }

    [Fact]
    public void Deprecated_tag_is_modelled_but_not_ingested()
    {
        Assert.Contains(OuraCollections.All, c => c.Name == "tag");
        Assert.DoesNotContain(OuraCollections.Ingested, c => c.Name == "tag");
    }

    [Fact]
    public void Personal_info_has_no_sandbox_endpoint()
    {
        Assert.False(OuraCollections.ByName("personal_info").HasSandbox);
        Assert.Contains("Not Found", Fixtures.Read(Path.Combine("errors", "personal_info_404.json")));
    }
}
