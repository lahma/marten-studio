using System.ComponentModel;
using System.Text.Json;

using MartenStudio.Services.Query;

namespace MartenStudio.Tests.Query;

/// <summary>A chain of nested objects deeper than the graph reaches.</summary>
public sealed class LinqDeep
{
    public Guid Id { get; set; }

    public LinqDeep1 One { get; set; } = new();
}

public sealed class LinqDeep1
{
    public string Level1 { get; set; } = string.Empty;

    public LinqDeep2 Two { get; set; } = new();
}

public sealed class LinqDeep2
{
    public string Level2 { get; set; } = string.Empty;

    public LinqDeep3 Three { get; set; } = new();
}

public sealed class LinqDeep3
{
    public string Level3 { get; set; } = string.Empty;

    public LinqDeep4 Four { get; set; } = new();
}

public sealed class LinqDeep4
{
    public string Level4 { get; set; } = string.Empty;

    public LinqDeep5 Five { get; set; } = new();
}

public sealed class LinqDeep5
{
    public string Level5 { get; set; } = string.Empty;
}

/// <summary>A base type whose <c>Code</c> is plain text.</summary>
public class LinqBaseWithCode
{
    public Guid Id { get; set; }

    public string Code { get; set; } = string.Empty;

    public static string Shared { get; set; } = string.Empty;
}

/// <summary>A derived document that hides the base's <c>Code</c> with one of a type the graph leaves out.</summary>
public sealed class LinqDerivedHidingCode : LinqBaseWithCode
{
    public new LinqCode Code { get; set; } = new();

    public string Name { get; set; } = string.Empty;
}

/// <summary>A type nothing but <see cref="LinqDocumentGraphTests.A_type_given_a_converter_at_run_time_is_left_out_too" /> touches.</summary>
public sealed class LinqRuntimeConverted
{
    public string Value { get; set; } = string.Empty;
}

/// <summary>Two members whose names differ only in case, which Dynamic LINQ cannot tell apart.</summary>
#pragma warning disable CA1708 // the case-only difference is the point of this type
public sealed class LinqAmbiguous
#pragma warning restore CA1708
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string NAME { get; set; } = string.Empty;

    public string Other { get; set; } = string.Empty;
}

/// <summary>
/// The members a predicate may name: the document's own graph, and nothing a predicate has no business reaching.
/// </summary>
public class LinqDocumentGraphTests
{
    [Fact]
    public void The_graph_is_the_documents_own_members_and_those_of_what_it_holds()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqOrder>();

        graph.MemberNames.Should().Contain(
        [
            "Id", "Name", "Notes", "Total", "Status", "PlacedAt", "Rush", "Priority", "Customer", "Tags", "Lines", "Secret",
            "City", "Sku", "Quantity", "Code",
        ]);
        graph.MemberNames.Contains("name").Should().BeTrue("names compare without case, as Dynamic LINQ's do");
    }

    [Fact]
    public void Framework_types_objects_dictionaries_and_types_with_converters_of_their_own_are_left_out()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqOrder>();

        graph.MemberNames.Should().NotContain(["Kind", "Payload", "Attributes", "Raw", "Converted", "Value"]);

        LinqDocumentGraph.Classify(typeof(Type)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(object)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(JsonElement)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(Dictionary<string, string>)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(List<List<string>>)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(LinqConverted)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(Func<string>)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(System.Linq.Expressions.Expression)).Should().Be(LinqTypeKind.Excluded);
        LinqDocumentGraph.Classify(typeof(Stream)).Should().Be(LinqTypeKind.Excluded);

        LinqDocumentGraph.Classify(typeof(string)).Should().Be(LinqTypeKind.Scalar);
        LinqDocumentGraph.Classify(typeof(int?)).Should().Be(LinqTypeKind.Scalar);
        LinqDocumentGraph.Classify(typeof(LinqOrderStatus)).Should().Be(LinqTypeKind.Scalar);
        LinqDocumentGraph.Classify(typeof(DateTimeOffset)).Should().Be(LinqTypeKind.Scalar);
        LinqDocumentGraph.Classify(typeof(string[])).Should().Be(LinqTypeKind.Collection);
        LinqDocumentGraph.Classify(typeof(List<LinqLine>)).Should().Be(LinqTypeKind.Collection);
        LinqDocumentGraph.Classify(typeof(IReadOnlyList<int>)).Should().Be(LinqTypeKind.Collection);
        LinqDocumentGraph.Classify(typeof(LinqCustomer)).Should().Be(LinqTypeKind.Object);
    }

    [Fact]
    public void The_converter_of_a_left_out_type_never_runs_while_the_graph_is_built()
    {
        int before = LinqConverterProbe.Conversions;

        LinqTestStore.Graph<LinqOrder>().MemberNames.Should().NotBeEmpty();

        LinqConverterProbe.Conversions.Should().Be(before, "the graph asks which converter a type has and never has it convert anything");
    }

    /// <summary>
    /// <see cref="LinqDocumentGraph.Bind" /> finds a name exactly as Dynamic LINQ's <c>FindPropertyOrField</c> does -
    /// receiver first, then its base types, instance and static, without case - and then requires the graph's own
    /// member. So a name allowed on one type is not allowed on another, and a name the parser would bind to something
    /// else is not allowed at all.
    /// </summary>
    [Fact]
    public void A_name_is_bound_as_the_parser_binds_it_and_only_to_the_graphs_own_member()
    {
        LinqDocumentGraph orders = LinqTestStore.Graph<LinqOrder>();

        orders.Bind(typeof(LinqOrder), "name")!.Member.Name.Should().Be("Name", "names bind without case");
        orders.Bind(typeof(LinqLine), "Quantity").Should().NotBeNull();
        orders.Bind(typeof(LinqOrder), "Quantity").Should().BeNull("Quantity is a line's, not the order's");
        orders.Bind(typeof(LinqCustomer), "Lines").Should().BeNull("Lines is the order's, not the customer's");
        orders.Bind(typeof(LinqOrder), "Converted").Should().BeNull("the graph leaves it out for its converter");
        orders.Bind(typeof(string), "Length").Should().BeNull("a framework member is the screen's to allow, on its own receiver");

        LinqDocumentGraph collide = LinqDocumentGraph.For(typeof(LinqCollide), new HashSet<Type>());

        collide.Bind(typeof(LinqHolder), "Code").Should().NotBeNull("the holder's Code is plain text");
        collide.Bind(typeof(LinqCollide), "Code").Should().BeNull("the document's own Code has a converter of the host's");

        LinqDocumentGraph derived = LinqDocumentGraph.For(typeof(LinqDerivedHidingCode), new HashSet<Type>());

        derived.Bind(typeof(LinqDerivedHidingCode), "Code").Should().BeNull(
            "the parser binds the derived Code, which hides the base's plain one");
        derived.Bind(typeof(LinqDerivedHidingCode), "Shared").Should().BeNull("a static member is bound by the parser and never the graph's");
        derived.Bind(typeof(LinqDerivedHidingCode), "Name").Should().NotBeNull();

        LinqDocumentGraph.FindAsDynamicLinq(typeof(LinqDerivedHidingCode), "code")!.DeclaringType.Should().Be<LinqDerivedHidingCode>();
        LinqDocumentGraph.FindAsDynamicLinq(typeof(LinqDerivedHidingCode), "shared").Should().NotBeNull("the parser finds static members too");
        LinqDocumentGraph.FindAsDynamicLinq(typeof(LinqAmbiguous), "name").Should().BeNull("two members match without case on one level");
    }

    /// <summary>
    /// A converter added at run time, with no attribute on the type, is the one Dynamic LINQ would be handed by
    /// <c>TypeDescriptor.GetConverter</c> - so the graph asks the same question rather than reading attributes alone.
    /// </summary>
    [Fact]
    public void A_type_given_a_converter_at_run_time_is_left_out_too()
    {
        LinqDocumentGraph.Classify(typeof(LinqRuntimeConverted)).Should().Be(LinqTypeKind.Object, "it has no converter yet");

        TypeDescriptor.AddAttributes(typeof(LinqRuntimeConverted), new TypeConverterAttribute(typeof(LinqConvertedConverter)));

        LinqDocumentGraph.Classify(typeof(LinqRuntimeConverted)).Should().Be(LinqTypeKind.Excluded);
    }

    [Fact]
    public void A_hidden_document_type_and_everything_only_reachable_through_it_are_left_out()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqOrder>(typeof(LinqSecret));

        graph.MemberNames.Should().NotContain("Secret");
        graph.MemberNames.Should().NotContain("Code", "it is reachable only through the hidden type");
        graph.IsHidden(typeof(LinqSecret)).Should().BeTrue();
        graph.IsHidden(typeof(List<LinqSecret>)).Should().BeTrue("a collection of a hidden type is the hidden type");
    }

    [Fact]
    public void The_hidden_set_is_asked_per_graph_and_never_cached_with_the_structure()
    {
        LinqTestStore.Graph<LinqOrder>(typeof(LinqSecret)).MemberNames.Should().NotContain("Secret");
        LinqTestStore.Graph<LinqOrder>().MemberNames.Should().Contain("Secret");
        LinqTestStore.Graph<LinqOrder>(typeof(LinqSecret)).MemberNames.Should().NotContain("Secret");
    }

    [Fact]
    public void The_graph_reaches_a_few_levels_down_and_no_further()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqDeep>();

        graph.MemberNames.Should().Contain(["One", "Level1", "Level2", "Level3", "Level4"]);
        graph.MemberNames.Should().NotContain("Level5", "LinqDeep5 is more than {0} levels below the document", LinqQueryLimits.GraphMaxDepth);
    }

    [Fact]
    public void Two_members_one_name_apart_in_case_are_neither_offered()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqAmbiguous>();

        graph.MemberNames.Should().NotContain("Name");
        graph.MemberNames.Should().Contain("Other");
    }

    [Fact]
    public void An_enum_is_data_only_when_a_member_of_the_graph_has_it()
    {
        LinqDocumentGraph graph = LinqTestStore.Graph<LinqOrder>();

        graph.IsGraphEnum(typeof(LinqOrderStatus)).Should().BeTrue();
        graph.IsGraphEnum(typeof(LinqOrderStatus?)).Should().BeTrue();
        graph.IsGraphEnum(typeof(DayOfWeek)).Should().BeFalse("no member of the document is a DayOfWeek");
    }

    [Fact]
    public void The_hint_lists_the_roots_own_scalars()
    {
        LinqTestStore.Graph<LinqOrder>().HintMembers.Should().Contain(["Name", "Total", "Status"]).And.NotContain("Customer");
    }
}
