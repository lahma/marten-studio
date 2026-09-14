using System.Globalization;

using Marten;
using Marten.Storage;

using MartenStudio.SampleDomain.Documents;
using MartenStudio.SampleDomain.Events;

using Npgsql;

using NpgsqlTypes;

// BulkInsertMode is Weasel's, not Marten's - the enum lives in Weasel.Core even though every overload
// that takes it is on IDocumentStore.
using Weasel.Core;

namespace MartenStudio.SampleDomain.Generation;

/// <summary>Which part of a run is happening, for the progress line.</summary>
public enum DemoDataPhase
{
    /// <summary>Nothing yet.</summary>
    Starting,

    /// <summary>Writing customers.</summary>
    Customers,

    /// <summary>Writing orders, and soft-deleting some of them.</summary>
    Orders,

    /// <summary>Writing invoices, tenant by tenant.</summary>
    Invoices,

    /// <summary>Writing products.</summary>
    Products,

    /// <summary>Writing cars and trucks.</summary>
    Vehicles,

    /// <summary>Writing audit notes.</summary>
    AuditNotes,

    /// <summary>Writing the two-megabyte documents.</summary>
    MediaAssets,

    /// <summary>Adding unknown JSON properties behind Marten's back.</summary>
    Drift,

    /// <summary>Appending event streams.</summary>
    Events,

    /// <summary>Running <c>analyze</c> so the collection rail's estimates are true.</summary>
    Analyzing,

    /// <summary>Finished.</summary>
    Done,
}

/// <summary>How much has been written so far.</summary>
/// <param name="Phase">What the run is doing.</param>
/// <param name="Documents">Documents written.</param>
/// <param name="Events">Events appended.</param>
/// <param name="Streams">Streams started.</param>
public readonly record struct DemoDataCounters(DemoDataPhase Phase, long Documents, long Events, long Streams)
{
    /// <summary>Documents and events together.</summary>
    public long Rows => Documents + Events;
}

/// <summary>
/// Writes a realistic demo data set of a chosen size into a store configured by <see cref="SampleStore" />.
/// </summary>
/// <remarks>
/// <para>
/// <b>Documents go in through <c>BulkInsertAsync</c></b> - Postgres's binary <c>COPY</c> - in batches of
/// <see cref="DemoDataPlan.DocumentBatchSize" />. That is one transaction per batch, so a cancelled run
/// leaves whole batches behind rather than a torn one, and it is roughly an order of magnitude faster
/// than the session write path, which is what makes a million documents a coffee break rather than an
/// afternoon. <c>BulkInsertMode.InsertsOnly</c> is deliberate: every identity is derived from the run id,
/// so there is nothing to collide with and nothing to pay a temp table for.
/// </para>
/// <para>
/// <b>Events go in through a session</b>, in batches of <see cref="DemoDataPlan.StreamBatchSize" />
/// streams, and not through <c>BulkInsertEventsAsync</c> - which exists in Marten 9.35 and is much
/// faster - because the fast path bypasses the append pipeline and therefore never runs the store's
/// <em>inline</em> projections. The point of generating streams here is to drive the sample's
/// projections, and half of them are inline, so the slower path is the correct one. The async daemon
/// picks the rest up on its own afterwards, which is the thing the projections screen is then showing.
/// </para>
/// <para>
/// <b>Identity versus content.</b> Content is a pure function of <see cref="DemoDataPlan.Seed" />;
/// identity is a function of <see cref="RunId" />. So two runs of one plan write the same customers under
/// different ids, which is what lets a second run append instead of colliding with the first on
/// <c>customer.email</c>'s unique index - and what lets truncation find exactly one run's rows.
/// </para>
/// </remarks>
public sealed class DemoDataGenerator
{
    private readonly IDocumentStore store;
    private readonly DemoDataPlan plan;

    private long documents;
    private long events;
    private long streams;

    /// <summary>Prepares a run.</summary>
    /// <param name="store">The store to write to, configured by <see cref="SampleStore.Configure" />.</param>
    /// <param name="plan">How much of everything to write.</param>
    /// <param name="runId">
    /// The run marker. Defaults to a fresh one; pass an explicit value only to reproduce a run's
    /// identities exactly, which also means a second run with the same value will collide.
    /// </param>
    public DemoDataGenerator(IDocumentStore store, DemoDataPlan plan, string? runId = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(plan);

        this.store = store;
        this.plan = plan;
        RunId = runId ?? NewRunId();
    }

    /// <summary>The marker every document of this run carries.</summary>
    public string RunId { get; }

    /// <summary>A fresh run id: short, sortable, and safe inside an email address.</summary>
    public static string NewRunId() =>
        "g" + DateTime.UtcNow.ToString("yyMMddHHmmss", CultureInfo.InvariantCulture)
        + Random.Shared.Next(0, 4096).ToString("x3", CultureInfo.InvariantCulture);

    /// <summary>The stream id of generated stream <paramref name="index" /> of run <paramref name="runId" />.</summary>
    /// <remarks>
    /// Public and static because truncation needs it: a stream cannot be found by predicate the way a
    /// document can, so the only way back to the streams a run wrote is to derive their ids again.
    /// </remarks>
    public static Guid StreamId(string runId, int index) => IdFor(runId, "stream", index);

    /// <summary>
    /// The id of generated customer <paramref name="index" /> of run <paramref name="runId" />.
    /// </summary>
    /// <remarks>
    /// The first <see cref="DemoDataPlan.DriftedDocuments" /> of these are the ones given JSON keys no
    /// CLR property matches, so this is how a test - or a curious developer - finds a document whose
    /// round-trip diff has something to report.
    /// </remarks>
    public static Guid CustomerId(string runId, int index) => IdFor(runId, "customer", index);

    /// <summary>
    /// Writes everything the plan asks for, reporting after every batch.
    /// </summary>
    /// <param name="progress">Called after each batch, on the generating task. May be <see langword="null" />.</param>
    /// <param name="cancellationToken">Stops the run between batches.</param>
    /// <returns>What was written.</returns>
    /// <exception cref="OperationCanceledException">The run was cancelled.</exception>
    public async Task<DemoDataCounters> GenerateAsync(
        Action<DemoDataCounters>? progress,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;

        Report(progress, DemoDataPhase.Starting);

        await GenerateCustomersAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateOrdersAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateInvoicesAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateProductsAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateVehiclesAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateAuditNotesAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateMediaAssetsAsync(progress, cancellationToken).ConfigureAwait(false);
        await DriftDocumentsAsync(progress, cancellationToken).ConfigureAwait(false);
        await GenerateEventsAsync(progress, cancellationToken).ConfigureAwait(false);

        if (plan.AnalyzeWhenDone)
        {
            Report(progress, DemoDataPhase.Analyzing);
            await AnalyzeAsync(cancellationToken).ConfigureAwait(false);
        }

        await RecordRunAsync(startedAt, completed: true, cancellationToken).ConfigureAwait(false);

        Report(progress, DemoDataPhase.Done);

        return Counters(DemoDataPhase.Done);
    }

    /// <summary>
    /// Writes the run's record even though it did not finish, so truncation can still find its streams.
    /// </summary>
    /// <param name="startedAt">When the run began.</param>
    /// <param name="cancellationToken">Cancels the write. Pass <see cref="CancellationToken.None" />.</param>
    public Task RecordPartialRunAsync(DateTimeOffset startedAt, CancellationToken cancellationToken = default) =>
        RecordRunAsync(startedAt, completed: false, cancellationToken);

    // ---------------------------------------------------------------------------------------------
    // Documents
    // ---------------------------------------------------------------------------------------------

    private Task GenerateCustomersAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken) =>
        BulkAsync(
            DemoDataPhase.Customers,
            plan.Customers,
            index => new Customer
            {
                Id = IdFor(RunId, "customer", index),
                Name = DemoDataWords.PersonName(plan.Seed, index),
                Email = DemoDataWords.Email(RunId, index),
                Address = new Address(
                    DemoDataWords.Range(plan.Seed, index, 3, 1, 200).ToString(CultureInfo.InvariantCulture)
                    + " " + DemoDataWords.Pick(DemoDataWords.Streets, plan.Seed, index, 4),
                    DemoDataWords.Pick(DemoDataWords.Cities, plan.Seed, index, 5),
                    (10_000 + DemoDataWords.Range(plan.Seed, index, 6, 0, 89_999))
                        .ToString(CultureInfo.InvariantCulture),
                    DemoDataWords.Pick(DemoDataWords.Countries, plan.Seed, index, 7)),
                Tags = TagsFor(index),
                RegisteredAt = DemoDataWords.Timestamp(plan.Seed, index, 8),
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken);

    private async Task GenerateOrdersAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken)
    {
        await BulkAsync(
            DemoDataPhase.Orders,
            plan.Orders,
            index => new Order
            {
                Id = new OrderId(IdFor(RunId, "order", index)),
                // Only ever a customer this run wrote, so the declared foreign key holds and the
                // detail page's "related documents" panel resolves.
                CustomerId = IdFor(RunId, "customer", plan.Customers == 0 ? 0 : index % plan.Customers),
                Reference = "ORD-" + RunId + "-" + index.ToString("D7", CultureInfo.InvariantCulture),
                Total = DemoDataWords.Money(plan.Seed, index, 11, 500, 500_000),
                // The soft-deleted ones are the first SoftDeletedOrders of them, and they are marked
                // "Voided" so that one predicate can find them again without a second id list.
                Status = index < plan.SoftDeletedOrders
                    ? VoidedStatus
                    : DemoDataWords.Pick(OrderStatuses, plan.Seed, index, 12),
                Lines = LinesFor(index),
                PlacedAt = DemoDataWords.Timestamp(plan.Seed, index, 13),
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        if (plan.SoftDeletedOrders <= 0 || plan.Customers < 0)
        {
            return;
        }

        // One statement, not one per order: DeleteWhere compiles to a single UPDATE over the predicate,
        // which is the difference between two hundred milliseconds and thirty thousand round trips.
        await using IDocumentSession session = store.LightweightSession();
        string runId = RunId;
        session.DeleteWhere<Order>(x => x.GeneratedRun == runId && x.Status == VoidedStatus);
        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task GenerateInvoicesAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken)
    {
        if (plan.Invoices <= 0)
        {
            return;
        }

        IReadOnlyList<string> tenants = SampleStore.TenantIds;
        int perTenant = plan.Invoices / tenants.Count;
        int remainder = plan.Invoices % tenants.Count;

        for (int t = 0; t < tenants.Count; t++)
        {
            string tenantId = tenants[t];
            int count = perTenant + (t < remainder ? 1 : 0);
            int offset = (t * perTenant) + Math.Min(t, remainder);

            await BulkAsync(
                DemoDataPhase.Invoices,
                count,
                local => NewInvoice(offset + local),
                progress,
                cancellationToken,
                tenantId).ConfigureAwait(false);
        }
    }

    private Invoice NewInvoice(int index) => new()
    {
        // Id is left at zero: Marten's HiLo sequence assigns it during the bulk load, which is the
        // behaviour worth demonstrating - an int-keyed collection whose ids the database chose.
        CustomerId = IdFor(RunId, "customer", plan.Customers == 0 ? 0 : index % plan.Customers),
        Number = "INV-" + RunId + "-" + index.ToString("D7", CultureInfo.InvariantCulture),
        Amount = DemoDataWords.Money(plan.Seed, index, 21, 1_000, 900_000),
        Paid = DemoDataWords.Noise(plan.Seed, index, 22) % 3 != 0,
        IssuedAt = DemoDataWords.Timestamp(plan.Seed, index, 23),
        GeneratedRun = RunId,
    };

    private Task GenerateProductsAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken) =>
        BulkAsync(
            DemoDataPhase.Products,
            plan.Products,
            index => new Product
            {
                // A string primary key the application assigns, and one that has to be unique across
                // runs for the same reason the email does.
                Id = "SKU-" + RunId + "-" + index.ToString("D6", CultureInfo.InvariantCulture),
                Name = DemoDataWords.Pick(DemoDataWords.ProductAdjectives, plan.Seed, index, 31)
                    + " " + DemoDataWords.Pick(DemoDataWords.ProductNouns, plan.Seed, index, 32),
                Category = DemoDataWords.Pick(DemoDataWords.Categories, plan.Seed, index, 33),
                Price = DemoDataWords.Money(plan.Seed, index, 34, 199, 250_000),
                Discontinued = DemoDataWords.Noise(plan.Seed, index, 35) % 11 == 0,
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken);

    private async Task GenerateVehiclesAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken)
    {
        if (plan.Vehicles <= 0)
        {
            return;
        }

        // Two cars to every truck, both through the root's bulk loader, because a hierarchy is one table
        // and Marten's SubClassBulkLoader is what writes mt_doc_type for the concrete type.
        int trucks = plan.Vehicles / 3;
        int cars = plan.Vehicles - trucks;

        await BulkAsync(
            DemoDataPhase.Vehicles,
            cars,
            index => (Vehicle) new Car
            {
                Id = IdFor(RunId, "car", index),
                Make = DemoDataWords.Pick(DemoDataWords.Makes, plan.Seed, index, 41),
                Model = "C" + DemoDataWords.Range(plan.Seed, index, 42, 100, 999).ToString(CultureInfo.InvariantCulture),
                Year = DemoDataWords.Range(plan.Seed, index, 43, 2005, 2027),
                Doors = DemoDataWords.Noise(plan.Seed, index, 44) % 2 == 0 ? 5 : 3,
                IsConvertible = DemoDataWords.Noise(plan.Seed, index, 45) % 9 == 0,
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken).ConfigureAwait(false);

        await BulkAsync(
            DemoDataPhase.Vehicles,
            trucks,
            index => (Vehicle) new Truck
            {
                Id = IdFor(RunId, "truck", index),
                Make = DemoDataWords.Pick(DemoDataWords.Makes, plan.Seed, index, 46),
                Model = "T" + DemoDataWords.Range(plan.Seed, index, 47, 100, 999).ToString(CultureInfo.InvariantCulture),
                Year = DemoDataWords.Range(plan.Seed, index, 48, 2005, 2027),
                PayloadTonnes = DemoDataWords.Range(plan.Seed, index, 49, 15, 400) / 10m,
                Axles = 2 + (int) (DemoDataWords.Noise(plan.Seed, index, 50) % 3),
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken).ConfigureAwait(false);
    }

    private Task GenerateAuditNotesAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken) =>
        BulkAsync(
            DemoDataPhase.AuditNotes,
            plan.AuditNotes,
            index => new AuditNote
            {
                Id = IdFor(RunId, "audit", index),
                Subject = DemoDataWords.Pick(DemoDataWords.ProductAdjectives, plan.Seed, index, 51)
                    + " " + DemoDataWords.Pick(DemoDataWords.ProductNouns, plan.Seed, index, 52)
                    + " #" + index.ToString(CultureInfo.InvariantCulture),
                Body = "Generated audit note " + index.ToString(CultureInfo.InvariantCulture)
                    + " for run " + RunId + ". Every optional metadata column on this collection is enabled.",
                Severity = DemoDataWords.Pick(DemoDataWords.Severities, plan.Seed, index, 53),
                GeneratedRun = RunId,
            },
            progress,
            cancellationToken);

    private Task GenerateMediaAssetsAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken) =>
        BulkAsync(
            DemoDataPhase.MediaAssets,
            plan.MediaAssets,
            index => BuildMediaAsset(index),
            progress,
            cancellationToken,
            // One ~2 MB document per batch: five thousand of these in one COPY is ten gigabytes in
            // memory, which is a way to bring down the host rather than to measure it.
            batchSizeOverride: 1);

    private MediaAsset BuildMediaAsset(int index)
    {
        byte[] payload = new byte[MediaAssetBytes];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte) (((i * 31) + index) % 251);
        }

        return new MediaAsset
        {
            Id = IdFor(RunId, "media", index),
            FileName = "generated-" + RunId + "-" + index.ToString("D3", CultureInfo.InvariantCulture) + ".bin",
            ContentType = "application/octet-stream",
            ByteCount = payload.Length,
            Base64 = Convert.ToBase64String(payload),
            GeneratedRun = RunId,
        };
    }

    // ---------------------------------------------------------------------------------------------
    // Unknown-property drift
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// Adds JSON keys no CLR property matches, to a handful of generated customers.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the case D7 is about: Marten has no untyped write, so editing such a document in the
    /// studio deserializes into <c>Customer</c> and serializes back, and the extra keys are gone. The
    /// dialog is supposed to say so before the save, and a demo database with no drifted document has no
    /// way to show that it does.
    /// </para>
    /// <para>
    /// Raw SQL, because there is no other way to write a key the type does not have. This is the sample,
    /// not the library, so AGENTS.md hard rule 4 does not reach it - but the identifier is still built
    /// from Marten's own mapping and quoted, and the id and the patch are parameters.
    /// </para>
    /// </remarks>
    private async Task DriftDocumentsAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken)
    {
        int count = Math.Min(plan.DriftedDocuments, plan.Customers);
        if (count <= 0)
        {
            return;
        }

        Report(progress, DemoDataPhase.Drift);

        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
        await using NpgsqlConnection connection = databases[0].CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        string table = DemoDataSql.QuoteQualified(store.Options.DatabaseSchemaName, "mt_doc_customer");

        for (int i = 0; i < count; i++)
        {
            await using var command = new NpgsqlCommand(
                "update " + table + " set data = data || @patch::jsonb where id = @id", connection);

            command.Parameters.Add(new NpgsqlParameter("patch", NpgsqlDbType.Text)
            {
                Value = string.Format(
                    CultureInfo.InvariantCulture,
                    """{{"LegacyNickname":"nick-{0}","MigratedFrom":"v1","RetiredScore":{1}}}""",
                    i,
                    DemoDataWords.Range(plan.Seed, i, 61, 1, 100)),
            });
            command.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid)
            {
                Value = IdFor(RunId, "customer", i),
            });

            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Events
    // ---------------------------------------------------------------------------------------------

    private async Task GenerateEventsAsync(Action<DemoDataCounters>? progress, CancellationToken cancellationToken)
    {
        if (plan.Streams <= 0)
        {
            return;
        }

        Report(progress, DemoDataPhase.Events);

        for (int start = 0; start < plan.Streams; start += plan.StreamBatchSize)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int end = Math.Min(plan.Streams, start + plan.StreamBatchSize);

            await using IDocumentSession session = store.LightweightSession();

            // The demo store enables correlation, causation and headers, and a column that is enabled
            // and always null tells the studio nothing.
            session.CorrelationId = "demo-data";
            session.CausationId = "DemoDataGenerator/" + RunId;
            session.SetHeader("generated-run", RunId);

            long batchEvents = 0;

            for (int index = start; index < end; index++)
            {
                List<object> batch = EventsFor(index);
                batchEvents += batch.Count;

                session.Events.StartStream<OrderSummary>(StreamId(RunId, index), batch);
            }

            await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            events += batchEvents;
            streams += end - start;

            Report(progress, DemoDataPhase.Events);
        }
    }

    private List<object> EventsFor(int index)
    {
        int span = Math.Max(1, plan.MaxEventsPerStream - plan.MinEventsPerStream + 1);
        int count = plan.MinEventsPerStream + (index % span);

        DateTimeOffset placedAt = DemoDataWords.Timestamp(plan.Seed, index, 71);
        string customer = DemoDataWords.PersonName(plan.Seed, index);

        var batch = new List<object>(count + 1)
        {
            new OrderPlaced(StreamId(RunId, index), customer, placedAt),
        };

        // Everything between the first and the last event is a line item, so the inline OrderSummary
        // ends up with a believable total and the DailySales roll-up has volume to add.
        for (int line = 1; line < count - 1; line++)
        {
            batch.Add(new ItemAdded(
                "SKU-" + DemoDataWords.Range(plan.Seed, index + line, 72, 1, 9999).ToString("D4", CultureInfo.InvariantCulture),
                1 + (int) (DemoDataWords.Noise(plan.Seed, index + line, 73) % 4),
                DemoDataWords.Money(plan.Seed, index + line, 74, 199, 40_000),
                placedAt.AddMinutes(line)));
        }

        // One stream in PoisonStreamInterval carries the SKU ShipmentTracker throws on. Nothing here
        // writes a dead letter: the daemon does, which is the only way to get an honest one.
        if (plan.PoisonStreamInterval > 0 && index > 0 && index % plan.PoisonStreamInterval == 0)
        {
            batch.Add(new ItemAdded(ItemAdded.PoisonSku, 1, 999m, placedAt.AddMinutes(count)));
        }

        if (count > 1)
        {
            batch.Add(DemoDataWords.Noise(plan.Seed, index, 75) % 5 == 0
                ? new OrderCancelled(
                    DemoDataWords.Pick(CancelReasons, plan.Seed, index, 76),
                    placedAt.AddHours(2))
                : new OrderShipped(
                    "Carrier " + DemoDataWords.Range(plan.Seed, index, 77, 1, 9).ToString(CultureInfo.InvariantCulture),
                    placedAt.AddHours(6)));
        }

        return batch;
    }

    // ---------------------------------------------------------------------------------------------
    // Bookkeeping
    // ---------------------------------------------------------------------------------------------

    private async Task RecordRunAsync(DateTimeOffset startedAt, bool completed, CancellationToken cancellationToken)
    {
        await using IDocumentSession session = store.LightweightSession();

        session.Store(new DemoDataRun
        {
            Id = RunId,
            Size = plan.Size.ToString(),
            Seed = plan.Seed,
            Streams = (int) streams,
            Documents = documents,
            Events = events,
            StartedAt = startedAt,
            CompletedAt = completed ? DateTimeOffset.UtcNow : null,
            Completed = completed,
        });

        await session.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Updates <c>pg_class.reltuples</c> for the demo's tables, which is what D8's counts read.
    /// </summary>
    /// <remarks>
    /// Autovacuum gets there eventually; "eventually" is minutes after a bulk load, and a collections
    /// rail that says "~0" next to a million rows reads as a studio bug rather than as a stale statistic.
    /// </remarks>
    private async Task AnalyzeAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<IMartenDatabase> databases = await store.Storage.AllDatabases().ConfigureAwait(false);
        await using NpgsqlConnection connection = databases[0].CreateConnection();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        // `analyze` takes no parameters and no schema-wide form, so each table is named explicitly and
        // quoted. The schema comes from the store's own options rather than from SampleStore's constants,
        // because the integration suite points the same domain at a schema per test class.
        string documents = store.Options.DatabaseSchemaName;
        string eventStore = store.Options.Events.DatabaseSchemaName;

        (string Schema, string Table)[] tables =
        [
            (documents, "mt_doc_customer"),
            (documents, "mt_doc_order"),
            (documents, "mt_doc_invoice"),
            (documents, "mt_doc_product"),
            (documents, "mt_doc_vehicle"),
            (documents, "mt_doc_auditnote"),
            (documents, "mt_doc_mediaasset"),
            (documents, "mt_doc_ordersummary"),
            (eventStore, "mt_events"),
            (eventStore, "mt_streams"),
        ];

        foreach ((string schema, string table) in tables)
        {
            string quoted = DemoDataSql.QuoteQualified(schema, table);

            await using var command = new NpgsqlCommand("analyze " + quoted, connection)
            {
                CommandTimeout = 300,
            };

            try
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (PostgresException exception) when (exception.SqlState == PostgresErrorCodes.UndefinedTable)
            {
                // A table the demo never wrote to - a plan with no audit notes, say - simply is not
                // there yet, and analysing it is not what the run was for.
            }
        }
    }

    // ---------------------------------------------------------------------------------------------
    // Plumbing
    // ---------------------------------------------------------------------------------------------

    private async Task BulkAsync<T>(
        DemoDataPhase phase,
        int count,
        Func<int, T> build,
        Action<DemoDataCounters>? progress,
        CancellationToken cancellationToken,
        string? tenantId = null,
        int? batchSizeOverride = null)
        where T : notnull
    {
        if (count <= 0)
        {
            return;
        }

        Report(progress, phase);

        int batchSize = Math.Max(1, batchSizeOverride ?? plan.DocumentBatchSize);
        var batch = new List<T>(Math.Min(batchSize, count));

        for (int index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            batch.Add(build(index));

            if (batch.Count < batchSize)
            {
                continue;
            }

            await FlushAsync(batch, tenantId, cancellationToken).ConfigureAwait(false);
            documents += batch.Count;
            batch.Clear();

            Report(progress, phase);
        }

        if (batch.Count > 0)
        {
            await FlushAsync(batch, tenantId, cancellationToken).ConfigureAwait(false);
            documents += batch.Count;
            Report(progress, phase);
        }
    }

    private Task FlushAsync<T>(List<T> batch, string? tenantId, CancellationToken cancellationToken)
        where T : notnull =>
        tenantId is null
            ? store.BulkInsertAsync(batch, BulkInsertMode.InsertsOnly, batch.Count, cancellationToken)
            : store.BulkInsertAsync(tenantId, batch, BulkInsertMode.InsertsOnly, batch.Count, cancellationToken);

    private void Report(Action<DemoDataCounters>? progress, DemoDataPhase phase) =>
        progress?.Invoke(Counters(phase));

    private DemoDataCounters Counters(DemoDataPhase phase) => new(phase, documents, events, streams);

    private List<string> TagsFor(int index)
    {
        var tags = new List<string>(2) { DemoDataWords.Pick(DemoDataWords.Tags, plan.Seed, index, 9) };

        if (DemoDataWords.Noise(plan.Seed, index, 10) % 4 == 0)
        {
            tags.Add(DemoDataWords.Pick(DemoDataWords.Tags, plan.Seed, index, 10));
        }

        return tags;
    }

    private List<OrderLine> LinesFor(int index)
    {
        int lines = 1 + (int) (DemoDataWords.Noise(plan.Seed, index, 14) % 4);
        var result = new List<OrderLine>(lines);

        for (int i = 0; i < lines; i++)
        {
            result.Add(new OrderLine(
                "SKU-" + DemoDataWords.Range(plan.Seed, index + i, 15, 1, 9999).ToString("D4", CultureInfo.InvariantCulture),
                1 + (int) (DemoDataWords.Noise(plan.Seed, index + i, 16) % 5),
                DemoDataWords.Money(plan.Seed, index + i, 17, 199, 40_000)));
        }

        return result;
    }

    /// <summary>
    /// A Guid that is stable for (run, kind, index), different for every run, and spread evenly over the
    /// whole uuid range.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The run id goes into the hash rather than the index alone, which is what lets a second run append
    /// a second set of rows: identical ids would need <c>BulkInsertMode.IgnoreDuplicates</c> and a temp
    /// table on every batch, and would still have collided on <c>customer.email</c>'s unique index.
    /// </para>
    /// <para>
    /// <b>Every byte is mixed, not only the last four.</b> The obvious implementation - a hash of
    /// (run, kind) in the high bytes and the index in the low four - gives a run whose ids all share a
    /// twenty-four-character prefix. That is not a cosmetic problem: it makes the whole collection one
    /// narrow band of the primary key's range, so an id-ordered page anywhere else in the range is empty
    /// and every insert lands on the same btree page. The 64-bit finalizer below is a bijection, so two
    /// different indices of one run can never produce the same id either.
    /// </para>
    /// </remarks>
    private static Guid IdFor(string runId, string kind, int index)
    {
        Span<byte> bytes = stackalloc byte[16];

        ulong seed = Fnv1a(runId) ^ (Fnv1a(kind) * 1099511628211ul);
        ulong low = Mix(seed ^ (uint) index);
        ulong high = Mix(low ^ 0x9E3779B97F4A7C15ul ^ ((ulong) (uint) index << 32));

        BitConverter.TryWriteBytes(bytes, low);
        BitConverter.TryWriteBytes(bytes[8..], high);

        return new Guid(bytes);
    }

    /// <summary>The 64-bit avalanche of MurmurHash3, which is a bijection and therefore collision-free.</summary>
    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value ^= value >> 33;
            value *= 0xFF51AFD7ED558CCDul;
            value ^= value >> 33;
            value *= 0xC4CEB9FE1A85EC53ul;
            value ^= value >> 33;
            return value;
        }
    }

    private static ulong Fnv1a(string value)
    {
        ulong hash = 14695981039346656037ul;

        foreach (char c in value)
        {
            hash ^= c;
            hash *= 1099511628211ul;
        }

        return hash;
    }

    /// <summary>The status the soft-deleted generated orders carry, so one predicate can find them.</summary>
    internal const string VoidedStatus = "Voided";

    /// <summary>Roughly two megabytes once base64 has had its way with it.</summary>
    private const int MediaAssetBytes = 1_500_000;

    private static readonly string[] OrderStatuses = ["Placed", "Placed", "Picking", "Shipped", "Delivered"];

    private static readonly string[] CancelReasons =
    [
        "Out of stock", "Customer changed their mind", "Payment failed", "Duplicate order", "Address unreachable",
    ];
}
