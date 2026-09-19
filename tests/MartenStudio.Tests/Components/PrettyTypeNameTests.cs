using MartenStudio.Components.Shared;

namespace MartenStudio.Tests.Components;

/// <summary>
/// <see cref="DisplayValueHelper.PrettyTypeName" />: the assembly-qualified name Marten records for an
/// event type, as the name a person reads.
/// </summary>
/// <remarks>
/// The screens that show a .NET type name keep the raw value in the cell's <c>title</c>, so this is a
/// summary and never the only copy. That is what lets it be strict about giving up: anything that does
/// not parse comes back exactly as it went in rather than half-rendered.
/// </remarks>
public class PrettyTypeNameTests
{
    /// <summary>
    /// The one that started this. A closed generic's assembly-qualified name nests a whole qualified
    /// name per argument, which made the event-types table 1691px wide inside a 1200px column and put
    /// four of its six columns beyond the right-hand edge with no way to scroll to them.
    /// </summary>
    [Fact]
    public void A_closed_generic_reads_as_the_C_sharp_name()
    {
        const string Recorded =
            "JasperFx.Events.Compacted`1[[MartenStudio.SampleDomain.Events.DailySales, "
            + "MartenStudio.SampleDomain, Version=0.1.0.0, Culture=neutral, PublicKeyToken=null]], "
            + "JasperFx.Events";

        DisplayValueHelper.PrettyTypeName(Recorded).Should().Be(
            "JasperFx.Events.Compacted<MartenStudio.SampleDomain.Events.DailySales>");
    }

    /// <summary>
    /// The namespace stays. Two event types called <c>OrderPlaced</c> in two namespaces is exactly the
    /// question this column is read to settle, so the simple name alone would answer the wrong one.
    /// </summary>
    [Fact]
    public void A_plain_type_keeps_its_namespace_and_loses_its_assembly()
    {
        DisplayValueHelper.PrettyTypeName("MartenStudio.SampleDomain.Events.ItemAdded, MartenStudio.SampleDomain")
            .Should().Be("MartenStudio.SampleDomain.Events.ItemAdded");
    }

    [Fact]
    public void An_argument_that_is_itself_generic_is_unwrapped_too()
    {
        DisplayValueHelper.PrettyTypeName("Outer`1[[Inner`1[[X, A]], A]], A").Should().Be("Outer<Inner<X>>");
    }

    [Fact]
    public void Two_arguments_are_separated_by_a_comma()
    {
        DisplayValueHelper.PrettyTypeName("System.Collections.Generic.Dictionary`2[[System.String, mscorlib],[System.Int32, mscorlib]], mscorlib")
            .Should().Be("System.Collections.Generic.Dictionary<System.String, System.Int32>");
    }

    /// <summary>An array rank is part of the type's name and is kept exactly as it was written.</summary>
    [Fact]
    public void An_array_keeps_its_rank()
    {
        DisplayValueHelper.PrettyTypeName("System.Int32[], mscorlib").Should().Be("System.Int32[]");
        DisplayValueHelper.PrettyTypeName("System.Int32[,], mscorlib").Should().Be("System.Int32[,]");
    }

    [Fact]
    public void Null_is_null_and_empty_is_empty()
    {
        DisplayValueHelper.PrettyTypeName(null).Should().BeNull();
        DisplayValueHelper.PrettyTypeName(string.Empty).Should().BeEmpty();
    }

    /// <summary>
    /// Anything that does not parse comes back byte for byte. A name that is only half understood would
    /// be a different fact from the one the store recorded, and this column is read precisely when
    /// somebody is trying to work out which type a row really is.
    /// </summary>
    [Theory]
    [InlineData("not a type name [[[")]
    [InlineData("Foo`1[[Bar")]
    [InlineData("Foo`1[[Bar, A]")]
    [InlineData("[[]]")]
    [InlineData("`1")]
    public void Anything_that_does_not_parse_comes_back_untouched(string garbage)
    {
        DisplayValueHelper.PrettyTypeName(garbage).Should().BeSameAs(garbage);
    }
}
