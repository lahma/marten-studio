using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Find;
using PostgresStudio.Services.Postgres;
using PostgresStudio.Services.Query;
using PostgresStudio.Services.Statements;

namespace PostgresStudio.Tests.Statements;

/// <summary>
/// 1.2-SCOPE-3: Find's statements. The contributor reads the Statements page's own list - the activity gate and the last sweep, never
/// a new one - so a refusal is the page's sentence as a note, a hit exists only for a <c>queryid</c> that list holds, and nothing is
/// audited here.
/// </summary>
public class StatementFindContributorTests
{
    private static readonly StudioScope Scope = new("default", "default", null);

    private sealed class FakeStatements(StatementsView view) : IStatementsService
    {
        public List<StudioScope> Latest { get; } = [];

        public int Runs { get; private set; }

        public Task<StatementsView> GetAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Runs++;
            return Task.FromResult(view);
        }

        public Task<StatementsView> GetLatestAsync(StudioScope scope, CancellationToken cancellationToken = default)
        {
            Latest.Add(scope);
            return Task.FromResult(view);
        }

        public Task<StatementPlanView> PlanAsync(StudioScope scope, long queryId, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<WhatIfResult> WhatIfAsync(StudioScope scope, long queryId, HypotheticalIndex index, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexAdviceView> AdviseIndexesAsync(StudioScope scope, IndexAdviceRequest request, CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IndexAdviceView> AdviseIndexesAsync(StudioScope scope, IndexAdviceRequest request, bool recordAudit, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static StatementCounts Counts() => new(5, 120, 20, 450, 15, 2, 0, null);

    private static StatementRowView Row(long queryId, string? text) =>
        new(queryId, text, null, "select", "select", Counts(), Counts(), 0.5, null, null);

    private static StatementFindContributor Contributor(FakeStatements statements) => new(
        statements,
        new PostgresStudioSettings(Microsoft.Extensions.Options.Options.Create(new PostgresStudioOptions())),
        PostgresStudioDescriptor.Instance);

    private static StatementsView Read(params StatementRowView[] rows) => new() { State = StatementsState.Read, Rows = rows };

    [Fact]
    public async Task A_queryid_the_last_sweep_read_is_a_hit_with_the_start_of_its_text()
    {
        FakeStatements statements = new(Read(Row(-4_821_000_111_222_333, "select *\n  from   orders\nwhere id = $1"), Row(7_000_000_000_000, "select 1")));

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse("-4821000111222333"), CancellationToken.None);

        FindHit hit = found.Hits.Should().ContainSingle().Subject;
        hit.Kind.Should().Be("Statement");
        hit.Title.Should().Be("Statement -4821000111222333");
        hit.Detail.Should().Be("select * from orders where id = $1", "whitespace is flattened");
        hit.Url.Should().Contain("queryid=-4821000111222333");
        hit.Scope.Should().Be(Scope);
        found.Searched.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task A_queryid_the_list_does_not_hold_is_no_hit()
    {
        FakeStatements statements = new(Read(Row(7_000_000_000_000, "select 1")));

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse("8000000000000"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().BeEmpty();
    }

    [Fact]
    public async Task A_text_the_visitor_may_not_read_is_not_invented()
    {
        FakeStatements statements = new(Read(Row(7_000_000_000_000, null)));

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse("7000000000000"), CancellationToken.None);

        found.Hits.Should().ContainSingle().Which.Detail.Should().BeNull();
    }

    [Fact]
    public async Task A_refusal_is_a_note_that_says_what_the_page_says_and_nothing_else()
    {
        FakeStatements statements = new(StatementsView.Refused(DatabaseRefusal.CapabilityOff, "Statements need Capabilities.ViewDatabaseActivity."));

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse("-5"), CancellationToken.None);

        found.Hits.Should().BeEmpty();
        found.Notes.Should().Equal("Statements: not searched. Statements need Capabilities.ViewDatabaseActivity.");
    }

    [Fact]
    public async Task No_sweep_yet_is_a_note_and_never_starts_one()
    {
        FakeStatements statements = new(new StatementsView { State = StatementsState.NotRun, Status = "No statement check has run yet." });

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse("-5"), CancellationToken.None);

        found.Notes.Should().Equal("Statements: not searched. No statement check has run yet.");
        statements.Runs.Should().Be(0, "Find reads the last sweep, as a poll does, and starts none");
        statements.Latest.Should().ContainSingle();
    }

    [Theory]
    [InlineData("#-5")]
    [InlineData("12")]
    [InlineData("99999999999")]
    [InlineData("orders")]
    [InlineData("")]
    public async Task A_value_that_is_not_shaped_like_a_queryid_is_not_this_kinds_and_asks_nothing_and_says_nothing(string typed)
    {
        FakeStatements statements = new(StatementsView.Refused(DatabaseRefusal.CapabilityOff, "refused"));

        FindContribution found = await Contributor(statements).FindAsync(Scope, FindQuery.Parse(typed), CancellationToken.None);

        found.Should().Be(FindContribution.NotApplicable);
        statements.Latest.Should().BeEmpty("the note appears only when the value had a queryid's shape");
    }
}
