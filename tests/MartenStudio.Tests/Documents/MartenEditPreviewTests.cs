using Marten;

using MartenStudio.Services.Configuration;
using MartenStudio.Services.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// M5: Marten's own preview of a save (<c>PreviewDocumentJsonAsync</c>, 9.53+) laid beside the studio's round trip, with no
/// database.
/// </summary>
/// <remarks>
/// <para>
/// The store points at a host that does not resolve. Marten's preview opens no session and no connection (read at
/// v9.53.0 and v9.54.2), so every answer here that is not a refusal is also the proof of that: a preview that tried to
/// connect would come back <see cref="MartenPreviewVerdict.Refused" /> with a socket error, never
/// <see cref="MartenPreviewVerdict.Agrees" />.
/// </para>
/// <para>
/// <b>Both ends.</b> At the floor every call is <see cref="MartenPreviewVerdict.Unavailable" /> with the line that names the
/// Marten it needs; from 9.53 the other verdicts apply. Each test skips at the end it does not describe, as the LINQ
/// mode's do.
/// </para>
/// </remarks>
public class MartenEditPreviewTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-preview-test.invalid;Database=none;Username=none;Password=none";

    private const string Id = "6b1e9c3e-0f2a-4f43-9d8e-0c5d4b1a7e21";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static bool Available => RuntimeFeature.DocumentEditPreview.IsAvailable(MartenRuntime.Current);

    [Fact]
    public async Task Below_9_53_there_is_no_preview_and_the_line_names_the_Marten_it_needs()
    {
        Assert.SkipWhen(Available, "This run resolved a Marten with its own preview; the absent case is the floor's.");

        using DocumentStore store = Store();

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(
            store, typeof(PreviewedThing), Id, Json(Id, "a"), Json(Id, "a"), Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Unavailable);
        check.IsDisagreement.Should().BeFalse("no answer is not a disagreement, and must not hold a save at the dialog");
        check.MartenJson.Should().BeNull();
        check.Reason.Should().Be(
            "Marten's own preview of a save (PreviewDocumentJsonAsync) needs Marten 9.53+; this host runs "
            + MartenRuntime.Current.Describe(RuntimePackage.Marten) + ".");
        MartenEditPreview.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public async Task From_9_53_an_edit_Marten_keeps_as_the_round_trip_does_agrees()
    {
        Assert.SkipUnless(Available, "This run resolved a Marten without its own preview (below 9.53).");

        using DocumentStore store = Store();
        string edited = Json(Id, "renamed");
        string roundTrip = store.Options.Serializer().ToJson(new PreviewedThing { Id = Guid.Parse(Id), Name = "renamed" });

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(store, typeof(PreviewedThing), Id, edited, roundTrip, Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Agrees, check.Reason);
        check.Differences.IsEmpty.Should().BeTrue();
        check.MartenJson.Should().Contain("renamed");
    }

    /// <summary>
    /// A property the CLR type has no member for is dropped by both - the round trip's D7 loss, which Marten's own
    /// preview confirms rather than contradicts.
    /// </summary>
    [Fact]
    public async Task From_9_53_a_property_the_type_does_not_have_is_dropped_by_both()
    {
        Assert.SkipUnless(Available, "This run resolved a Marten without its own preview (below 9.53).");

        using DocumentStore store = Store();
        string edited = $$"""{"Id":"{{Id}}","Name":"a","Legacy":42}""";
        string roundTrip = store.Options.Serializer().ToJson(new PreviewedThing { Id = Guid.Parse(Id), Name = "a" });

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(store, typeof(PreviewedThing), Id, edited, roundTrip, Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Agrees, check.Reason);
        check.MartenJson.Should().NotContain("Legacy");
    }

    /// <summary>
    /// An edit that changes the document's id: Marten's save would refuse it, and so its preview does. (The studio's own
    /// preview refuses it first, at every Marten - <c>DocumentIdChangeLiveTests</c>; this holds the invoker's refusal path.)
    /// </summary>
    [Fact]
    public async Task From_9_53_an_edit_that_changes_the_id_is_refused_and_flagged()
    {
        Assert.SkipUnless(Available, "This run resolved a Marten without its own preview (below 9.53).");

        using DocumentStore store = Store();
        string other = Guid.NewGuid().ToString();
        string edited = Json(other, "a");

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(store, typeof(PreviewedThing), Id, edited, edited, Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Refused);
        check.IsDisagreement.Should().BeTrue();
        check.Reason.Should().StartWith("ArgumentException: ").And.Contain("does not match the requested id");
    }

    /// <summary>
    /// The comparison itself: whatever the studio's round trip says that Marten's does not is a disagreement, with the
    /// differences named.
    /// </summary>
    [Fact]
    public async Task From_9_53_a_round_trip_that_differs_from_Martens_disagrees_and_says_where()
    {
        Assert.SkipUnless(Available, "This run resolved a Marten without its own preview (below 9.53).");

        using DocumentStore store = Store();
        string edited = Json(Id, "a");
        string notWhatMartenKeeps = $$"""{"Id":"{{Id}}","Name":"b"}""";

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(
            store, typeof(PreviewedThing), Id, edited, notWhatMartenKeeps, Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Disagrees);
        check.IsDisagreement.Should().BeTrue();
        check.Differences.Changed.Select(static x => x.Path).Should().ContainSingle().Which.Should().Contain("Name");
    }

    [Fact]
    public async Task From_9_53_JSON_the_serializer_rejects_is_a_refusal_not_an_exception()
    {
        Assert.SkipUnless(Available, "This run resolved a Marten without its own preview (below 9.53).");

        using DocumentStore store = Store();
        string edited = $$$"""{"Id":"{{{Id}}}","Name":{"not":"a string"}}""";

        MartenPreviewCheck check = await MartenEditPreview.CheckAsync(store, typeof(PreviewedThing), Id, edited, edited, Token);

        check.Verdict.Should().Be(MartenPreviewVerdict.Refused);
        check.Reason.Should().NotBeNullOrWhiteSpace();
        check.Reason!.Length.Should().BeLessThanOrEqualTo(MartenEditPreview.MaxReasonLength + 64);
    }

    private static string Json(string id, string name) => $$"""{"Id":"{{id}}","Name":"{{name}}"}""";

    private static DocumentStore Store() => DocumentStore.For(options =>
    {
        options.Connection(DummyConnectionString);
        options.DatabaseSchemaName = "m5_preview";
        options.Schema.For<PreviewedThing>();
    });

    /// <summary>A plain document type, Guid id.</summary>
    public sealed class PreviewedThing
    {
        /// <summary>The identity.</summary>
        public Guid Id { get; set; }

        /// <summary>Something to edit.</summary>
        public string Name { get; set; } = string.Empty;
    }
}
