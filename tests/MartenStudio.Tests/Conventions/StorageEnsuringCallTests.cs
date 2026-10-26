using System.Reflection;
using System.Reflection.Emit;
using System.Text.RegularExpressions;

using JasperFx.Events;

using Marten;

using Npgsql;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// DL: no read path in <c>src/</c> reaches Marten's <c>EnsureStorageExists</c>. Every call to a Marten member that
/// does is one of today's sites, made in the method the list names, and every one of today's sites is a
/// capability-gated, audited write - or F7's LINQ composition, which Marten never executes.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why.</b> Read at Marten <c>V9.39.0</c> and <c>V9.50.2</c> (the files are identical at both): every LINQ terminal
/// (<c>MartenLinqQueryProvider.EnsureStorageExistsAsync</c>), <c>ToCommand</c> / <c>ToPreviewCommand</c> /
/// <c>ExplainAsync</c> (<c>MartenLinqQueryable.ToPreviewCommand</c>), every <c>LoadAsync</c>, <c>LoadManyAsync</c>,
/// <c>CheckExistsAsync</c>, <c>MetadataForAsync</c>, string and compiled <c>QueryAsync</c>, <c>AdvancedSql</c>'s
/// <c>QueryAsync</c> and <c>StreamAsync</c>, the JSON loader (<c>FindByIdAsync</c>, <c>FindJsonByIdAsync</c>,
/// <c>StreamById</c>) and batch, every <c>session.Events</c> read (<c>FetchStreamAsync</c>, <c>FetchStreamStateAsync</c>,
/// <c>AggregateStreamAsync</c>, <c>LoadAsync</c>, <c>QueryEventsAsync</c>, <c>FetchForWriting</c>, <c>FetchLatest</c>, the
/// DCB tag reads and <c>CompactStreamAsync</c>), the concurrent appends (<c>AppendOptimistic</c>, <c>AppendExclusive</c>),
/// JasperFx's <c>IEventStore</c> explorer as <c>DocumentStore</c> implements it (<c>GetRecentStreamsAsync</c>,
/// <c>ReadStreamAsync</c>, <c>GetStreamMetadataAsync</c>, <c>QueryByTagsAsync</c>, <c>GetProjectedStateForTagsAsync</c>,
/// <c>RehydrateAtVersionAsync</c>, <c>RehydrateAtVersionByNameAsync</c>, <c>GetProjectionStatusesAsync</c>), the daemon's
/// <c>StartProjectionBatchAsync</c> and <c>FetchProjectionStorageAsync</c>, the event-storage and document-cleaner members
/// of <c>MartenDatabase</c>, bulk insertion and <c>SaveChangesAsync</c> call <c>IMartenDatabase.EnsureStorageExistsAsync</c>
/// for the type they touch. Under the database's own <c>AutoCreate</c> that is Weasel's <c>ensureStorageExistsAsync</c>
/// (<c>9.33.0</c>): it creates the schemas, calls <c>AllObjects()</c> - which on a store with a HiLo id applies
/// <c>mt_hilo</c> - and runs the type's migration, which under <c>CreateOrUpdate</c> is not additive, the first time the
/// process asks. A read path that reached one wrote DDL because somebody opened a page: hard rule 14, which names this
/// test.
/// </para>
/// <para>
/// <b>Two layers, one list.</b> The <em>source</em> layer reads <c>src/</c> after <see cref="SourceScanner" /> has
/// blanked comments and strings, and matches a member access followed by an argument list. It cannot tell Marten's
/// <c>LoadAsync</c> or <c>QueryAsync</c> from the studio's own helpers of those names - the health runner's
/// <c>context.Sql.QueryAsync</c>, the preferences' <c>HealthMutes.LoadAsync</c> - so for those, and for the other
/// members whose type argument cannot be inferred, it matches only the generic spelling Marten's members need; and two
/// names the studio's own services also use, <c>GetRecentStreamsAsync</c> and <c>ReadStreamAsync</c>, it does not match
/// at all. The <em>compiled</em> layer reads the IL of every method in <c>MartenStudio.dll</c> and matches a call, a
/// virtual call or a delegate's <c>ldftn</c> whose target is declared in Marten, JasperFx or Weasel - so it sees the
/// non-generic <c>QueryAsync(type, sql)</c>, an event <c>LoadAsync(id)</c>, a method group and a lambda's body, and never
/// a studio method that merely shares a name. Both are held to <see cref="AllowedSites" />: the source counts by file,
/// and the compiled layer by the type each file declares <em>and</em> by the method each call is made in, so a call that
/// moves from a write into a read fails even when no count changes. A method token the scan cannot resolve is a failure,
/// not a skip.
/// </para>
/// <para>
/// <b>It counts call sites, not reach.</b> A helper that makes one of these calls is one site however many paths reach
/// it, and nothing here follows a studio method to what calls it - the list is by file, member and method, and the
/// "needs" on each entry, the doc comment at each site and the live <c>NoDdl</c> suites are what say the path is a write.
/// The member list was read from Marten's and JasperFx's sources at both tags and grew in review; it is not proven
/// complete. That is why JasperFx's <c>IEventStore</c> - whose explorer reads each ensure the event store first - is
/// fenced as a whole as well: the studio calls none of its members unless <see cref="AllowedEventStoreCalls" /> says so.
/// <see cref="NoSchemaBuildingCallTests" /> predates this test and still pins its twelve event-store members on its own.
/// </para>
/// <para>
/// <b>Marten 9.52 widened what ensures</b> (read at <c>V9.54.1</c>): the high-water advance - which the studio calls, from
/// its capability-gated, audited correction - its per-database form and preview on <c>IEventStore</c>, the projection-floor
/// seam, and two new LINQ reads. And it moved two ensures the studio does not call itself: the daemon reads a projection's
/// stored floor through <c>FetchProjectionFloorAsync</c> on every continuous start and every rebuild, so Start and Rebuild -
/// both behind capabilities and audited - now ensure the event storage inside the daemon. No read path reaches a daemon
/// start; the live <c>NoDdl</c> suites are what would see one that did.
/// </para>
/// </remarks>
public class StorageEnsuringCallTests
{
    /// <summary>
    /// Marten's members that call <c>EnsureStorageExistsAsync</c>, by a name nothing in the studio shares - matched with
    /// or without type arguments.
    /// </summary>
    private static readonly string[] Members =
    [
        // LINQ terminals (Marten.QueryableExtensions, IMartenQueryable) and the statement builders that ensure first.
        "ToListAsync", "AnyAsync", "CountAsync", "LongCountAsync", "FirstAsync", "FirstOrDefaultAsync", "SingleAsync",
        "SingleOrDefaultAsync", "SumAsync", "MinAsync", "MaxAsync", "AverageAsync", "ToAsyncEnumerable",
        "StreamJsonArray", "StreamJsonFirst", "StreamJsonFirstOrDefault", "StreamJsonSingle", "StreamJsonSingleOrDefault",
        "StreamPagedJsonArray", "ToJsonArray", "ToJsonFirst", "ToJsonFirstOrDefault", "ToJsonSingle", "ToJsonSingleOrDefault",
        "ToPagedListAsync", "ToJsonPageByCursorAsync", "AggregateToAsync", "AggregateToManyAsync",
        "ToCommand", "ToPreviewCommand", "ExplainAsync",
        "QueryAllRawEvents", "QueryRawEventDataOnly",

        // The session.
        "LoadManyAsync", "CheckExistsAsync", "MetadataForAsync", "CreateBatchQuery", "SaveChangesAsync", "SaveChanges",

        // session.Events, and the concurrent appends, which read the stream's version first.
        "FetchStreamAsync", "FetchStreamStateAsync", "AggregateStreamAsync", "AggregateStreamToLastKnownAsync",
        "QueryEventsAsync", "FetchForWriting", "FetchForExclusiveWriting", "FetchLatest", "EventsExistAsync",
        "QueryByTagsAsync", "AggregateByTagsAsync", "FetchForWritingByTags", "AppendOptimistic", "AppendExclusive",

        // JasperFx's IEventStore explorer, as DocumentStore.EventStoreExplorer.cs implements it: each read opens with
        // EnsureStorageExistsAsync(typeof(IEvent)) or reaches QueryByTagsAsync or AggregateStreamAsync, which do.
        "GetStreamMetadataAsync", "GetProjectedStateForTagsAsync", "RehydrateAtVersionByNameAsync", "GetProjectionStatusesAsync",

        // The daemon's projection batch.
        "StartProjectionBatchAsync",

        // IMartenDatabase: the ensure itself, the event storage, the dead letters, the cleaner, bulk insertion.
        "EnsureStorageExists", "EnsureStorageExistsAsync", "MarkEventsAsSkipped", "WriteExtendedProgressionAsync",
        "FindEventStoreFloorAtTimeAsync", "FetchHighestEventSequenceNumber", "FetchMaxEventSequenceAsync",
        "FetchEventStoreStatistics", "AllProjectionProgress", "ReadProjectionProgressAsync",
        "DeleteProjectionProgressByShardNameAsync", "FetchProjectionProgressFor", "ProjectionProgressFor",
        "CountDeadLetterEventsAsync", "FetchDeadLetterCountsAsync", "QueryDeadLetterEventsAsync",
        "DeleteAllDocumentsAsync", "DeleteDocumentsByTypeAsync", "DeleteDocumentsExceptAsync", "CompletelyRemoveAsync",
        "CompletelyRemoveAllAsync", "DeleteAllEventDataAsync", "DeleteSingleEventStreamAsync",
        "BulkInsertAsync", "BulkInsertDocumentsAsync", "BulkInsertEnlistTransactionAsync",

        // Marten 9.52-9.54, read at V9.54.1: the high-water advance - AdvancedOperations' two overloads and IEventStore's
        // per-database one go through DocumentStore.highWaterDetectorFor, which ensures IEvent - and its preview; the
        // projection-floor seam on MartenDatabase; and the LINQ reads over event data and every tenant's stream states.
        "AdvanceHighWaterMarkToLatestAsync", "PreviewHighWaterAdvanceAsync",
        "FetchProjectionFloorAsync", "TrySetProjectionFloorAsync", "SetProjectionFloorAsync",
        "QueryEventData", "QueryStreamStatesForAllTenants",
    ];

    /// <summary>
    /// The members matched by the source layer only with the type arguments Marten's own need, because the studio has a
    /// namesake (<c>Query&lt;T&gt;()</c>, <c>LoadAsync&lt;T&gt;(id)</c>, <c>QueryAsync&lt;T&gt;(sql)</c>,
    /// <c>Events.CompactStreamAsync&lt;T&gt;(id)</c>) or because the name is too common to match bare
    /// (<c>AdvancedSql.StreamAsync&lt;T&gt;</c>, <c>Json.FindByIdAsync&lt;T&gt;</c>, <c>FindJsonByIdAsync&lt;T&gt;</c>,
    /// <c>StreamById&lt;T&gt;</c>, <c>FetchProjectionStorageAsync&lt;TDoc, TId&gt;</c>,
    /// <c>RehydrateAtVersionAsync&lt;TAggregate&gt;</c>) - each of which can only be called with them. The compiled layer
    /// matches every spelling.
    /// </summary>
    private static readonly string[] GenericOnlyMembers =
    [
        "Query", "LoadAsync", "QueryAsync", "CompactStreamAsync", "StreamAsync", "FindByIdAsync", "FindJsonByIdAsync",
        "StreamById", "FetchProjectionStorageAsync", "RehydrateAtVersionAsync",
    ];

    /// <summary>
    /// Members of JasperFx's <c>IEventStore</c> explorer whose names the studio's own code also uses
    /// (<c>IEventDataService.GetRecentStreamsAsync</c>, which the Overview calls, and <c>EventDataService</c>'s private
    /// <c>ReadStreamAsync</c>): matched by the compiled layer only, which tells them apart by declaring assembly.
    /// </summary>
    private static readonly string[] CompiledOnlyMembers = ["GetRecentStreamsAsync", "ReadStreamAsync"];

    /// <summary>The assemblies a compiled call's target has to be declared in to count.</summary>
    private static readonly HashSet<string> MartenAssemblies =
        new(["Marten", "JasperFx", "JasperFx.Events", "Weasel.Core", "Weasel.Postgresql", "Weasel.Storage"], StringComparer.Ordinal);

    /// <summary>
    /// A call: a member access, the member, optional type arguments (two levels of nesting), and an argument list.
    /// </summary>
    private static readonly Regex StorageEnsuringCall = new(
        @"\.\s*(?:(?<member>" + string.Join("|", Members) + @")\s*(?:<" + TypeArguments + @">\s*)?\("
        + @"|(?<member>" + string.Join("|", GenericOnlyMembers) + @")\s*<" + TypeArguments + @">\s*\()",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    private const string TypeArguments = @"(?:[^<>()]|<(?:[^<>()]|<[^<>()]*>)*>)*";

    /// <summary>
    /// Today's sites: the file, the member, the method each call is made in, and why the path may ensure storage.
    /// </summary>
    private static readonly AllowedSite[] AllowedSites =
    [
        new("src/MartenStudio/Services/Documents/DocumentWriteService.cs", "SaveChangesAsync",
            ["SaveCoreAsync", "DeleteCoreAsync", "UndeleteCoreAsync", "BulkDeleteCoreAsync"],
            "the document writes - save, delete, undo delete, bulk delete - each behind its capability and audited"),
        new("src/MartenStudio/Services/Events/EventDataService.cs", "SaveChangesAsync",
            ["ArchiveStreamAsync", "DiscardDeadLetterAsync"],
            "ArchiveStreamAsync (ArchiveStreams) and DiscardDeadLetterAsync (ManageDeadLetters), audited"),
        new("src/MartenStudio/Services/Events/EventDataService.cs", "Query",
            ["DiscardDeadLetterAsync", "DiscardDeadLetterAsync"],
            "DiscardDeadLetterAsync: the row it deletes, and the miss path's out-of-scope check - a ManageDeadLetters write"),
        new("src/MartenStudio/Services/Events/EventDataService.cs", "FirstOrDefaultAsync",
            ["DiscardDeadLetterAsync"],
            "DiscardDeadLetterAsync: the row it deletes, read in the write's own session"),
        new("src/MartenStudio/Services/Events/EventDataService.cs", "AnyAsync",
            ["DiscardDeadLetterAsync"],
            "DiscardDeadLetterAsync: whether a missed id belongs to another tenant, so the refusal is a refusal"),
        new("src/MartenStudio/Services/Events/EventDataService.cs", "MarkEventsAsSkipped",
            ["SkipEventAsync"],
            "SkipEventAsync (ManageDeadLetters), audited; NoSchemaBuildingCallTests allows it too. Its 9.51 tenant overload "
            + "is reached by reflection from the same write (see the shapes neither layer sees)"),
        new("src/MartenStudio/Services/Events/StreamCompactionInvoker.cs", "CompactStreamAsync",
            ["CompactAndSaveAsync", "CompactAndSaveAsync"],
            "the compaction (CompactStreams), Guid and string ids, audited by EventDataService.CompactStreamAsync"),
        new("src/MartenStudio/Services/Events/StreamCompactionInvoker.cs", "SaveChangesAsync",
            ["CompactAndSaveAsync"],
            "the compaction's own save"),
        new("src/MartenStudio/Services/Projections/ProjectionDataService.cs", "AdvanceHighWaterMarkToLatestAsync",
            ["AdvanceHighWaterMarkAsync", "AdvanceHighWaterMarkAsync"],
            "AdvanceHighWaterMarkAsync (CorrectProgression), through AdvancedAsync's capability check, reach authorization and "
            + "audit: from Marten 9.52 the advance ensures the event store's storage first, which the dialog and the button say"),
        new("src/MartenStudio/Services/Query/LinqQueryComposer.cs", "Query",
            ["ComposeCore"],
            "F7: the LINQ source the visitor's predicate is composed onto - built by MartenLinqCommand without "
            + "EnsureStorageExists and run by the studio, never by Marten"),
    ];

    /// <summary>
    /// The calls the studio makes on JasperFx's <c>IEventStore</c>, as <c>"Type: Member"</c> with what each needs. None:
    /// the explorer's reads ensure the event store, and what it reports the studio reads with its own SQL.
    /// </summary>
    private static readonly (string Call, string Needs)[] AllowedEventStoreCalls = [];

    /// <summary>
    /// The calls the studio makes on JasperFx's document-store diagnostics - <c>IDocumentStoreDiagnostics</c> (JasperFx
    /// 2.74 and later) and <c>IDocumentStoreDiagnosticsWriter</c> (later) - as <c>"Type: Member"</c> with what each needs.
    /// None: Marten implements them for CritterWatch, and its implementations reach LINQ, <c>LoadAsync</c> or
    /// <c>FetchStreamAsync</c> (<c>DocumentStore.DocumentDiagnostics*.cs</c> at <c>V9.54.1</c>), each of which ensures
    /// storage, where the studio has its own reads of the same things.
    /// </summary>
    private static readonly (string Call, string Needs)[] AllowedDocumentDiagnosticsCalls = [];

    [Fact]
    public void Every_storage_ensuring_call_in_src_is_one_of_todays_sites()
    {
        Dictionary<(string File, string Member), int> found = SourceCalls();

        string[] offenders =
        [
            .. found
                .Where(x => x.Value > AllowedCount(x.Key))
                .Select(x => $"{x.Key.File}: {x.Key.Member} x{x.Value} (allowed {AllowedCount(x.Key)})")
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "hard rule 14: each of these Marten members runs EnsureStorageExistsAsync, which creates or migrates storage "
            + "under the database's AutoCreate the first time the process asks. A read reads with the studio's own SQL "
            + "(Internal/Sql); a write behind a capability goes on AllowedSites with what it needs. Offenders: "
            + string.Join(", ", offenders));
    }

    /// <summary>
    /// The reflected call neither layer above sees - Marten 9.51's tenant overload of <c>MarkEventsAsSkipped</c>, which
    /// ensures the event storage - is reached through exactly one studio method, and that method is called from exactly
    /// one place: <c>SkipEventAsync</c>, the capability-gated, audited write the listed tenant-less call is made in.
    /// </summary>
    [Fact]
    public void The_reflected_tenant_skip_is_reached_only_from_SkipEventAsync()
    {
        MethodInfo helper = typeof(MartenStudio.Services.Events.EventDataService).GetMethod(
            nameof(MartenStudio.Services.Events.EventDataService.MarkTenantEventAsSkippedAsync),
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!;

        helper.Should().NotBeNull("the one method that makes the reflected call");

        IlScan.Result scan = IlScan.Scan(typeof(MartenStudioOptions).Assembly, target => target == helper);

        scan.Unresolved.Should().BeEmpty();
        scan.Calls.Should().ContainSingle("only EventDataService calls it").Which.Key.Should().Be(
            (typeof(MartenStudio.Services.Events.EventDataService).FullName!, helper.Name));
        scan.Calls.Single().Value.Should().Equal(
            [nameof(MartenStudio.Services.Events.EventDataService.SkipEventAsync)],
            "a second caller would be a second path into a storage-ensuring Marten write that the allow-list above cannot see");
    }

    /// <summary>The allow-list is a statement about code that exists, not a permanent exemption.</summary>
    [Fact]
    public void Every_allowed_site_is_still_there_so_the_list_cannot_go_stale()
    {
        Dictionary<(string File, string Member), int> found = SourceCalls();

        found.Should().NotBeEmpty("today's writes are calls of exactly these members, so scanning found nothing for the wrong reason");

        string[] stale =
        [
            .. AllowedSites
                .Where(x => found.GetValueOrDefault((x.File, x.Member)) != x.Count)
                .Select(x => $"{x.File}: {x.Member} allowed x{x.Count}, found x{found.GetValueOrDefault((x.File, x.Member))}")
                .Order(StringComparer.Ordinal),
        ];

        stale.Should().BeEmpty("an entry that excuses calls which are no longer there would excuse whatever is written there next - prune it: "
            + string.Join(", ", stale));

        AllowedSites.Should().OnlyContain(static x => !string.IsNullOrWhiteSpace(x.Needs), "every site says why it may ensure storage");
        AllowedSites.Should().OnlyContain(static x => x.Methods.Length > 0, "every site names the method its calls are made in");
        AllowedSites.Select(static x => (x.File, x.Member)).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// The compiled layer: the IL of every method in <c>MartenStudio.dll</c>, by the type each allowed file declares and
    /// by the method each call is made in, agrees with the list exactly - so a call the source layer cannot see is a
    /// failure here, and so is an allowed call that moved into another method.
    /// </summary>
    [Fact]
    public void The_compiled_assembly_makes_exactly_the_allowed_calls_in_the_allowed_methods()
    {
        IlScan.Result scan = IlScan.Scan(typeof(MartenStudioOptions).Assembly, IlScan.EnsuresStorage);

        scan.Unresolved.Should().BeEmpty("a call whose target the scan cannot resolve is a call it cannot judge, so it must not be skipped");
        scan.Calls.Should().NotBeEmpty("the allowed writes are compiled calls of these members, so the scan read nothing if it found none");

        Dictionary<(string Type, string Member), List<string>> allowed = AllowedSites
            .GroupBy(static x => (TypeOf(x.File), x.Member))
            .ToDictionary(static x => x.Key, static x => x.SelectMany(static site => site.Methods).ToList());

        foreach (string type in allowed.Keys.Select(static x => x.Type).Distinct())
        {
            typeof(MartenStudioOptions).Assembly.GetType(type).Should().NotBeNull(
                "an allowed file has to declare the type its name says - {0} - for the compiled count to be its count", type);
        }

        string[] mismatched = Mismatches(scan.Calls, allowed);

        mismatched.Should().BeEmpty(
            "the IL sees every call into Marten whatever its spelling - a non-generic QueryAsync, an event LoadAsync(id), a "
            + "method group, a lambda - and the method each is made in, and both have to agree with AllowedSites: "
            + string.Join("; ", mismatched));
    }

    /// <summary>
    /// JasperFx's <c>IEventStore</c>, fenced as a whole: its explorer's reads ensure the event store, and the member list
    /// above is read rather than proven complete, so a new explorer read cannot slip in under a name nobody listed.
    /// </summary>
    [Fact]
    public void The_studio_calls_nothing_on_JasperFxs_event_store_unless_it_is_allowed()
    {
        IlScan.Result scan = IlScan.Scan(typeof(MartenStudioOptions).Assembly, IlScan.IsOnTheEventStore);

        scan.Unresolved.Should().BeEmpty();

        string[] calls = [.. scan.Calls.Keys.Select(static x => x.Type + ": " + x.Member).Distinct().Order(StringComparer.Ordinal)];
        string[] allowed = [.. AllowedEventStoreCalls.Select(static x => x.Call)];

        calls.Except(allowed, StringComparer.Ordinal).Should().BeEmpty(
            "JasperFx's IEventStore explorer reads (GetRecentStreamsAsync, ReadStreamAsync, GetStreamMetadataAsync, ...) each "
            + "ensure the event store first; read what they report with the studio's own SQL, or allow the call with what it needs");
        allowed.Except(calls, StringComparer.Ordinal).Should().BeEmpty("an allowed call that is no longer made is a stale entry - prune it");
    }

    /// <summary>
    /// JasperFx's document-store diagnostics, fenced as whole interfaces like <c>IEventStore</c>: Marten 9.52-9.54 grew them
    /// (explain, aggregate a document from its stream, undelete, preview a write), every implementation reaches a
    /// storage-ensuring read, and Marten implements them explicitly, so a call is a call on the interface.
    /// </summary>
    [Fact]
    public void The_studio_calls_nothing_on_JasperFxs_document_store_diagnostics_unless_it_is_allowed()
    {
        IlScan.DocumentDiagnosticsInterfaces.Should().NotBeEmpty(
            "IDocumentStoreDiagnostics is in JasperFx from 2.74.0, the floor's, so the fence has something to fence");

        IlScan.Result scan = IlScan.Scan(typeof(MartenStudioOptions).Assembly, IlScan.IsOnTheDocumentDiagnostics);

        scan.Unresolved.Should().BeEmpty();

        string[] calls = [.. scan.Calls.Keys.Select(static x => x.Type + ": " + x.Member).Distinct().Order(StringComparer.Ordinal)];
        string[] allowed = [.. AllowedDocumentDiagnosticsCalls.Select(static x => x.Call)];

        calls.Except(allowed, StringComparer.Ordinal).Should().BeEmpty(
            "JasperFx's IDocumentStoreDiagnostics and IDocumentStoreDiagnosticsWriter, as Marten implements them, reach LINQ, "
            + "LoadAsync or FetchStreamAsync, each of which ensures storage; read what they report with the studio's own SQL, "
            + "or allow the call with what it needs");
        allowed.Except(calls, StringComparer.Ordinal).Should().BeEmpty("an allowed call that is no longer made is a stale entry - prune it");
    }

    /// <summary>
    /// The diagnostics fence's anti-vacuity: a call on the interface, and a method group of one, are seen; and the
    /// writer's members are fenced too wherever this run's JasperFx has the writer.
    /// </summary>
    [Fact]
    public void A_call_on_JasperFxs_document_store_diagnostics_is_seen_by_the_whole_interface_fence()
    {
        IlScan.Result scan = IlScan.Scan(typeof(StorageEnsuringCallTests).Assembly, IlScan.IsOnTheDocumentDiagnostics);

        scan.Unresolved.Should().BeEmpty();
        Mine(scan).Should().ContainKey("DocumentTypesAsync")
            .WhoseValue.Should().Equal([nameof(Offenders.Diagnostics), nameof(Offenders.DiagnosticsMethodGroup)]);

        Type? writer = typeof(JasperFx.Documents.IDocumentStoreDiagnostics).Assembly
            .GetType("JasperFx.Documents.IDocumentStoreDiagnosticsWriter", throwOnError: false);

        if (writer is not null)
        {
            IlScan.DocumentDiagnosticsInterfaces.Should().Contain(writer);
            writer.GetMethods().Should().NotBeEmpty().And.OnlyContain(x => IlScan.IsOnTheDocumentDiagnostics(x));
        }
    }

    /// <summary>The source layer's anti-vacuity: it finds a real call however it is spelled, and ignores prose and namesakes.</summary>
    [Theory]
    [InlineData("var rows = await session.Query<Order>().Where(x => x.Total > 0).ToListAsync(token);", "Query")]
    [InlineData("var rows = await queryable\n    .ToListAsync(token);", "ToListAsync")]
    [InlineData("var n = await queryable.CountAsync(token);", "CountAsync")]
    [InlineData("var hit = await queryable.FirstOrDefaultAsync(x => x.Id == id, token);", "FirstOrDefaultAsync")]
    [InlineData("var any = await session.Query<DeadLetterEvent>().AnyAsync(x => x.Id == id, token);", "Query")]
    [InlineData("NpgsqlCommand sql = queryable.ToCommand(FetchType.FetchMany);", "ToCommand")]
    [InlineData("var sql = martenQueryable.ToPreviewCommand(FetchType.FetchMany);", "ToPreviewCommand")]
    [InlineData("var plan = await queryable.ExplainAsync(token: token);", "ExplainAsync")]
    [InlineData("var doc = await session.LoadAsync<Order>(id, token);", "LoadAsync")]
    [InlineData("var doc = await session?.LoadAsync<Dictionary<string, List<int>>>(id);", "LoadAsync")]
    [InlineData("var docs = await session.LoadManyAsync<Order>(token, ids);", "LoadManyAsync")]
    [InlineData("var rows = await session.QueryAsync<Order>(\"where data ->> 'x' = ?\", token, value);", "QueryAsync")]
    [InlineData("var state = await session.Events.FetchStreamStateAsync(id, token);", "FetchStreamStateAsync")]
    [InlineData("var events = await session.Events.FetchStreamAsync(id, token: token);", "FetchStreamAsync")]
    [InlineData("var agg = await session.Events.AggregateStreamAsync<Order>(id, version, token: token);", "AggregateStreamAsync")]
    [InlineData("await session.Events.CompactStreamAsync<Order>(id, configure);", "CompactStreamAsync")]
    [InlineData("var letters = await database.QueryDeadLetterEventsAsync(shard, token);", "QueryDeadLetterEventsAsync")]
    [InlineData("await resolved.Database.EnsureStorageExistsAsync(typeof(Order), token);", "EnsureStorageExistsAsync")]
    [InlineData("await session.SaveChangesAsync(token).ConfigureAwait(false);", "SaveChangesAsync")]
    [InlineData("var raw = session.Events.QueryAllRawEvents();", "QueryAllRawEvents")]
    [InlineData("var page = await queryable.ToPagedListAsync(1, 50, token);", "ToPagedListAsync")]
    [InlineData("string? json = await session.Json.FindByIdAsync<Order>(id, token);", "FindByIdAsync")]
    [InlineData("bool found = await session.Json.StreamById<Order>(id, stream, token);", "StreamById")]
    [InlineData("await foreach (var row in session.AdvancedSql.StreamAsync<Order>(sql, token)) { }", "StreamAsync")]
    [InlineData("await session.Events.AppendOptimistic(id, token, e);", "AppendOptimistic")]
    [InlineData("await session.Events.AppendExclusive(key, e);", "AppendExclusive")]
    [InlineData("var meta = await ((IEventStore) store).GetStreamMetadataAsync(id, token);", "GetStreamMetadataAsync")]
    [InlineData("var shards = await explorer.GetProjectionStatusesAsync(token);", "GetProjectionStatusesAsync")]
    [InlineData("var at = await explorer.RehydrateAtVersionAsync<Order>(id, 3, token);", "RehydrateAtVersionAsync")]
    [InlineData("var batch = await store.StartProjectionBatchAsync(range, database, mode, options, token);", "StartProjectionBatchAsync")]
    [InlineData("var storage = await session.FetchProjectionStorageAsync<Order, Guid>(tenant, token);", "FetchProjectionStorageAsync")]
    [InlineData("await HealthMutes.LoadAsync(JsRuntime, cancellation.Token);", null)]
    [InlineData("await definitions.LoadAsync(Data, State.ActiveScope, reference);", null)]
    [InlineData("var rows = await context.Sql.QueryAsync(query, Map, cancellationToken);", null)]
    [InlineData("string outcome = await Events.CompactStreamAsync(scope, Id, typed, plan.Version, token);", null)]
    [InlineData("var recent = await Events.GetRecentStreamsAsync(scope, 20, token);", null)]
    [InlineData("StreamState state = await ReadStreamAsync(scope, streamId, ConnectionUsage.Read, token);", null)]
    [InlineData("DeadLetterPage page = await Events.ListDeadLettersAsync(scope, query, token);", null)]
    [InlineData("var count = await CountDeadLettersAsync(scope, token);", null)]
    [InlineData("/// <c>session.Query&lt;DeadLetterEvent&gt;().ToListAsync()</c> ensures storage first.", null)]
    [InlineData("// never session.Events.FetchStreamStateAsync(id): it migrates", null)]
    [InlineData("var why = \"session.LoadAsync<T>(id) would migrate\";", null)]
    [InlineData("@* the list was session.Query<DeadLetterEvent>().ToListAsync() *@", null)]
    [InlineData("private async Task<int> SaveChangesAsync(CancellationToken token)", null)]
    public void The_scanner_finds_a_real_call_and_ignores_prose_and_studio_namesakes(string source, string? member)
    {
        Match match = StorageEnsuringCall.Match(SourceScanner.StripCommentsAndStrings(source));

        if (member is null)
        {
            match.Success.Should().BeFalse("'{0}' calls nothing of Marten's", source);
        }
        else
        {
            match.Success.Should().BeTrue("'{0}' calls {1}", source, member);
            match.Groups["member"].Value.Should().Be(member);
        }
    }

    /// <summary>
    /// The compiled layer's anti-vacuity: every spelling in <see cref="Offenders" />, compiled into this test assembly, is
    /// found in the method it is written in - including the shapes the source layer cannot see - and a namesake that is not
    /// Marten's is not.
    /// </summary>
    [Fact]
    public void The_compiled_scan_finds_every_spelling_in_its_method_and_ignores_a_namesake()
    {
        IlScan.Result scan = IlScan.Scan(typeof(StorageEnsuringCallTests).Assembly, IlScan.EnsuresStorage);

        scan.Unresolved.Should().BeEmpty();

        Dictionary<string, List<string>> mine = Mine(scan);

        mine.Keys.Should().BeEquivalentTo(
            ["Query", "ToListAsync", "LoadAsync", "QueryAsync", "FetchStreamStateAsync", "ToCommand", "GetRecentStreamsAsync"]);

        mine["Query"].Should().Equal(nameof(Offenders.Linq));
        mine["ToListAsync"].Should().Equal(nameof(Offenders.Linq));
        mine["LoadAsync"].Should().Equal([nameof(Offenders.Load), nameof(Offenders.MethodGroup)], "a method group is a load too");
        mine["QueryAsync"].Should().Equal([nameof(Offenders.UserSql)], "the namesake's QueryAsync<T> is not Marten's and does not count");
        mine["FetchStreamStateAsync"].Should().Equal(
            [nameof(Offenders.Lambda)], "a lambda's body is a method of a compiler-generated type, read back to the member it is written in");
        mine["ToCommand"].Should().Equal(nameof(Offenders.Built));
        mine["GetRecentStreamsAsync"].Should().Equal(
            [nameof(Offenders.Explorer)], "a compiled-only name is seen on JasperFx's IEventStore, whatever the studio calls its own");
    }

    /// <summary>
    /// The method assertion's anti-vacuity: an allow-list that puts <see cref="Offenders.Lambda" />'s call in another method
    /// fails, though the count it names is right - which is what an allowed call moved into a read path looks like.
    /// </summary>
    [Fact]
    public void An_allowed_call_moved_into_another_method_fails_although_its_count_is_unchanged()
    {
        IlScan.Result scan = IlScan.Scan(typeof(StorageEnsuringCallTests).Assembly, IlScan.EnsuresStorage);
        string self = typeof(StorageEnsuringCallTests).FullName!;

        Dictionary<(string Type, string Member), List<string>> compiled = new()
        {
            [(self, "FetchStreamStateAsync")] = scan.Calls[(self, "FetchStreamStateAsync")],
        };

        Mismatches(compiled, new() { [(self, "FetchStreamStateAsync")] = [nameof(Offenders.Lambda)] })
            .Should().BeEmpty("the allow-list says where the call is, and it is there");

        Mismatches(compiled, new() { [(self, "FetchStreamStateAsync")] = [nameof(Offenders.Load)] })
            .Should().ContainSingle("one call, the right count, the wrong method")
            .Which.Should().Contain(nameof(Offenders.Lambda)).And.Contain(nameof(Offenders.Load));
    }

    /// <summary>The event-store fence's anti-vacuity: a call on JasperFx's <c>IEventStore</c> is seen as one.</summary>
    [Fact]
    public void A_call_on_JasperFxs_event_store_is_seen_by_the_whole_interface_fence()
    {
        IlScan.Result scan = IlScan.Scan(typeof(StorageEnsuringCallTests).Assembly, IlScan.IsOnTheEventStore);

        Mine(scan).Should().ContainKey("GetRecentStreamsAsync")
            .WhoseValue.Should().Equal(nameof(Offenders.Explorer));
    }

    /// <summary>
    /// The shapes neither layer sees, written down so they are a decision rather than a surprise - and so that a scanner
    /// taught to see one of them fails here, and this list is updated.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><description>A call made through <b>reflection</b> names the member as a string - <c>DocumentWriteService</c>
    /// builds its typed <c>Delete</c> and <c>UndoDeleteWhere</c> calls that way, and they queue operations that only its
    /// <c>SaveChangesAsync</c> (on the list) runs. So does <c>EventDataService.MarkTenantEventAsSkippedAsync</c>: Marten
    /// 9.51's tenant overload of <c>MarkEventsAsSkipped</c>, which ensures the event storage like the tenant-less one on
    /// the list, and is reached by reflection because the floor has no such member to compile against. It is made only
    /// from <c>SkipEventAsync</c> - the same capability-gated, audited write as the listed site, which
    /// <see cref="The_reflected_tenant_skip_is_reached_only_from_SkipEventAsync" /> pins - and its declaration is
    /// looked up by name in <c>RuntimeFeature.TenantSkipMethod</c>, a string this scanner blanks.</description></item>
    /// <item><description>A call inside an <b>interpolation hole</b> is blanked with its string by the source layer; the
    /// compiled layer still sees it.</description></item>
    /// <item><description>A member named in an <b>expression tree</b> is an <c>ldtoken</c>, not a call, and is not counted:
    /// Marten translates such a tree, it does not run it.</description></item>
    /// <item><description>Type arguments nested <b>three</b> deep are beyond the source pattern; the compiled layer sees the
    /// call.</description></item>
    /// <item><description><b>What a host's own code does</b> with the session the studio hands it - an <c>Apply</c> method
    /// that loads reference data during a replay - is the host's: it runs in Marten's aggregator, not in
    /// <c>src/</c>.</description></item>
    /// </list>
    /// </remarks>
    [Theory]
    [InlineData("var m = typeof(IQuerySession).GetMethod(\"LoadAsync\")!.MakeGenericMethod(type);", "reflection")]
    [InlineData("var text = $\"{(await queryable.ToListAsync(token)).Count} rows\";", "an interpolation hole")]
    [InlineData("var doc = await session.LoadAsync<A<B<C<D>>>>(id);", "type arguments three deep")]
    public void What_the_source_layer_cannot_see_is_written_down(string source, string shape)
    {
        StorageEnsuringCall.IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().BeFalse(
            "{0} is a known limit of a call-site scan; if the scanner now sees it, move the row to the theory above and "
            + "the bullet out of this remark",
            shape);
    }

    /// <summary>
    /// Every key on which the compiled calls and the allow-list disagree - in how many calls, or in which methods they are
    /// made in, compared as multisets.
    /// </summary>
    private static string[] Mismatches(
        Dictionary<(string Type, string Member), List<string>> compiled,
        Dictionary<(string Type, string Member), List<string>> allowed) =>
        [
            .. compiled.Keys.Union(allowed.Keys)
                .Where(key => !Sorted(compiled.GetValueOrDefault(key)).SequenceEqual(Sorted(allowed.GetValueOrDefault(key)), StringComparer.Ordinal))
                .Select(key =>
                    $"{key.Type}: {key.Member} compiled in [{string.Join(", ", Sorted(compiled.GetValueOrDefault(key)))}], "
                    + $"allowed in [{string.Join(", ", Sorted(allowed.GetValueOrDefault(key)))}]")
                .Order(StringComparer.Ordinal),
        ];

    private static List<string> Sorted(List<string>? methods) => [.. (methods ?? []).Order(StringComparer.Ordinal)];

    /// <summary>This class's own calls - which are <see cref="Offenders" />', since nothing else in it calls Marten.</summary>
    private static Dictionary<string, List<string>> Mine(IlScan.Result scan)
    {
        string self = typeof(StorageEnsuringCallTests).FullName!;

        return scan.Calls
            .Where(x => x.Key.Type == self)
            .ToDictionary(static x => x.Key.Member, static x => Sorted(x.Value), StringComparer.Ordinal);
    }

    /// <summary>The namespace-and-folder convention: <c>src/MartenStudio/Services/Events/EventDataService.cs</c> declares
    /// <c>MartenStudio.Services.Events.EventDataService</c>, and so does <c>EventDataService.Compaction.cs</c>.</summary>
    private static string TypeOf(string file)
    {
        string path = file["src/".Length..];
        string directory = Path.GetDirectoryName(path)!.Replace('\\', '/');
        string name = Path.GetFileName(path);

        return directory.Replace('/', '.') + "." + name[..name.IndexOf('.', StringComparison.Ordinal)];
    }

    private static int AllowedCount((string File, string Member) site) =>
        AllowedSites.FirstOrDefault(x =>
            string.Equals(x.File, site.File, StringComparison.Ordinal)
            && string.Equals(x.Member, site.Member, StringComparison.Ordinal))?.Count ?? 0;

    /// <summary>Every source call the shipped tree makes, counted by file and member.</summary>
    private static Dictionary<(string File, string Member), int> SourceCalls()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        Dictionary<(string File, string Member), int> found = new();

        foreach (string path in files)
        {
            string relative = SourceScanner.Relative(path);
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(path));

            foreach (Match match in StorageEnsuringCall.Matches(code))
            {
                (string File, string Member) key = (relative, match.Groups["member"].Value);
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }

        return found;
    }

    /// <summary>One allowed site.</summary>
    /// <param name="File">The file, relative to the repository root, with forward slashes.</param>
    /// <param name="Member">The Marten member called.</param>
    /// <param name="Methods">The method each call is made in, one entry per call - a lambda's is the member it is written in.</param>
    /// <param name="Needs">Why this path may ensure storage: the capability it is behind, and what it writes.</param>
    private sealed record AllowedSite(string File, string Member, string[] Methods, string Needs)
    {
        /// <summary>How many calls of the member the file makes.</summary>
        public int Count => Methods.Length;
    }

    /// <summary>Every spelling the compiled layer has to see, compiled and never run.</summary>
    private static class Offenders
    {
        public static Task<IReadOnlyList<Probe>> Linq(IQuerySession session, CancellationToken token) =>
            session.Query<Probe>().ToListAsync(token);

        public static Task<Probe?> Load(IQuerySession session, Guid id) => session.LoadAsync<Probe>(id);

        public static Func<Guid, CancellationToken, Task<Probe?>> MethodGroup(IQuerySession session) => session.LoadAsync<Probe>;

        public static Task<IReadOnlyList<object>> UserSql(IQuerySession session, CancellationToken token) =>
            session.QueryAsync(typeof(Probe), "where 1 = 1", token);

        public static Func<Task<bool>> Lambda(IQuerySession session, string key) =>
            async () => await session.Events.FetchStreamStateAsync(key) is not null;

        public static NpgsqlCommand Built(IQueryable<Probe> queryable) => queryable.ToCommand();

        public static async Task<int> Explorer(IEventStore store, CancellationToken token) =>
            (await store.GetRecentStreamsAsync(10, token)).Count;

        public static Task<int> NotAnOffender(Namesake namesake) => namesake.QueryAsync<Probe>("select 1");

        public static async Task<int> Diagnostics(JasperFx.Documents.IDocumentStoreDiagnostics diagnostics, CancellationToken token) =>
            (await diagnostics.DocumentTypesAsync(token)).Count;

        public static Func<CancellationToken, Task<IReadOnlyList<JasperFx.Documents.DocumentTypeRef>>> DiagnosticsMethodGroup(
            JasperFx.Documents.IDocumentStoreDiagnostics diagnostics) => diagnostics.DocumentTypesAsync;
    }

    /// <summary>A document type for the offenders to name.</summary>
    private sealed class Probe
    {
        public Guid Id { get; set; }
    }

    /// <summary>Not Marten: a method that shares a name with one of Marten's and must not count.</summary>
    private sealed class Namesake
    {
        private readonly int offset = 1;

        public Task<int> QueryAsync<T>(string sql) => Task.FromResult(sql.Length + typeof(T).Name.Length + offset);
    }

    /// <summary>Reads method bodies for calls into Marten, JasperFx and Weasel.</summary>
    private static class IlScan
    {
        private const BindingFlags Declared =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        private static readonly HashSet<string> Names =
            new([.. Members, .. GenericOnlyMembers, .. CompiledOnlyMembers], StringComparer.Ordinal);

        private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Select(static field => (OpCode) field.GetValue(null)!)
            .GroupBy(static code => code.Value)
            .ToDictionary(static group => group.Key, static group => group.First());

        /// <summary>A target that ensures storage: one of the listed names, declared in Marten, JasperFx or Weasel.</summary>
        public static bool EnsuresStorage(MethodBase target) =>
            Names.Contains(target.Name)
            && MartenAssemblies.Contains(target.DeclaringType?.Assembly.GetName().Name ?? string.Empty);

        /// <summary>
        /// JasperFx's document-store diagnostics interfaces this run's JasperFx has: <c>IDocumentStoreDiagnostics</c> from
        /// 2.74.0, <c>IDocumentStoreDiagnosticsWriter</c> from later - looked up by name, so the fence compiles at both ends.
        /// </summary>
        public static readonly IReadOnlyList<Type> DocumentDiagnosticsInterfaces =
        [
            .. new[] { "JasperFx.Documents.IDocumentStoreDiagnostics", "JasperFx.Documents.IDocumentStoreDiagnosticsWriter" }
                .Select(static name => typeof(JasperFx.AutoCreate).Assembly.GetType(name, throwOnError: false))
                .OfType<Type>(),
        ];

        /// <summary>
        /// Every method that implements one of <see cref="DocumentDiagnosticsInterfaces" /> in Marten, JasperFx or Weasel, by
        /// module and metadata token - so an implicit implementation called on the concrete type is fenced as well as the
        /// interface member.
        /// </summary>
        private static readonly Lazy<HashSet<(Module Module, int Token)>> DocumentDiagnosticsImplementations = new(static () =>
        {
            HashSet<(Module, int)> methods = [];

            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies().Where(static x => MartenAssemblies.Contains(x.GetName().Name ?? string.Empty)))
            {
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException exception)
                {
                    types = [.. exception.Types.OfType<Type>()];
                }

                foreach (Type type in types.Where(static x => x is { IsClass: true, IsGenericTypeDefinition: false }))
                {
                    foreach (Type @interface in DocumentDiagnosticsInterfaces.Where(x => x.IsAssignableFrom(type)))
                    {
                        foreach (MethodInfo target in type.GetInterfaceMap(@interface).TargetMethods)
                        {
                            methods.Add((target.Module, target.MetadataToken));
                        }
                    }
                }
            }

            return methods;
        });

        /// <summary>
        /// A target declared on JasperFx's document-store diagnostics interfaces, or a method that implements one of their
        /// members - any member at all.
        /// </summary>
        public static bool IsOnTheDocumentDiagnostics(MethodBase target) =>
            (target.DeclaringType is { } owner && DocumentDiagnosticsInterfaces.Contains(owner))
            || DocumentDiagnosticsImplementations.Value.Contains((target.Module, target.MetadataToken));

        /// <summary>A target declared on JasperFx's <c>IEventStore</c>, or its generic form - any member at all.</summary>
        public static bool IsOnTheEventStore(MethodBase target) =>
            target.DeclaringType is { } owner
            && (owner == typeof(IEventStore) || (owner.IsGenericType && owner.GetGenericTypeDefinition() == typeof(IEventStore<,>)));

        /// <summary>
        /// Every call whose target <paramref name="counts" />, keyed by the outermost type it is made in and the member it
        /// calls, with the source member each one is made from - and every method token that could not be resolved.
        /// </summary>
        public static Result Scan(Assembly assembly, Func<MethodBase, bool> counts)
        {
            Dictionary<(string Type, string Member), List<string>> calls = new();
            List<string> unresolved = [];

            foreach (Type type in assembly.GetTypes())
            {
                foreach (MethodBase method in type.GetMethods(Declared).Cast<MethodBase>().Concat(type.GetConstructors(Declared)))
                {
                    if (method.GetMethodBody()?.GetILAsByteArray() is not { } il)
                    {
                        continue;
                    }

                    foreach (MethodBase target in Targets(method, il, unresolved))
                    {
                        if (!counts(target))
                        {
                            continue;
                        }

                        (string Type, string Member) key = (Outermost(type).FullName!, target.Name);

                        if (!calls.TryGetValue(key, out List<string>? sources))
                        {
                            calls[key] = sources = [];
                        }

                        sources.Add(SourceMember(method));
                    }
                }
            }

            return new Result(calls, unresolved);
        }

        private static IEnumerable<MethodBase> Targets(MethodBase method, byte[] il, List<string> unresolved)
        {
            int position = 0;

            while (position < il.Length)
            {
                short value = il[position] == 0xFE && position + 1 < il.Length
                    ? unchecked((short) (0xFE00 | il[++position]))
                    : il[position];
                position++;

                OpCode code = ByValue[value];

                if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Ldftn || code == OpCodes.Ldvirtftn)
                {
                    int token = BitConverter.ToInt32(il, position);

                    if (Resolve(method, token) is { } target)
                    {
                        yield return target;
                    }
                    else
                    {
                        unresolved.Add($"{method.DeclaringType?.FullName}.{method.Name}: {code.Name} 0x{token:x8}");
                    }
                }

                position += code.OperandType switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineI8 or OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                    _ => 4,
                };
            }
        }

        /// <summary>The method a call's token names, or <see langword="null" /> when this context cannot resolve it.</summary>
        private static MethodBase? Resolve(MethodBase method, int token)
        {
            try
            {
                return method.Module.ResolveMethod(
                    token,
                    method.DeclaringType is { IsGenericType: true } owner ? owner.GetGenericArguments() : null,
                    method.IsGenericMethod ? method.GetGenericArguments() : null);
            }
            catch (ArgumentException)
            {
                // Counted by the caller as unresolved: a call the scan cannot judge is a failure, never a skip.
                return null;
            }
        }

        private static Type Outermost(Type type)
        {
            while (type.DeclaringType is { } outer)
            {
                type = outer;
            }

            return type;
        }

        /// <summary>
        /// The member a compiler-generated method was written in: <c>&lt;DiscardDeadLetterAsync&gt;d__50.MoveNext</c>,
        /// <c>&lt;&lt;DiscardDeadLetterAsync&gt;b__0&gt;d.MoveNext</c> and <c>&lt;Lambda&gt;b__4_0</c> all read back to the name
        /// between the first angle brackets.
        /// </summary>
        private static string SourceMember(MethodBase method)
        {
            if (!method.Name.StartsWith('<') && method.DeclaringType?.Name.StartsWith('<') != true)
            {
                return method.Name;
            }

            foreach (string name in Spellings(method))
            {
                string trimmed = name.TrimStart('<');
                int end = trimmed.IndexOf('>', StringComparison.Ordinal);

                if (name.StartsWith('<') && end > 0)
                {
                    return trimmed[..end];
                }
            }

            return method.Name;

            static IEnumerable<string> Spellings(MethodBase method)
            {
                yield return method.Name;

                for (Type? type = method.DeclaringType; type is not null; type = type.DeclaringType)
                {
                    yield return type.Name;
                }
            }
        }

        /// <summary>What a scan found.</summary>
        /// <param name="Calls">The counted calls, by outermost type and member, with the source member of each.</param>
        /// <param name="Unresolved">Every call token the scan could not resolve - which must be none.</param>
        public sealed record Result(Dictionary<(string Type, string Member), List<string>> Calls, List<string> Unresolved);
    }
}
