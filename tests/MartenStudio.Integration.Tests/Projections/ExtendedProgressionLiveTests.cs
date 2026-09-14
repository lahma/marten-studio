using System.Security.Claims;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain.Events;
using MartenStudio.Services;
using MartenStudio.Services.Projections;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Projections;

/// <summary>
/// One progression table with every extended column filled in, read by two studios that differ only in
/// <c>Events.EnableExtendedProgressionTracking</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this shape exists at all.</b> Marten 9.35's <c>EventProgressionTable</c> creates
/// <c>heartbeat</c>, <c>agent_status</c>, <c>pause_reason</c>, <c>running_on_node</c> and the four
/// <c>failure_*</c> columns for every store, deliberately ungated on the flag - #5309, because gating the
/// DDL made two <c>DocumentStore</c>s over one <c>DatabaseSchemaName</c> resolve each other's columns as
/// extras and <c>drop column</c> them in turn. Only the <em>use</em> of the columns is conditional:
/// <c>ProjectionProgressStatement</c> selects them and <c>ShardStateSelector</c> hydrates them only when
/// the flag is on, and the flag defaults to <see langword="false" />. So "this table has
/// <c>pause_reason</c>" and "something is writing <c>pause_reason</c>" are different facts, and a store
/// that once ran with the flag on - or that shares a schema with a store that has it on, which is #5309's
/// own scenario - has the first without the second.
/// </para>
/// <para>
/// <b>What it is pinning.</b> The studio's read used to be driven by <c>information_schema</c> alone, so
/// on exactly that store it reconstructed an <c>AgentStatus</c>, a <c>PauseReason</c> and a
/// <c>ShardFailure</c> that <c>IMartenDatabase.AllProjectionProgress</c> does not report, and drew a
/// running shard as paused with a failure out of telemetry nothing maintains. Both readings are compared
/// against Marten's own here, in both flag positions, because "the studio's row and Marten's row are the
/// same <c>ShardState</c>" is the claim the code makes and this is what makes it checkable.
/// </para>
/// <para>
/// The row is written by hand: a real one needs a running daemon that has actually paused, and what this
/// class needs is a row with known values in every optional column. Nothing here runs a daemon.
/// </para>
/// </remarks>
public class ExtendedProgressionLiveTests(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The shard the hand-written row belongs to - a real one, so it is not an orphan.</summary>
    private const string Shard = "DailySales:All";

    private const long Sequence = 12;

    private const string AgentStatus = "Paused";

    private const string PauseReason = "the node this shard ran on was drained";

    /// <summary>A real <c>ShardFailureCategory</c> name: the column stores the name, never the ordinal.</summary>
    private const string FailureCategory = "ApplyEvent";

    private const string FailureEventType = "OrderPlaced";

    private const long FailureEventSequence = 41;

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private string schema = string.Empty;
    private string eventSchema = string.Empty;

    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        schema = GetType().Name.ToLowerInvariant();
        eventSchema = schema + "_events";

        await postgres.CreateSchemaAsync(schema, Token);
        await postgres.CreateSchemaAsync(eventSchema, Token);

        // One store creates the event store. Which flag it was built with does not matter to the shape of
        // mt_event_progression - that is the whole of #5309 - and this test proves it below rather than
        // assuming it.
        await using ServiceProvider migrating = BuildStudio(extendedProgressionTracking: false);

        var store = migrating.GetRequiredService<IDocumentStore>();
        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await WriteProgressionRowAsync();
    }

    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The premise: the columns are on the table even though the store that migrated it had the flag off.
    /// </summary>
    /// <remarks>
    /// Without this the two tests below would pass on a table that simply has no extended columns, which
    /// is a different store and not the one the bug was about.
    /// </remarks>
    [PostgresFact]
    public async Task The_extended_columns_exist_whatever_the_flag_said_when_the_table_was_made()
    {
        IReadOnlyList<string> columns = await ProgressionColumnsAsync();

        columns.Should().Contain("heartbeat")
            .And.Contain("agent_status")
            .And.Contain("pause_reason")
            .And.Contain("running_on_node")
            .And.Contain("failure_category")
            .And.Contain("failure_event_sequence")
            .And.Contain("failure_event_type")
            .And.Contain("failure_event_tenant_id");
    }

    /// <summary>
    /// With the flag off, the studio reports the position and nothing else - exactly as Marten does.
    /// </summary>
    [PostgresFact]
    public async Task A_store_with_the_flag_off_reports_none_of_the_telemetry_the_row_carries()
    {
        await using ServiceProvider provider = BuildStudio(extendedProgressionTracking: false);

        ShardProgress shard = await ReadShardAsync(provider);

        shard.HasProgressRow.Should().BeTrue("the row is there and its position is real");
        shard.Sequence.Should().Be(Sequence);

        shard.AgentStatus.Should().BeNull("nothing on this store writes agent_status");
        shard.PauseReason.Should().BeNull("nothing on this store writes pause_reason");
        shard.Failure.Should().BeNull("nothing on this store writes the failure_* columns");
        shard.IsPaused.Should().BeFalse("a running shard drawn as paused was the bug");
        shard.HasFailed.Should().BeFalse();

        // And that is Marten's answer too, which is the whole claim.
        ShardState martens = await MartensStateAsync(provider);

        martens.AgentStatus.Should().BeNull();
        martens.PauseReason.Should().BeNull();
        martens.Failure.Should().BeNull();
        martens.Sequence.Should().Be(Sequence);
    }

    /// <summary>
    /// And with the flag on, the same row in the same table answers all of it.
    /// </summary>
    /// <remarks>
    /// The other half, and what stops the fix from being "never read the extended columns": a store that
    /// really does publish this telemetry must still see it, and see it reconstructed the way
    /// <c>ShardStateSelector</c> reconstructs it - <c>failure_category</c> as the presence flag,
    /// <c>pause_reason</c> as both message and detail.
    /// </remarks>
    [PostgresFact]
    public async Task The_same_row_read_with_the_flag_on_carries_the_status_the_pause_and_the_failure()
    {
        await using ServiceProvider provider = BuildStudio(extendedProgressionTracking: true);

        ShardProgress shard = await ReadShardAsync(provider);

        shard.Sequence.Should().Be(Sequence);
        shard.AgentStatus.Should().Be(AgentStatus);
        shard.PauseReason.Should().Be(PauseReason);
        shard.IsPaused.Should().BeTrue();
        shard.HasFailed.Should().BeTrue();
        shard.Failure.Should().Contain(PauseReason, "pause_reason is the only failure text that is persisted");

        // Marten's own reading of the same row, member by member.
        ShardState martens = await MartensStateAsync(provider);

        martens.AgentStatus.Should().Be(shard.AgentStatus);
        martens.PauseReason.Should().Be(shard.PauseReason);
        martens.Failure.Should().NotBeNull();
        martens.Failure!.Category.Should().Be(Enum.Parse<ShardFailureCategory>(FailureCategory));
        martens.Failure.Detail.Should().Be(PauseReason);
        martens.Failure.Event.Should().NotBeNull();
        martens.Failure.Event!.Sequence.Should().Be(FailureEventSequence);
        martens.Failure.Event.EventTypeName.Should().Be(FailureEventType);
    }

    /// <summary>One shard row, read the way a page reads it.</summary>
    private static async Task<ShardProgress> ReadShardAsync(ServiceProvider provider)
    {
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        var service = scope.ServiceProvider.GetRequiredService<IProjectionDataService>();

        ProjectionsView view = await service.GetProjectionsAsync(ScopeOf, Token);

        view.HasEventStore.Should().BeTrue();

        return view.Progress.Should().ContainSingle(x => x.ShardName == Shard).Subject;
    }

    /// <summary>
    /// Marten's own reading of the same row.
    /// </summary>
    /// <remarks>
    /// <c>AllProjectionProgress</c> migrates before it reads, which is why no page calls it (hard rule 14)
    /// and why calling it from a test is fine: the schema is already there, so the migration is a no-op,
    /// and this is the only way to compare the studio's answer with the one it claims to reproduce.
    /// </remarks>
    private static async Task<ShardState> MartensStateAsync(ServiceProvider provider)
    {
        var store = provider.GetRequiredService<IDocumentStore>();
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases();

        IReadOnlyList<ShardState> progress = await databases[0].AllProjectionProgress(Token);

        return progress.Should().ContainSingle(x => x.ShardName == Shard).Subject;
    }

    /// <summary>The scope every call is made in: this store's one database, no tenant.</summary>
    private static StudioScope ScopeOf { get; } =
        new(MartenStoreRegistry.DefaultStoreKey, string.Empty, null);

    /// <summary>A studio over this class's schema pair, differing only in the flag.</summary>
    private ServiceProvider BuildStudio(bool extendedProgressionTracking)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, AnonymousAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = eventSchema;
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            options.Events.EnableExtendedProgressionTracking = extendedProgressionTracking;

            // One registered async projection, so DailySales:All is a shard the view has a row for rather
            // than an orphan.
            options.Projections.Add(new DailySalesProjection(), ProjectionLifecycle.Async);
        });

        services.AddMartenStudio(options =>
        {
            options.AuthorizationPolicy = null;
            options.DiscoverTenantIds = false;
        });

        return services.BuildServiceProvider();
    }

    /// <summary>What <c>information_schema</c> says <c>mt_event_progression</c> has.</summary>
    private async Task<IReadOnlyList<string>> ProgressionColumnsAsync()
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select column_name from information_schema.columns "
            + "where table_schema = @schema and table_name = 'mt_event_progression'",
            connection);

        command.Parameters.AddWithValue("schema", eventSchema);

        List<string> names = [];

        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    /// <summary>
    /// Writes one progression row with every optional column populated, as a daemon with extended
    /// tracking on would have left it.
    /// </summary>
    private async Task WriteProgressionRowAsync()
    {
        await using NpgsqlConnection connection = await postgres.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            $"""
             insert into "{eventSchema}"."mt_event_progression"
                 (name, last_seq_id, last_updated, heartbeat, agent_status, pause_reason, running_on_node,
                  failure_category, failure_event_sequence, failure_event_type, failure_event_tenant_id)
             values (@name, @sequence, now(), now(), @status, @reason, 3,
                  @category, @failureSequence, @failureType, '*DEFAULT*')
             on conflict (name) do update set last_seq_id = excluded.last_seq_id
             """,
            connection);

        command.Parameters.AddWithValue("name", Shard);
        command.Parameters.AddWithValue("sequence", Sequence);
        command.Parameters.AddWithValue("status", AgentStatus);
        command.Parameters.AddWithValue("reason", PauseReason);
        command.Parameters.AddWithValue("category", FailureCategory);
        command.Parameters.AddWithValue("failureSequence", FailureEventSequence);
        command.Parameters.AddWithValue("failureType", FailureEventType);

        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>Who a container-only studio's circuit belongs to: nobody.</summary>
    private sealed class AnonymousAuthenticationStateProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity())));
    }
}
