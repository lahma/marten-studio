using JasperFx;

using Marten;
using Marten.Services;

using MartenStudio.Services.Query;

using Npgsql;

// StartsWith("A") is the overload Dynamic LINQ produces and Marten translates; the char overload is another method.
#pragma warning disable CA1866

namespace MartenStudio.Tests.Query;

/// <summary>
/// Marten's SQL for a LINQ query, built the way <c>ToCommand</c> builds it but without the table creation
/// <c>ToCommand</c> does - pinned at whatever Marten this run resolved, since the members are internal to Marten.
/// </summary>
/// <remarks>
/// No database anywhere: the store points at a host that does not resolve, so anything that tried to reach Postgres
/// - which is exactly what <c>ToCommand</c>'s <c>EnsureStorageExists</c> does - fails loudly instead of passing.
/// </remarks>
public class MartenLinqCommandTests
{
    [Fact]
    public void The_internal_builder_resolves_at_this_Marten()
    {
        MartenLinqCommand.IsAvailable.Should().BeTrue(
            "IMartenLinqQueryable.BuildLinqParser and LinqQueryParser.BuildDiagnosticCommand exist at 9.39.0 and 9.50.2 alike");
    }

    [Fact]
    public void Marten_builds_its_statement_with_no_database()
    {
        using DocumentStore store = LinqTestStore.Create();
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        MartenLinqStatement statement = MartenLinqCommand.Build(session.Query<LinqOrder>().Where(x => x.Name == "a"), session.TenantId);

        statement.Command.CommandText.Should().Contain(LinqTestStore.Schema + ".mt_doc_linqorder as d");
        statement.Command.CommandText.Should().Contain("d.id").And.Contain("d.data");
        statement.Command.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).Should().Contain("a",
            "a value the visitor typed is a parameter, never text in the statement");
        statement.DocumentTypes.Should().Equal(typeof(LinqOrder));
    }

    [Fact]
    public void Marten_binds_the_sessions_tenant_into_its_own_tenant_filter()
    {
        using DocumentStore store = LinqTestStore.Create(static options => options.Schema.For<LinqOrder>().MultiTenanted());
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("acme", store.Storage.Database));

        MartenLinqStatement statement = MartenLinqCommand.Build(session.Query<LinqOrder>(), session.TenantId);

        statement.Command.CommandText.Should().Contain("tenant_id");
        statement.Command.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).Should().Contain("acme");
    }

    [Fact]
    public void AnyTenant_takes_Marten_off_the_tenant_and_nothing_a_predicate_can_spell_does()
    {
        using DocumentStore store = LinqTestStore.Create(static options => options.Schema.For<LinqOrder>().MultiTenanted());
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase("acme", store.Storage.Database));

        MartenLinqStatement statement = MartenLinqCommand.Build(session.Query<LinqOrder>().Where(static x => x.AnyTenant()), session.TenantId);

        statement.Command.Parameters.Cast<NpgsqlParameter>().Select(static x => x.Value).Should().NotContain("acme",
            "the studio's own AnyTenant() - applied only when no tenant is in scope - is what lifts the filter");
    }

    /// <summary>
    /// Why the studio does not call <c>ToCommand</c>: on a store that may create its schema, <c>ToCommand</c> reaches for
    /// the database to make sure the table exists - here, a host that does not resolve - where the studio's builder
    /// does not.
    /// </summary>
    [Fact]
    public void ToCommand_reaches_for_the_database_to_create_the_table_and_the_studios_builder_does_not()
    {
        using DocumentStore store = LinqTestStore.Create(static options => options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate);
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        IQueryable<LinqOrder> query = session.Query<LinqOrder>().Where(x => x.Total > 1m);

        Action toCommand = () => query.ToCommand();
        toCommand.Should().Throw<Exception>("EnsureStorageExists opens a connection to run the migration");

        MartenLinqCommand.Build(query, session.TenantId).Command.CommandText.Should().Contain("mt_doc_linqorder");
    }

    [Fact]
    public void Where_ToCommand_creates_nothing_it_builds_the_very_same_statement()
    {
        using DocumentStore store = LinqTestStore.Create(static options => options.AutoCreateSchemaObjects = AutoCreate.None);
        using IDocumentSession session = store.LightweightSession(SessionOptions.ForDatabase(store.Storage.Database));

        IQueryable<LinqOrder> query = session.Query<LinqOrder>()
            .Where(x => x.Name.StartsWith("A") && x.Lines.Any(l => l.Quantity > 5))
            .OrderBy(x => x.Total)
            .Skip(50)
            .Take(51);

        MartenLinqCommand.Build(query, session.TenantId).Command.CommandText.Should().Be(query.ToCommand().CommandText);
    }

    [Fact]
    public void A_query_another_session_type_made_is_refused()
    {
        Action build = () => MartenLinqCommand.Build(new[] { new LinqOrder() }.AsQueryable(), "*DEFAULT*");

        build.Should().Throw<InvalidOperationException>().WithMessage("*Marten's own session*");
    }
}
