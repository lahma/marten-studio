using MartenStudio.Services;
using MartenStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Query;

/// <summary>
/// DB-0-fix-3, F-a (security): <c>RunSql</c> is authorized for the database as a whole, because the
/// console reads every tenant's rows - against a real database, with a real ASP.NET Core policy that
/// allows <c>acme</c> and nothing wider.
/// </summary>
/// <remarks>
/// <para>
/// The console used to resolve <c>(db, acme, RunSql)</c>, so this visitor could type
/// <c>select * from … mt_doc_queryticket</c> and read <c>globex</c>'s tickets. Now the console is refused,
/// the plan a Mode A clause would fetch through it is refused as a value, and Mode A's nested-read rules
/// stay on - while Mode A itself, which composes its own tenant predicate, still reads <c>acme</c>'s rows.
/// </para>
/// <para>
/// Each refusal is checked against the database too: what matters is that <c>globex</c>'s rows never
/// reached this visitor, not only that an exception was thrown.
/// </para>
/// </remarks>
public class SqlConsoleReachLiveTests(PostgresFixture fixture) : IAsyncLifetime
{
    private QueryHarness harness = null!;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        harness = await QueryHarness.CreateAsync(fixture, "sqlconsolereachlive", static options =>
        {
            options.StoreAuthorizationPolicy = QueryHarness.AcmeOnlyPolicy;
            options.WriteAuthorizationPolicy = QueryHarness.AcmeOnlyPolicy;
        });
    }

    public async ValueTask DisposeAsync()
    {
        if (harness is not null)
        {
            await harness.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static StudioScope AcmeScope => QueryHarness.ScopeFor(QueryHarness.Acme);

    private async Task<QueryDocumentTypeInfo> TicketAsync() =>
        (await harness.Queries.ListDocumentTypesAsync(AcmeScope, Token)).Single(static x => x.TypeName == nameof(QueryTicket));

    /// <summary>
    /// The statement that would have read <c>globex</c>'s tickets is never sent, and the refusal is
    /// audited against the database as a whole.
    /// </summary>
    [PostgresFact]
    public async Task A_visitor_allowed_one_tenant_is_refused_the_console_and_reads_nothing()
    {
        QueryDocumentTypeInfo ticket = await TicketAsync();

        Func<Task> running = () => harness.Queries.RunSqlAsync(
            AcmeScope, new SqlConsoleRequest($"select tenant_id from {ticket.QualifiedTableName}"), Token);

        StudioNotAuthorizedException refused = (await running.Should().ThrowAsync<StudioNotAuthorizedException>(
            "the statement would read every tenant's tickets")).Which;

        refused.Scope.TenantId.Should().BeNull();

        StudioActionLogEntry entry = harness.Audit.GetLatest()
            .First(static x => x.Action == QueryService.RunSqlAction);

        entry.Succeeded.Should().BeFalse();
        entry.TenantId.Should().BeNull("the audit names the question that was refused");

        harness.Logs.Should().Contain(
            static x => x.EventId == 9203 && x.Message.Contains(QueryHarness.AcmeOnlyPolicy, StringComparison.Ordinal),
            "9203 names the policy that refused");
    }

    /// <summary>
    /// Mode A needs no capability and composes its own tenant predicate, so it still reads the visitor's
    /// own tenant - and only it. The plan it would fetch through the console's gate is refused as a value,
    /// with the reason, rather than failing the read that succeeded.
    /// </summary>
    [PostgresFact]
    public async Task Mode_A_still_reads_the_visitors_own_tenant_and_no_plan_is_fetched()
    {
        QueryDocumentTypeInfo ticket = await TicketAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            AcmeScope, new MartenQueryRequest(ticket.Alias, "where 1 = 1"), Token);

        result.Rejection.Should().BeNull();
        result.Error.Should().BeNull();
        result.Rows.Select(static x => x.Id).Should().Equal(QueryHarness.AcmeLive.ToString());
        result.Rows.Should().OnlyContain(static x => x.TenantId == QueryHarness.Acme);

        result.Plan!.UnavailableReason.Should().Contain("this database as a whole",
            "the EXPLAIN goes through the console's gate, which asks about the database");
    }

    /// <summary>
    /// A subquery reads beyond the tenant as surely as the console does, so the nested-read rules stay on
    /// for a visitor the console would refuse - and the clause that would have read <c>globex</c>'s ticket
    /// through one is refused before it is sent.
    /// </summary>
    [PostgresFact]
    public async Task A_subquery_that_would_reach_another_tenant_is_refused_before_it_is_sent()
    {
        QueryDocumentTypeInfo ticket = await TicketAsync();

        MartenQueryResult result = await harness.Queries.RunMartenQueryAsync(
            AcmeScope,
            new MartenQueryRequest(
                ticket.Alias,
                $"where exists (select 1 from {ticket.QualifiedTableName} as other where other.tenant_id = 'globex')"),
            Token);

        result.Rejection.Should().NotBeNull("without the console's authorization the clause may read only the table it filters");
        result.Rows.Should().BeEmpty();
    }
}
