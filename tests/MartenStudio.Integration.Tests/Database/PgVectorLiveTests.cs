using System.Security.Claims;

using Marten;
using Marten.Schema;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

using Testcontainers.PostgreSql;

namespace MartenStudio.Integration.Tests.Database;

/// <summary>
/// PGV against a real pgvector: what Schema › Tables, Schema › Indexes, the database browser's object detail and its
/// rows show of <c>vector</c>, <c>halfvec</c> and <c>sparsevec</c> columns and of HNSW and IVFFlat indexes - with
/// explicit and default parameters - and that reading all of it creates nothing, holds no lock on what it lists, and
/// gives up at the session's <c>lock_timeout</c> behind somebody's <c>ACCESS EXCLUSIVE</c> lock rather than queue.
/// </summary>
/// <remarks>
/// <c>postgres:17-alpine</c> has no pgvector, so the class runs on a server of its own,
/// <see cref="PgVectorServerFixture.Image" />, never reused. The fixture seeds what a host meets in practice: a table
/// beside the store in its document schema, the shape of Marten.PgVector's <c>VectorIndex&lt;T&gt;()</c> (an HNSW index
/// over a cast expression), and the shapes only a host's own DDL makes - <c>halfvec</c>, <c>sparsevec</c>, IVFFlat, a
/// domain over <c>vector(3)</c> and a <c>vector</c> declared without dimensions.
/// </remarks>
public class PgVectorLiveTests(PgVectorServerFixture server) : IClassFixture<PgVectorServerFixture>
{
    private const string DocumentSchema = PgVectorServerFixture.DocumentSchema;
    private const string EventSchema = PgVectorServerFixture.EventSchema;
    private const string Table = PgVectorServerFixture.Table;

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task The_image_is_the_pinned_pgvector_and_owns_both_access_methods()
    {
        SkipIfUnavailable();

        await using NpgsqlConnection connection = await server.OpenAsync();

        (await ScalarAsync<string>(connection, "select extversion from pg_catalog.pg_extension where extname = 'vector'"))
            .Should().Be(PgVectorServerFixture.ExtensionVersion, "the defaults the studio cites are this version's");
    }

    [PostgresFact]
    public async Task Schema_Tables_names_every_vector_column_with_its_declared_dimensions()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build(["*"]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        SchemaTables tables = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().TablesAsync(Scope, Token);

        tables.Reason.Should().BeNull();
        TableStats table = tables.Tables.Single(static x => x.Schema == DocumentSchema && x.Table == Table);

        table.VectorColumns.Select(static x => x.Text).Should().Equal(
            "embedding vector(3)",
            "summary halfvec(3)",
            "sparse sparsevec(5)",
            "unsized vector",
            "wide vector(400)",
            "typed vector(3)");

        tables.Tables.Where(static x => x.Table != Table).SelectMany(static x => x.VectorColumns).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Schema_Indexes_shows_each_HNSW_and_IVFFlat_index_with_its_operator_class_and_parameters()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build(["*"]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        SchemaIndexes indexes = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().IndexesAsync(Scope, Token);

        indexes.Reason.Should().BeNull();
        Dictionary<string, IndexInfo> byName = indexes.Indexes.Where(static x => x.Table == Table).ToDictionary(static x => x.Name);

        byName["embeddings_pkey"].Vector.Should().BeNull();
        byName["embeddings_pkey"].AccessMethod.Should().Be("btree");

        PgVectorIndex hnsw = byName["embeddings_hnsw"].Vector!;
        hnsw.AccessMethod.Should().Be("hnsw");
        hnsw.Columns.Should().ContainSingle().Which.Should().Be(
            new PgVectorIndexColumn("embedding", "vector_cosine_ops", new PgVectorDistance("cosine distance", "<=>")));
        Parameters(hnsw).Should().Equal(("m", "8", false), ("ef_construction", "32", false));

        PgVectorIndex hnswDefault = byName["embeddings_hnsw_default"].Vector!;
        hnswDefault.Columns.Single().OperatorClass.Should().Be("halfvec_l2_ops");
        Parameters(hnswDefault).Should().Equal(("m", "16", true), ("ef_construction", "64", true));

        PgVectorIndex ivfflat = byName["embeddings_ivfflat"].Vector!;
        ivfflat.AccessMethod.Should().Be("ivfflat");
        Parameters(ivfflat).Should().Equal(("lists", "2", false));
        ivfflat.Columns.Single().OperatorClass.Should().Be("vector_l2_ops");
        byName["embeddings_ivfflat"].Definition.Should().NotContain("vector_l2_ops",
            "Postgres prints no operator class that is the default for its type - which is why the studio reads pg_opclass");

        PgVectorIndex ivfflatDefault = byName["embeddings_ivfflat_default"].Vector!;
        ivfflatDefault.Columns.Single().Distance.Should().Be(new PgVectorDistance("negative inner product", "<#>"));
        Parameters(ivfflatDefault).Should().Equal(("lists", "100", true));

        PgVectorIndex expression = byName["embeddings_expression"].Vector!;
        expression.Columns.Single().Definition.Should().Be("(embedding::text::public.vector(3))",
            "Marten.PgVector's shape: an HNSW index over a cast, printed with its schema while the search path is pinned");
        expression.Columns.Single().OperatorClass.Should().Be("vector_l1_ops");
    }

    [PostgresFact]
    public async Task Object_detail_badges_vector_columns_and_describes_the_vector_indexes()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build(["*"]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DatabaseObjectDetail detail = await scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
            .GetObjectAsync(Scope, DocumentSchema, Table, Token);

        detail.Refusal.Should().Be(DatabaseRefusal.None, detail.Reason);

        Dictionary<string, DatabaseColumnInfo> columns = detail.Columns.ToDictionary(static x => x.Name);
        columns["id"].Vector.Should().BeNull();
        columns["embedding"].Type.Should().Be("public.vector(3)", "format_type prints the dimension, and the schema while the path is pinned");
        columns["embedding"].Vector.Should().Be(new PgVectorColumn(PgVectorKind.Vector, 3));
        columns["summary"].Vector.Should().Be(new PgVectorColumn(PgVectorKind.HalfVector, 3));
        columns["sparse"].Vector.Should().Be(new PgVectorColumn(PgVectorKind.SparseVector, 5));
        columns["unsized"].Vector.Should().Be(new PgVectorColumn(PgVectorKind.Vector, null));
        columns["typed"].Type.Should().Be(DocumentSchema + ".embedding3");
        columns["typed"].Vector.Should().Be(new PgVectorColumn(PgVectorKind.Vector, 3), "a domain's own type modifier is its dimensions");

        Dictionary<string, DatabaseIndexInfo> indexes = detail.Indexes.ToDictionary(static x => x.Name);
        indexes["embeddings_pkey"].Vector.Should().BeNull();
        indexes["embeddings_hnsw"].AccessMethod.Should().Be("hnsw");
        Parameters(indexes["embeddings_hnsw"].Vector!).Should().Equal(("m", "8", false), ("ef_construction", "32", false));
        Parameters(indexes["embeddings_ivfflat_default"].Vector!).Should().Equal(("lists", "100", true));
    }

    [PostgresFact]
    public async Task A_withheld_extension_schema_is_masked_and_the_vector_is_still_known_from_the_catalog()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build([DocumentSchema]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        DatabaseObjectDetail detail = await scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
            .GetObjectAsync(Scope, DocumentSchema, Table, Token);

        detail.Refusal.Should().Be(DatabaseRefusal.None, detail.Reason);

        DatabaseColumnInfo embedding = detail.Columns.Single(static x => x.Name == "embedding");
        embedding.Type.Should().Be(WithheldNames.Token + ".vector(3)", "public is not browsable here, so its name is masked");
        embedding.Vector.Should().Be(new PgVectorColumn(PgVectorKind.Vector, 3));

        DatabaseIndexInfo expression = detail.Indexes.Single(static x => x.Name == "embeddings_expression");
        expression.Definition.Should().NotContain("public.");
        expression.Vector!.Columns.Single().Definition.Should().Be("(embedding::text::" + WithheldNames.Token + ".vector(3))");

        // Schema > Indexes masks the per-key definitions through the same gate as the definition beside them.
        SchemaIndexes indexes = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().IndexesAsync(Scope, Token);

        indexes.Reason.Should().BeNull();
        IndexInfo schemaExpression = indexes.Indexes.Single(static x => x.Name == "embeddings_expression");
        schemaExpression.Definition.Should().NotContain("public.").And.Contain(WithheldNames.Token + ".vector_l1_ops");
        schemaExpression.Vector!.Columns.Single().Definition.Should().Be("(embedding::text::" + WithheldNames.Token + ".vector(3))");
        schemaExpression.Vector.Columns.Single().OperatorClass.Should().Be("vector_l1_ops",
            "an operator class is shown by its bare name, which names no schema");
    }

    /// <summary>
    /// The index reads' <c>pg_get_indexdef</c> briefly locks the indexed table (<c>AccessShareLock</c>, through
    /// <c>deparse_context_for</c>), so while somebody holds it in <c>ACCESS EXCLUSIVE</c> mode - a migration - those reads
    /// queue, and the read-only session's three-second <c>lock_timeout</c> ends the wait: Schema › Indexes says it is
    /// busy, and the browser's object detail fails with the reason, as every browser read does behind a held lock. The
    /// vector-column read, which touches nothing but the catalogs, answers through the lock.
    /// </summary>
    [PostgresFact]
    public async Task Behind_an_access_exclusive_lock_the_index_reads_give_up_at_the_lock_timeout_and_the_column_read_answers()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build(["*"]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();

        await using NpgsqlConnection holder = await server.OpenAsync();
        await using NpgsqlTransaction held = await holder.BeginTransactionAsync(Token);

        await using (var lockTable = new NpgsqlCommand("lock table pgv_docs.embeddings in access exclusive mode", holder, held))
        {
            await lockTable.ExecuteNonQueryAsync(Token);
        }

        try
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            SchemaIndexes indexes = await scope.ServiceProvider.GetRequiredService<ISchemaDataService>().IndexesAsync(Scope, Token);
            clock.Stop();

            indexes.Busy.Should().BeTrue(indexes.Reason);
            indexes.Reason.Should().Contain("locked right now");
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the three-second lock_timeout ends the wait, not QueryTimeout");

            clock.Restart();
            DatabaseObjectDetail detail = await scope.ServiceProvider.GetRequiredService<IDatabaseObjectService>()
                .GetObjectAsync(Scope, DocumentSchema, Table, Token);
            clock.Stop();

            detail.Found.Should().BeFalse();
            detail.Refusal.Should().Be(DatabaseRefusal.Unavailable, "a catalog read that failed is the browser's Unavailable, with the reason: " + detail.Reason);
            detail.Reason.Should().NotBeNullOrWhiteSpace();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15), "the same lock_timeout ends the browser's read");

            // The catalog-only read answers through the lock: it opens no relation it lists.
            await using NpgsqlConnection reader = await server.OpenAsync();
            await using NpgsqlTransaction reading = await reader.BeginTransactionAsync(Token);

            await using (var timeout = new NpgsqlCommand("set local lock_timeout = '3s'", reader, reading))
            {
                await timeout.ExecuteNonQueryAsync(Token);
            }

            clock.Restart();
            (await SchemaStatsQueries.ReadVectorColumnsAsync(reader, reading, [DocumentSchema, EventSchema], 30, Token)).Should().HaveCount(6);
            clock.Stop();
            clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "it never waited for the lock");

            await reading.RollbackAsync(Token);
        }
        finally
        {
            await held.RollbackAsync(Token);
        }
    }

    [PostgresFact]
    public async Task Rows_carry_the_column_type_and_a_cut_vector_is_read_again_whole()
    {
        SkipIfUnavailable();

        await using ServiceProvider provider = Build(["*"]);
        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        ITableRowService rows = scope.ServiceProvider.GetRequiredService<ITableRowService>();

        TableRowPage page = await rows.ListRowsAsync(Scope, DocumentSchema, Table, new TableRowRequest(), Token);

        page.Refusal.Should().Be(DatabaseRefusal.None, page.Reason);
        page.Rows.Should().HaveCount(2);

        int embedding = IndexOf(page, "embedding");
        int wide = IndexOf(page, "wide");
        int sparse = IndexOf(page, "sparse");

        page.Columns[embedding].Vector.Should().Be(new PgVectorColumn(PgVectorKind.Vector, 3));
        page.Rows[0].Cells[embedding].Text.Should().Be("[1,2,3]");
        PgVector.Preview(page.Rows[0].Cells[sparse].Text, false)!.Text.Should().Be("{1:1.5, 3:2}/5");

        SqlCell cut = page.Rows[0].Cells[wide];
        cut.IsTruncated.Should().BeTrue("four hundred components are more than the grid's kilobyte");
        System.Text.Encoding.UTF8.GetByteCount(cut.Text.TrimEnd('…')).Should().BeLessThanOrEqualTo(TableRowCaps.List.Text,
            "the page keeps the grid's kilobyte of a vector, like any cell, and its mark of the cut");

        PgVectorPreview preview = PgVector.Preview(cut.Text, cut.IsTruncated)!;
        preview.Text.Should().Be("[0.001, 0.002, 0.003, …]");
        preview.Total.Should().BeNull("a value cut on the server is never counted");

        TableCellValue whole = await rows.GetCellAsync(Scope, DocumentSchema, Table, page.Rows[0].Key!, "wide", Token);

        whole.Refusal.Should().Be(DatabaseRefusal.None, whole.Reason);
        whole.Truncated.Should().BeFalse();
        PgVector.Preview(whole.Text, false)!.Total.Should().Be(400);
    }

    [PostgresFact]
    public async Task Reading_every_vector_screen_creates_nothing_and_holds_no_lock_on_what_it_lists()
    {
        SkipIfUnavailable();

        IReadOnlyList<string> before = await ObjectsAsync();

        await using (ServiceProvider provider = Build(["*"]))
        {
            await using AsyncServiceScope scope = provider.CreateAsyncScope();
            IServiceProvider services = scope.ServiceProvider;

            (await services.GetRequiredService<ISchemaDataService>().TablesAsync(Scope, Token)).Reason.Should().BeNull();
            (await services.GetRequiredService<ISchemaDataService>().IndexesAsync(Scope, Token)).Reason.Should().BeNull();
            (await services.GetRequiredService<IDatabaseObjectService>().GetObjectAsync(Scope, DocumentSchema, Table, Token)).Refusal
                .Should().Be(DatabaseRefusal.None);
            (await services.GetRequiredService<ITableRowService>().ListRowsAsync(Scope, DocumentSchema, Table, new TableRowRequest(), Token)).Refusal
                .Should().Be(DatabaseRefusal.None);
        }

        (await ObjectsAsync()).Should().Equal(before, "reading pgvector's catalog entries must never run DDL - nor Marten's HiLo bookkeeping");

        // The catalog reads themselves, in one transaction the way the services run them, and pg_locks for that
        // backend before it ends. A snapshot sees only the locks still held: pg_get_indexdef's brief AccessShareLock on
        // the indexed table is taken and released inside each call, so it is not here - the lock-timeout test above
        // is what measures it. What this proves is that nothing beyond an AccessShareLock is held to the end of the
        // transaction, and nothing at all on what was listed.
        await using NpgsqlConnection connection = await server.OpenAsync();
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(Token);

        await DatabaseCatalogQueries.PinSearchPathAsync(connection, transaction, 30, Token);

        string[] schemas = [DocumentSchema, EventSchema];
        (await SchemaStatsQueries.ReadVectorColumnsAsync(connection, transaction, schemas, 30, Token)).Should().HaveCount(6);
        (await SchemaStatsQueries.ReadIndexesAsync(connection, transaction, schemas, 30, Token))
            .Count(static x => x.AccessMethodIsPgVector).Should().Be(5);
        (await DatabaseCatalogQueries.ReadRelationDetailAsync(connection, transaction, DocumentSchema, Table, 30, Token))!.Indexes
            .Count(static x => x.OperatorClasses is not null).Should().Be(5);

        List<(string Mode, string? Schema, string? Relation)> held = [];

        await using (var command = new NpgsqlCommand(
                         "select l.mode, n.nspname::text, c.relname::text from pg_catalog.pg_locks l "
                         + "left join pg_catalog.pg_class c on c.oid = l.relation "
                         + "left join pg_catalog.pg_namespace n on n.oid = c.relnamespace "
                         + "where l.pid = pg_catalog.pg_backend_pid() and l.locktype = 'relation' and l.granted",
                         connection,
                         transaction))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token))
        {
            while (await reader.ReadAsync(Token))
            {
                held.Add((reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        await transaction.RollbackAsync(Token);

        held.Should().NotBeEmpty("the catalog reads hold AccessShareLocks on the catalogs, so an empty list means nothing was measured");
        held.Should().OnlyContain(static x => x.Mode == "AccessShareLock", "no lock beyond AccessShareLock is held to the end of the transaction");
        held.Where(static x => x.Schema == DocumentSchema).Should().BeEmpty(
            "no lock on the table or its indexes is still held once the reads return (a brief one inside pg_get_indexdef is not visible here)");
    }

    private static int IndexOf(TableRowPage page, string column)
    {
        for (int i = 0; i < page.Columns.Count; i++)
        {
            if (page.Columns[i].Name == column)
            {
                return i;
            }
        }

        throw new InvalidOperationException(column + " is not on the page");
    }

    private static List<(string Name, string Value, bool IsDefault)> Parameters(PgVectorIndex index) =>
        [.. index.Parameters.Select(static x => (x.Name, x.Value, x.IsDefault))];

    private void SkipIfUnavailable() =>
        Assert.SkipWhen(server.Unavailable is not null, server.Unavailable ?? string.Empty);

    private ServiceProvider Build(string[] browsable)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, SignedInProvider>();

        services.AddMarten(options =>
        {
            options.Connection(server.ConnectionString);
            options.DatabaseSchemaName = DocumentSchema;
            options.Events.DatabaseSchemaName = EventSchema;

            // Marten's own default, said out loud: the mode a read path would have migrated under.
            options.AutoCreateSchemaObjects = JasperFx.AutoCreate.CreateOrUpdate;

            // A long id puts the lazy HiLo feature into AllActiveFeatures.
            options.Schema.For<VectorThing>();
        });

        services.AddMartenStudio(options =>
        {
            options.Capabilities.BrowseDatabase = true;

            foreach (string schema in browsable)
            {
                options.BrowsableSchemas.Add(schema);
            }
        });

        return services.BuildServiceProvider();
    }

    /// <summary>Every relation, routine and type in the class's schemas and in <c>public</c>, on a connection of the test's own.</summary>
    private async Task<IReadOnlyList<string>> ObjectsAsync()
    {
        await using NpgsqlConnection connection = await server.OpenAsync();
        await using var command = new NpgsqlCommand(
            "select 'r:' || n.nspname || '.' || c.relname from pg_class c join pg_namespace n on n.oid = c.relnamespace where n.nspname = any(@s) "
            + "union all select 'f:' || n.nspname || '.' || p.proname from pg_proc p join pg_namespace n on n.oid = p.pronamespace where n.nspname = any(@s) "
            + "union all select 't:' || n.nspname || '.' || t.typname from pg_type t join pg_namespace n on n.oid = t.typnamespace where n.nspname = any(@s) "
            + "order by 1",
            connection);
        command.Parameters.AddWithValue("s", new[] { DocumentSchema, EventSchema, "public" });

        List<string> names = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<T?> ScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default : (T) value;
    }

    /// <summary>A document type with a numeric id, so Marten's HiLo sequence feature is active.</summary>
    [DocumentAlias("vectorthing")]
    public class VectorThing
    {
        /// <summary>A <c>long</c> id, which Marten backs with the HiLo sequence.</summary>
        public long Id { get; set; }
    }

    /// <summary>Somebody is signed in.</summary>
    private sealed class SignedInProvider : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(
                new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "integration")], "test"))));
    }
}

/// <summary>
/// A pgvector server of this class's own, never reused: <c>postgres:17-alpine</c> has no pgvector.
/// </summary>
/// <remarks>
/// Pinned by tag to the pgvector release whose defaults the studio cites. When the image cannot be pulled or
/// started locally the class skips with the reason; on CI that fails the run instead, as a missing Docker does
/// (D19's rule), so no green run can be one that did not happen.
/// </remarks>
public sealed class PgVectorServerFixture : IAsyncLifetime
{
    /// <summary>The image, pinned.</summary>
    public const string Image = "pgvector/pgvector:0.8.1-pg17";

    /// <summary>The extension version that image installs.</summary>
    public const string ExtensionVersion = "0.8.1";

    /// <summary>The store's document schema, where the vector table lives beside it.</summary>
    public const string DocumentSchema = "pgv_docs";

    /// <summary>The store's event schema.</summary>
    public const string EventSchema = "pgv_events";

    /// <summary>The vector table.</summary>
    public const string Table = "embeddings";

    private const string SeedSql =
        """
        create extension if not exists vector;
        create schema pgv_docs;
        create schema pgv_events;
        create domain pgv_docs.embedding3 as vector(3);
        create table pgv_docs.embeddings (
            id int primary key,
            embedding vector(3) not null,
            summary halfvec(3),
            sparse sparsevec(5),
            unsized vector,
            wide vector(400),
            typed pgv_docs.embedding3);
        insert into pgv_docs.embeddings values
            (1, '[1,2,3]', '[1,2,3]', '{1:1.5,3:2}/5', '[1,2]',
             (select ('[' || pg_catalog.string_agg((i / 1000.0)::text, ',' order by i) || ']') from pg_catalog.generate_series(1, 400) as i)::vector(400),
             '[0.5,0.25,0.125]'),
            (2, '[0.5,-0.25,4]', '[0.5,-0.25,4]', '{2:1}/5', null, null, null);
        create index embeddings_hnsw on pgv_docs.embeddings using hnsw (embedding vector_cosine_ops) with (m = 8, ef_construction = 32);
        create index embeddings_hnsw_default on pgv_docs.embeddings using hnsw (summary halfvec_l2_ops);
        create index embeddings_ivfflat on pgv_docs.embeddings using ivfflat (embedding vector_l2_ops) with (lists = 2);
        create index embeddings_ivfflat_default on pgv_docs.embeddings using ivfflat (embedding vector_ip_ops);
        create index embeddings_expression on pgv_docs.embeddings using hnsw (((embedding::text)::vector(3)) vector_l1_ops);
        analyze pgv_docs.embeddings;
        """;

    private PostgreSqlContainer? container;

    /// <summary>Why the class cannot run here, or <see langword="null" /> when the server is up.</summary>
    public string? Unavailable { get; private set; }

    /// <summary>The server's superuser connection string.</summary>
    public string ConnectionString =>
        container?.GetConnectionString() ?? throw new InvalidOperationException(Unavailable ?? "The pgvector server is not running.");

    /// <summary>A new open connection to the server.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        return connection;
    }

    public async ValueTask InitializeAsync()
    {
        DockerAvailability.ThrowIfMissingOnContinuousIntegration();

        if (!DockerAvailability.IsAvailable)
        {
            Unavailable = DockerAvailability.SkipReason;
            return;
        }

        try
        {
            container = new PostgreSqlBuilder(Image).WithReuse(false).Build();
            await container.StartAsync();
        }
        catch (Exception exception) when (!DockerAvailability.OnContinuousIntegration)
        {
            Unavailable = "The " + Image + " image could not be started here (" + exception.Message + "); `docker pull " + Image +
                          "` makes this class run. On CI this is a failure, not a skip.";
            container = null;
            return;
        }

        await using NpgsqlConnection connection = await OpenAsync();
        await using var seed = new NpgsqlCommand(SeedSql, connection);
        await seed.ExecuteNonQueryAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.DisposeAsync();
        }
    }
}
