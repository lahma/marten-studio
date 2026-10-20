using System.ComponentModel;
using System.Globalization;

using Marten;
using Marten.Services;

using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>
/// Acceptance criterion 1: a theory of hostile inputs, every one refused before Marten sees it - by the text screen
/// alone, by JasperFx's parse and the studio's guard with the screen taken away, and by the whole pipeline.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three runs of one list.</b> The screen alone runs at every Marten. The other two need JasperFx's DynamicQuery,
/// so they run at the latest (<c>-p:MartenStudioMartenVersion=9.50.2</c>) and skip at the floor with that command in
/// the reason - at the floor the mode is absent, which <see cref="LinqQueryServiceTests" /> proves.
/// </para>
/// <para>
/// <b>"Before Marten sees it"</b> is checked structurally: a refusal names its stage, and only
/// <see cref="LinqStage.Screen" />, <see cref="LinqStage.Parse" /> and <see cref="LinqStage.Guard" /> are before Marten's
/// translation; a composition that reached Marten carries a command, and none here does. The probe-based proof that
/// Marten would otherwise evaluate such a subtree is <see cref="LinqExpressionGuardTests" />.
/// </para>
/// </remarks>
public class LinqHostileInputTests
{
    /// <summary>
    /// Code execution, reflection, type reach, unbounded work, the parser's own escape hatches - each as text a person
    /// could type.
    /// </summary>
    private static readonly string[] HostileTexts =
    [
        // Reaching a type and running it.
        "Name == System.IO.File.ReadAllText(\"c:/x\")",
        "Name == System.Environment.GetEnvironmentVariable(\"PATH\")",
        "Name == System.Diagnostics.Process.Start(\"calc\").ToString()",
        "Name == Convert.ToString(1)",
        "Name == Math.Max(1, 2).ToString()",
        "Name == string.Concat(\"a\", \"b\")",
        "PlacedAt < DateTime.Now",
        "Enum.Parse(\"x\") == Status",
        "StringComparer.OrdinalIgnoreCase.Equals(Name, \"x\")",
        "Uri(\"http://169.254.169.254/\") != null",

        // Reflection.
        "Name.GetType().Assembly.FullName == \"x\"",
        "Name == typeof(object).Assembly.FullName",
        "Kind.Assembly.FullName == \"x\"",
        "cast(Total, \"System.Object\") != null",
        "it is LinqOrder",
        "(it as object) != null",

        // In-process work Marten would do for a closed subtree.
        "Name == \"x\".PadLeft(2000000000)",
        "Name == \"x\".Replace(\"x\", \"y\")",
        "Lines.Select(Quantity).Sum() > 0",
        "Total == 1 * 2 * 3",

        // The parser's own escape hatches.
        "new(Name as N) != null",
        "iif(Rush, Name, Notes) == \"x\"",
        "np(Customer.Name) == \"x\"",
        "Name == @0",
        "$.Name == \"x\"",
        "Name == root.Name",
        "Name == parent.Name",
        "\"abc\"[0] == 'a'",
        "Tags[0] == \"x\"",
        "Attributes[\"key\"] == \"x\"",
        "Converted == \"x\"",
        "Name == \"x\" ? Rush : Rush",
        "(Notes ?? \"x\") == \"x\"",
        "Name & \"x\" == \"x\"",
        "\"System.DateTime\"(PlacedAt) > null",

        // A hidden type, through a navigation.
        "Secret.Code == \"x\"",
    ];

    /// <summary>Every hostile text, for the screen and for the whole pipeline.</summary>
    public static TheoryData<string> Hostile()
    {
        var data = new TheoryData<string>();

        foreach (string text in HostileTexts)
        {
            data.Add(text);
        }

        return data;
    }

    /// <summary>
    /// The list with the screen taken away. Four entries are the screen's alone and are left out here, each for a stated
    /// reason; one is added that only the guard refuses.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>
    /// <c>$</c>, <c>root</c> and <c>parent</c> are Dynamic LINQ's names for the lambda parameters: <c>$.Name</c> is
    /// <c>Name</c>. They are refused by the screen for simplicity, not because they reach anything.
    /// </description></item>
    /// <item><description>
    /// <c>Converted == "x"</c> compares a member whose type carries a converter of the host's own. Dynamic LINQ takes
    /// its class-equality branch for <c>==</c> before it would reach the converter, and the parse fails on the operand
    /// types - so this list cannot show what the parse runs. The ordering comparisons that do reach a converter, and
    /// the proof that the screen is what stops them, are <see cref="ConverterShapes" />.
    /// </description></item>
    /// </list>
    /// </remarks>
    public static TheoryData<string> HostileWithoutScreen()
    {
        string[] screenOnly = ["$.Name == \"x\"", "Name == root.Name", "Name == parent.Name", "Converted == \"x\""];

        var data = new TheoryData<string>();

        foreach (string text in HostileTexts.Where(x => !screenOnly.Contains(x)))
        {
            data.Add(text);
        }

        // The screen admits this one - every word in it is the document's or a method LINQ mode has - and the guard
        // refuses it: a method's argument must be a literal.
        data.Add("Name.ToLower().Contains(Notes)");

        return data;
    }

    [Theory]
    [MemberData(nameof(Hostile))]
    public void The_screen_alone_refuses_every_hostile_input(string text)
    {
        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.Where, text, Graph());

        refusal.Should().NotBeNull("'{0}' must not reach a parser", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
    }

    /// <summary>
    /// The same list with the screen taken away: straight into JasperFx's parse, with the studio's guard as the first
    /// rule of its policy and the composed walk after it. Every one is refused at the parse or by the guard - which
    /// says nothing about what the parse ran on the way: that is the screen's, and
    /// <see cref="Without_the_screen_the_parser_runs_a_host_converter_on_the_visitors_text" /> is the proof.
    /// </summary>
    [DynamicLinqTheory]
    [MemberData(nameof(HostileWithoutScreen))]
    public void With_no_screen_JasperFx_and_the_guard_still_refuse_every_hostile_input(string text)
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        IQueryable<LinqOrder> source = session.Query<LinqOrder>();
        LinqExpressionGuard guard = LinqTestStore.Guard<LinqOrder>(false, typeof(LinqSecret));

        DynamicLinqApplied<LinqOrder> applied = DynamicLinqAdapter.Current!.Apply(source, text, null, guard);

        string? refusal = applied.Refusal?.Message
                          ?? guard.CheckComposed(applied.Query!.Expression, source.Expression);

        refusal.Should().NotBeNull("'{0}' must be refused by JasperFx's parse or by the guard", text);
    }

    // ------------------------------------------------------------------------------------------------
    // The F7 security review's B1: a host converter run at parse time through a name the graph left out
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// Names that are allowed somewhere in the graph, bound by the parser to a member the graph left out for its
    /// converter: the same name on a nested type (<c>Code</c>), a framework member's name on an object
    /// (<c>Value</c>), and a strongly-typed id on the root beside a nested entity's plain <c>Guid Id</c>.
    /// </summary>
    /// <remarks>
    /// Ordering comparisons, because those are what reach the converter: Dynamic LINQ's <c>==</c> between a class and
    /// a string takes its class-equality branch first. <see cref="ScreenOnlyShapes" /> adds the equalities, which the
    /// screen refuses just the same.
    /// </remarks>
    public static TheoryData<string, string> ConverterShapes() => new()
    {
        { nameof(LinqCollide), "Code > \"probe\"" },
        { nameof(LinqCollide), "Code < \"probe\"" },
        { nameof(LinqCollide), "Holder.Code == \"x\" or Code > \"probe\"" },

        // Contains reads its argument against the document, not the element - whose Code is allowed.
        { nameof(LinqCollide), "Holders.Contains(Code > \"probe\")" },
        { nameof(LinqValueHolder), "it.Value > \"probe\"" },
        { nameof(LinqValueHolder), "Value > \"probe\"" },
        { nameof(LinqTypedRoot), "Id > \"probe\"" },
        { nameof(LinqTypedRoot), "Lines.Any(Id == \"8f1d5a6e-1a2b-4c3d-9e8f-000000000001\") and Id > \"probe\"" },
    };

    /// <summary>Every converter shape, and the equalities beside them.</summary>
    public static TheoryData<string, string> ScreenOnlyShapes()
    {
        TheoryData<string, string> data = ConverterShapes();

        data.Add(nameof(LinqCollide), "Code == \"probe\"");
        data.Add(nameof(LinqValueHolder), "it.Value == \"probe\"");
        data.Add(nameof(LinqTypedRoot), "Id == \"probe\"");
        data.Add(nameof(LinqTypedRoot), "it.ID != null");

        return data;
    }

    /// <summary>
    /// The anti-vacuity half: with the screen taken away, the parse alone runs the host's converter on these texts -
    /// before JasperFx's allow-list or the studio's guard sees anything. So the screen, and nothing after it, is what
    /// keeps the count at zero in the two tests below.
    /// </summary>
    [DynamicLinqTheory]
    [MemberData(nameof(ConverterShapes))]
    public void Without_the_screen_the_parser_runs_a_host_converter_on_the_visitors_text(string document, string text)
    {
        int before = LinqCodeConverter.Conversions;

        ParseWithoutScreen(document, text);

        LinqCodeConverter.Conversions.Should().BeGreaterThan(before,
            "'{0}' binds a member the graph left out, and Dynamic LINQ converts the literal through its type while it parses", text);
    }

    [Theory]
    [MemberData(nameof(ScreenOnlyShapes))]
    public void The_screen_binds_each_name_where_the_parser_would_and_refuses_a_member_the_graph_left_out(string document, string text)
    {
        int before = LinqCodeConverter.Conversions;

        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.Where, text, LinqDocumentGraph.For(DocumentType(document), new HashSet<Type>()));

        refusal.Should().NotBeNull("'{0}' names a member the graph left out on the type it binds on", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
        refusal.Message.Should().NotContain(nameof(LinqCode), "a refusal never names a type the graph excludes");
        LinqCodeConverter.Conversions.Should().Be(before);
    }

    /// <summary>The whole pipeline, as the service runs it: refused at the screen, and the converter never runs.</summary>
    [DynamicLinqTheory]
    [InlineData(nameof(LinqCollide), "Code > \"probe\"")]
    [InlineData(nameof(LinqCollide), "Code < \"probe\"")]
    [InlineData(nameof(LinqValueHolder), "it.Value > \"probe\"")]
    [InlineData(nameof(LinqValueHolder), "Value > \"probe\"")]
    [InlineData(nameof(LinqStructHolder), "Code == \"probe\"")]
    [InlineData(nameof(LinqStructHolder), "Code != \"probe\"")]
    [InlineData(nameof(LinqStructHolder), "\"probe\" == Code")]
    public void The_whole_pipeline_never_runs_a_host_converter_on_the_visitors_text(string document, string text)
    {
        int before = LinqCodeConverter.Conversions;
        int structBefore = LinqStructCodeConverter.Conversions;

        ComposedLinqQuery? composed = Pipeline(DocumentType(document), text, out LinqRefusal? screened);

        (screened ?? composed?.Refusal).Should().NotBeNull("'{0}' must be refused", text);
        screened!.Stage.Should().Be(LinqStage.Screen);
        composed.Should().BeNull("nothing is parsed once the screen has refused");
        LinqCodeConverter.Conversions.Should().Be(before, "a host converter must never run on visitor text");
        LinqStructCodeConverter.Conversions.Should().Be(structBefore, "a struct's no more than a class's");
    }

    /// <summary>The control for the shapes above: the name the graph does allow, on the type that has it, goes through.</summary>
    [DynamicLinqTheory]
    [InlineData(nameof(LinqCollide), "Holder.Code > \"probe\"")]
    [InlineData(nameof(LinqCollide), "Holders.Any(Code > \"probe\")")]
    [InlineData(nameof(LinqValueHolder), "Name > \"probe\"")]
    public void The_allowed_member_of_the_same_name_still_reaches_Marten(string document, string text)
    {
        int before = LinqCodeConverter.Conversions;

        ComposedLinqQuery? composed = Pipeline(DocumentType(document), text, out LinqRefusal? screened);

        screened.Should().BeNull();
        composed!.Refusal.Should().BeNull();
        composed.Command.Should().NotBeNull();
        LinqCodeConverter.Conversions.Should().Be(before);
    }

    /// <summary>
    /// The type-name shorthand: Dynamic LINQ reads a predefined type's name followed by a string as a conversion of
    /// the string (<c>ParseTypeAccess</c>), running a framework converter at parse time. Inside <c>Lines.Any(…)</c>,
    /// <c>Uri</c> binds on the line - which has none - so the parser would take it for the type; the screen binds it
    /// there too and refuses it, and refuses two values in a row wherever they are.
    /// </summary>
    [Theory]
    [InlineData("Lines.Any(Uri \"http://example.invalid/\" != null)", 10)]
    [InlineData("Uri \"http://example.invalid/\" != null", 4)]
    [InlineData("Lines.Any(Sku \"http://example.invalid/\" != null)", 14)]
    public void The_type_name_shorthand_never_reaches_the_parser(string text, int position)
    {
        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.Where, text, LinqDocumentGraph.For(typeof(LinqShorthand), new HashSet<Type>()));

        refusal.Should().NotBeNull("'{0}' converts a string through a type name", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
        refusal.Position.Should().Be(position);
    }

    /// <summary>
    /// SEC1 (b): a <em>value-type</em> member whose type carries a converter of the host's own, compared with <c>==</c>. A
    /// class compared with a string under <c>==</c> takes Dynamic LINQ's class-equality branch and never meets a converter
    /// (1.7.4, <c>ExpressionParser.ParseComparisonOperator</c>, the first branch); a struct skips that branch and is
    /// converted through <c>TypeDescriptor.GetConverter(memberType)</c> while it parses (the branch after the enums). So
    /// <c>Code == "probe"</c> on a struct is the equality shape <see cref="ConverterShapes" /> cannot cover with a class.
    /// </summary>
    public static TheoryData<string> StructConverterShapes() => ["Code == \"probe\"", "Code != \"probe\"", "\"probe\" == Code"];

    /// <summary>The anti-vacuity half: without the screen, the parse runs the host's converter on a struct member's literal.</summary>
    [DynamicLinqTheory]
    [MemberData(nameof(StructConverterShapes))]
    public void Without_the_screen_the_parser_runs_a_struct_members_converter_on_an_equality(string text)
    {
        int before = LinqStructCodeConverter.Conversions;

        try
        {
            ParseWithoutScreen(nameof(LinqStructHolder), text);
        }
        catch (System.Reflection.TargetInvocationException)
        {
            // Whatever the allow-list says after the parse, the conversion has already happened by then.
        }

        LinqStructCodeConverter.Conversions.Should().BeGreaterThan(before,
            "'{0}' compares a struct member with a string, and Dynamic LINQ converts the literal through the struct's converter while it parses", text);
    }

    /// <summary>And the screen refuses every one, with a nullable of the struct beside them, and the converter never runs.</summary>
    [Theory]
    [MemberData(nameof(StructConverterShapes))]
    [InlineData("MaybeCode == \"probe\"")]
    [InlineData("MaybeCode.Value == \"probe\"")]
    public void A_struct_member_with_a_host_converter_is_never_reachable(string text)
    {
        int before = LinqStructCodeConverter.Conversions;

        LinqDocumentGraph graph = LinqDocumentGraph.For(typeof(LinqStructHolder), new HashSet<Type>());
        LinqRefusal? refusal = LinqTextScreen.Check(LinqClause.Where, text, graph);

        refusal.Should().NotBeNull("'{0}' names a member whose type the graph leaves out for its converter", text);
        refusal!.Stage.Should().Be(LinqStage.Screen);
        refusal.Message.Should().NotContain(nameof(LinqStructCode), "a refusal never names a type the graph excludes");
        LinqDocumentGraph.Classify(typeof(LinqStructCode)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(LinqStructCode?)).Should().Be(LinqTypeKind.Excluded);
        LinqStructCodeConverter.Conversions.Should().Be(before);

        LinqTextScreen.Check(LinqClause.Where, "Name == \"probe\"", graph)
            .Should().BeNull("the control: the same document's plain member goes through");
    }

    private static Type DocumentType(string name) => name switch
    {
        nameof(LinqCollide) => typeof(LinqCollide),
        nameof(LinqValueHolder) => typeof(LinqValueHolder),
        nameof(LinqTypedRoot) => typeof(LinqTypedRoot),
        nameof(LinqStructHolder) => typeof(LinqStructHolder),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, null),
    };

    /// <summary>JasperFx's parse over an in-memory source, with the studio's rules but no screen - what the parser itself does.</summary>
    private static void ParseWithoutScreen(string document, string text)
    {
        Type type = DocumentType(document);

        typeof(LinqHostileInputTests)
            .GetMethod(nameof(ParseWithoutScreenOf), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .MakeGenericMethod(type)
            .Invoke(null, [text]);
    }

    private static void ParseWithoutScreenOf<T>(string text)
    {
        LinqDocumentGraph graph = LinqDocumentGraph.For(typeof(T), new HashSet<Type>());
        var guard = new LinqExpressionGuard(graph, static _ => null, jsonEnumsAsName: false);

        DynamicLinqAdapter.Current!.Apply(Array.Empty<T>().AsQueryable(), text, null, guard);
    }

    /// <summary>
    /// And the whole pipeline - screen, parse, guard, Marten's translation - as the service runs it, with no database:
    /// every hostile input stops before Marten, so no statement is built.
    /// </summary>
    [DynamicLinqTheory]
    [MemberData(nameof(Hostile))]
    public void The_whole_pipeline_refuses_every_hostile_input_before_Marten(string text)
    {
        ComposedLinqQuery? composed = Pipeline(text, out LinqRefusal? screened);
        LinqRefusal? refusal = screened ?? composed?.Refusal;

        refusal.Should().NotBeNull("'{0}' must be refused", text);
        refusal!.Stage.Should().BeOneOf([LinqStage.Screen, LinqStage.Parse, LinqStage.Guard], "a hostile input never reaches Marten's translation");
        composed?.Command.Should().BeNull("no statement is built for a refused input");
    }

    /// <summary>The control: ordinary predicates go all the way through to Marten's SQL, so the theory above is not vacuous.</summary>
    [DynamicLinqTheory]
    [InlineData("Name == \"a\" and Total > 100")]
    [InlineData("Customer.Name.StartsWith(\"A\")")]
    [InlineData("Lines.Any(Quantity > 5)")]
    [InlineData("Tags.Any(it == \"vip\")")]
    [InlineData("Status == \"Shipped\"")]
    [InlineData("Notes != null and Notes != \"x\"")]
    [InlineData("PlacedAt > \"2024-01-01\"")]
    [InlineData("Status in (\"Open\", \"Shipped\")")]
    [InlineData("Name not in (\"a\", \"b\")")]
    [InlineData("not (Rush or Total > 5)")]
    public void An_ordinary_predicate_reaches_Marten_and_comes_back_as_SQL(string text)
    {
        ComposedLinqQuery? composed = Pipeline(text, out LinqRefusal? screened);

        screened.Should().BeNull();
        composed.Should().NotBeNull();
        composed!.Refusal.Should().BeNull("'{0}' is the dialect the studio offers", text);
        composed.Command.Should().NotBeNull();
        composed.Command!.CommandText.Should().Contain("mt_doc_linqorder");
    }

    /// <summary>
    /// A date with no offset is the UTC date it names, on whatever time zone this server is in. JasperFx 2.84 to 2.90.2
    /// read it in the server's zone (see <see cref="DynamicLinqAdapter.ReadsDatesInServerTime" />); on a host east or
    /// west of Greenwich - this is the test that fails there without the correction - the bound value moved by the
    /// zone's offset. JasperFx 2.91.0 reads it as UTC itself, and there this is the test that fails if the studio
    /// corrected it a second time. On a UTC host it passes either way.
    /// </summary>
    [DynamicLinqTheory]
    [InlineData("PlacedAt > \"2024-01-01\"", "2024-01-01T00:00:00")]
    [InlineData("PlacedAt > \"2024-07-01T09:30:00\"", "2024-07-01T09:30:00")]
    [InlineData("PlacedAt > \"2024-07-01T09:30:00Z\"", "2024-07-01T09:30:00")]
    [InlineData("PlacedAt > \"2024-07-01T09:30:00+02:00\"", "2024-07-01T07:30:00")]
    public void A_date_literal_is_bound_as_the_UTC_date_it_names(string text, string expected)
    {
        ComposedLinqQuery? composed = Pipeline(text, out LinqRefusal? screened);

        screened.Should().BeNull();
        composed!.Refusal.Should().BeNull();

        DateTime bound = composed.Command!.Parameters.Select(static x => x.Value).OfType<DateTime>().Single();

        bound.Should().Be(DateTime.Parse(expected, System.Globalization.CultureInfo.InvariantCulture));
        bound.Kind.Should().Be(DateTimeKind.Unspecified, "a DateTime member is bound as timestamp without time zone");
    }

    /// <summary>
    /// Two literals the server's zone reads as one instant but that name different ones cannot be told apart once
    /// parsed, so the predicate is refused rather than one of them guessed - on any host that is not on UTC.
    /// </summary>
    [Fact]
    public void Two_dates_the_server_reads_as_one_instant_are_refused_rather_than_guessed()
    {
        DateTime read = DateTimeOffset.Parse("2024-01-01", System.Globalization.CultureInfo.InvariantCulture).UtcDateTime;
        string same = read.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

        Dictionary<DateTime, DateTime>? corrections =
            LinqQueryComposer.DateLiterals.Corrections("PlacedAt > \"2024-01-01\" or PlacedAt == \"" + same + "\"");

        bool serverOnUtc = read == new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        (corrections is null).Should().Be(!serverOnUtc,
            "on this host \"2024-01-01\" reads as {0:o}, which is also what \"{1}\" names", read, same);
    }

    private static LinqDocumentGraph Graph() => LinqTestStore.Graph<LinqOrder>(typeof(LinqSecret));

    private static ComposedLinqQuery? Pipeline(string text, out LinqRefusal? screened) =>
        Pipeline(typeof(LinqOrder), text, out screened, typeof(LinqSecret));

    private static ComposedLinqQuery? Pipeline(Type document, string text, out LinqRefusal? screened, params Type[] hidden)
    {
        LinqDocumentGraph graph = LinqDocumentGraph.For(document, new HashSet<Type>(hidden));

        screened = LinqTextScreen.Check(LinqClause.Where, text, graph);
        if (screened is not null)
        {
            return null;
        }

        using DocumentStore store = LinqTestStore.Create();

        return LinqQueryComposer.Compose(new LinqComposition(
            store,
            SessionOptions.ForDatabase(store.Storage.Database),
            document,
            DynamicLinqAdapter.Current!,
            new LinqExpressionGuard(graph, static _ => null, jsonEnumsAsName: false),
            text,
            string.Empty,
            DeletedFilter.Exclude,
            CrossTenant: false,
            Offset: 0,
            PageSize: 50));
    }
}

/// <summary>A value with a converter of the host's own, which counts every conversion it is asked for.</summary>
[TypeConverter(typeof(LinqCodeConverter))]
public sealed class LinqCode
{
    public string Text { get; set; } = string.Empty;
}

/// <summary>Counts every conversion - only <see cref="LinqHostileInputTests" /> uses it, so its tests see each other's alone, in order.</summary>
public sealed class LinqCodeConverter : TypeConverter
{
    private static int conversions;

    public static int Conversions => Volatile.Read(ref conversions);

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        Interlocked.Increment(ref conversions);
        return new LinqCode { Text = value as string ?? string.Empty };
    }
}

/// <summary>A nested object whose <c>Code</c> is plain text - and so allowed, by that name, in the graph.</summary>
public sealed class LinqHolder
{
    public string Code { get; set; } = string.Empty;
}

/// <summary>A document whose own <c>Code</c> the graph leaves out for its converter, while its holder's is allowed.</summary>
public sealed class LinqCollide
{
    public Guid Id { get; set; }

    public LinqCode Code { get; set; } = new();

    public LinqHolder Holder { get; set; } = new();

    public List<LinqHolder> Holders { get; set; } = [];
}

/// <summary>A document whose left-out member is named like a framework member: a nullable's <c>Value</c>.</summary>
public sealed class LinqValueHolder
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public LinqCode Value { get; set; } = new();
}

/// <summary>A value type with a converter of the host's own - the struct twin of <see cref="LinqCode" /> (SEC1 b).</summary>
[TypeConverter(typeof(LinqStructCodeConverter))]
public readonly record struct LinqStructCode(string Text);

/// <summary>Counts every conversion to <see cref="LinqStructCode" />; only the struct shapes use it.</summary>
public sealed class LinqStructCodeConverter : TypeConverter
{
    private static int conversions;

    public static int Conversions => Volatile.Read(ref conversions);

    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType) => sourceType == typeof(string);

    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        Interlocked.Increment(ref conversions);
        return new LinqStructCode(value as string ?? string.Empty);
    }
}

/// <summary>A document whose struct-typed members the graph leaves out for their converter, beside a plain one.</summary>
public sealed class LinqStructHolder
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public LinqStructCode Code { get; set; }

    public LinqStructCode? MaybeCode { get; set; }
}

/// <summary>An element with a plain <c>Guid Id</c>.</summary>
public sealed class LinqTypedLine
{
    public Guid Id { get; set; }
}

/// <summary>A strongly-typed id with a converter on the root, beside nested entities with a <c>Guid Id</c> - a common shape.</summary>
public sealed class LinqTypedRoot
{
    public LinqCode Id { get; set; } = new();

    public List<LinqTypedLine> Lines { get; set; } = [];
}

/// <summary>A member named like a predefined type, and a collection whose element has no such member.</summary>
public sealed class LinqShorthand
{
    public Guid Id { get; set; }

    public string Uri { get; set; } = string.Empty;

    public List<LinqLine> Lines { get; set; } = [];
}
