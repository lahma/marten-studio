using JasperFx.Events;

using MartenStudio.Internal.Sql;
using MartenStudio.Services.Events;
using MartenStudio.Services.Projections;

using PostgresStudio.Services;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// H1, item 1: a refusal thrown at a page is rendered, audited and - whenever an exception is logged whole - written to the
/// application's log, so a value the visitor typed into the address or a circuit goes into its message only escaped and
/// bounded (<see cref="StudioLogValue" />, AGENTS.md hard rule 5). Each refusal that names such a value, by its own factory.
/// </summary>
public class ExceptionValuesTests
{
    /// <summary>A name a visitor could type: a forged second line, a terminal escape, and past every bound.</summary>
    private static readonly string Hostile = "acme\r\n2026-10-27 12:00:00 warn: forged\u001b[2J‮" + new string('x', 1_000);

    public static TheoryData<string, string> Refusals() => new()
    {
        { "UnknownProjection", ProjectionDataService.UnknownProjection(Hostile).Message },
        { "UnknownAsyncProjection", EventDataService.UnknownAsyncProjection(Hostile).Message },
        { "UnprovisionedTenant", ProjectionDataService.UnprovisionedTenant("default", Hostile).Message },
        { "ParseId uuid", DocumentQueryBuilder.ParseId(DocumentIdColumnType.Uuid, Hostile).Error! },
        { "ParseId int4", DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int4, Hostile).Error! },
        { "ParseId int8", DocumentQueryBuilder.ParseId(DocumentIdColumnType.Int8, Hostile).Error! },
        { "ParseStreamId", new EventTableInfo { Schema = "events", StreamIdentity = StreamIdentity.AsGuid }.ParseStreamId(Hostile).Error! },
    };

    [Theory]
    [MemberData(nameof(Refusals))]
    public void A_refusal_names_what_the_visitor_typed_escaped_and_bounded(string refusal, string message)
    {
        message.Should().NotContain(Hostile.Trim(), refusal + " names the value only escaped and bounded");
        message.Any(StudioLogValue.IsEscaped).Should().BeFalse("{0} carries no raw control, format or separator character", refusal);
        message.Should().Contain(StudioLogValue.Cap(Hostile.Trim()), "{0} still names it, bounded", refusal);
        message.Length.Should().BeLessThan(StudioLogValue.MaxLength + 300, "{0} is bounded whatever was typed", refusal);
    }

    /// <summary>The anti-vacuity half: an ordinary name goes into each sentence exactly as it was typed.</summary>
    [Fact]
    public void An_ordinary_name_is_named_as_it_was_typed()
    {
        ProjectionDataService.UnknownProjection("DailySales").Message.Should().Be("This store has no projection named 'DailySales'.");
        EventDataService.UnknownAsyncProjection("DailySales").Message.Should().Be("This store has no async projection named 'DailySales'.");
        DocumentQueryBuilder.ParseId(DocumentIdColumnType.Uuid, "not-a-guid").Error.Should()
            .Be("'not-a-guid' is not a GUID, and this collection's id column is uuid.");
    }
}
