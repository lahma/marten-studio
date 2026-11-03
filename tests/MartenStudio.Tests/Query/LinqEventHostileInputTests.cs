using Marten;

using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// The hostile-input theory over the event log (1.1): every input is refused before Marten sees it - by the text screen
/// alone, by JasperFx's parse and the studio's guard with the screen taken away, and by the whole pipeline - and a control
/// list shows ordinary predicates do reach SQL, so the theory is not vacuous.
/// </summary>
/// <remarks>
/// The graph is the one the service builds for a conjoined store with correlation, causation and user name recorded and
/// tenant <c>a</c> selected: <c>TenantId</c> is withheld because Marten already narrows to the tenant, and so are
/// <c>IsArchived</c>, <c>StreamKey</c>, <c>IsSkipped</c> and <c>AggregateTypeName</c> - each of which Marten would read from
/// the body or silently ignore, and which therefore must be refused as members, by both walls.
/// </remarks>
public class LinqEventHostileInputTests
{
    private const string Tenant = "a";

    private static readonly string[] HostileTexts =
    [
        // Reaching a type and running it.
        "EventTypeName == System.IO.File.ReadAllText(\"c:/x\")",
        "EventTypeName == System.Environment.GetEnvironmentVariable(\"PATH\")",
        "EventTypeName == Convert.ToString(1)",
        "Timestamp < DateTime.Now",
        "Timestamp < DateTimeOffset.UtcNow",
        "Uri(\"http://169.254.169.254/\") != null",

        // Reflection.
        "EventTypeName.GetType().Assembly.FullName == \"x\"",
        "EventType.Assembly.FullName == \"x\"",
        "cast(Version, \"System.Object\") != null",
        "it is IEvent",
        "(it as object) != null",

        // The members Marten keeps no column for, or a filter of its own for: refused as members.
        "IsArchived",
        "IsArchived == true",
        "not IsArchived",
        "IsSkipped",
        "AggregateTypeName == \"x\"",
        "StreamKey == \"x\"",
        "TenantId == \"b\"",
        "Data.ToString() == \"x\"",
        "Headers[\"a\"] == \"x\"",
        "Tags.Any(it == \"x\")",

        // Marten's own extension methods, which Dynamic LINQ cannot reach and the screen does not know.
        "AnyTenant()",
        "AnyTenant() == true",
        "TenantIsOneOf(\"a\", \"b\")",
        "MaybeArchived()",
        "HasTag(\"x\")",

        // The parser's own escape hatches.
        "iif(Version > 1, EventTypeName, UserName) == \"x\"",
        "new(EventTypeName as N) != null",
        "EventTypeName == @0",
        "np(UserName) == \"x\"",
        "EventTypeName == \"x\" ? Version > 1 : Version > 2",
        "(UserName ?? \"x\") == \"x\"",
        "\"System.DateTime\"(Timestamp) > null",

        // In-process work Marten would do for a closed subtree.
        "EventTypeName == \"x\".PadLeft(2000000000)",
        "EventTypeName == \"x\".Replace(\"x\", \"y\")",
        "Version == 1 * 2 * 3",
    ];

    public static TheoryData<string> Hostile()
    {
        var data = new TheoryData<string>();

        foreach (string text in HostileTexts)
        {
            data.Add(text);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void The_screen_alone_refuses_every_hostile_input(string text)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqRefusal? refusal = LinqEventTestSupport.Screen(store, Tenant, text);

        refusal.Should().NotBeNull("'{0}' must not reach a parser", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
    }

    /// <summary>
    /// The screen taken away: JasperFx's parse and the studio's guard, on the same restricted graph, are what refuse - the
    /// second wall behind the first. A member the graph withholds is refused by the guard as surely as by the screen.
    /// </summary>
    [DynamicLinqTheory]
    [MemberData(nameof(Hostile))]
    public void Parse_and_guard_without_the_screen_refuse_every_hostile_input(string text)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, Tenant, text);

        composed.Refusal.Should().NotBeNull("'{0}' must be refused with the screen taken away", text);
        composed.Refusal!.Stage.Should().BeOneOf([LinqStage.Parse, LinqStage.Guard], "a hostile input never reaches Marten's translation");
        composed.Command.Should().BeNull("no statement is built for a refused input");
    }

    [DynamicLinqTheory]
    [MemberData(nameof(Hostile))]
    public void The_whole_pipeline_refuses_every_hostile_input_before_Marten(string text)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqRefusal? screened = LinqEventTestSupport.Screen(store, Tenant, text);
        ComposedLinqQuery? composed = screened is null ? LinqEventTestSupport.Compose(store, Tenant, text) : null;
        LinqRefusal? refusal = screened ?? composed?.Refusal;

        refusal.Should().NotBeNull("'{0}' must be refused", text);
        refusal!.Stage.Should().BeOneOf([LinqStage.Screen, LinqStage.Parse, LinqStage.Guard]);
        composed?.Command.Should().BeNull();
    }

    [DynamicLinqTheory]
    [InlineData("Sequence > 100 and EventTypeName == \"order_placed\"")]
    [InlineData("EventTypeName.StartsWith(\"order\") or EventTypeName.EndsWith(\"shipped\")")]
    [InlineData("Timestamp >= \"2026-11-01T00:00:00Z\" and Timestamp < \"2026-11-02T00:00:00Z\"")]
    [InlineData("StreamId == \"aaaaaaaa-0000-0000-0000-000000000001\"")]
    [InlineData("CorrelationId != null and UserName == \"ada\"")]
    [InlineData("EventTypeName in (\"a\", \"b\")")]
    [InlineData("not (Version > 3 or Version < 1)")]
    public void An_ordinary_predicate_reaches_Marten_and_comes_back_as_SQL(string text)
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqEventTestSupport.Screen(store, Tenant, text).Should().BeNull();

        ComposedLinqQuery composed = LinqEventTestSupport.Compose(store, Tenant, text);

        composed.Refusal.Should().BeNull("'{0}' is the dialect the studio offers", text);
        composed.Command!.CommandText.Should().Contain("mt_events");
    }

    /// <summary>
    /// A host that has the extension methods in scope cannot use them: Dynamic LINQ has no custom types, so a method with no
    /// receiver is refused by the screen with its own sentence.
    /// </summary>
    [Fact]
    public void A_method_with_no_receiver_is_refused_with_the_screens_own_sentence()
    {
        using DocumentStore store = LinqEventTestSupport.ConjoinedStore();

        LinqRefusal? refusal = LinqEventTestSupport.Screen(store, Tenant, "AnyTenant()");

        refusal!.Message.Should().Contain("calls 'AnyTenant' with no receiver");
        refusal.Clause.Should().Be(LinqClause.Where);
    }
}
