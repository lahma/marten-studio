using Marten;
using Marten.Schema;

using MartenStudio.Services;
using MartenStudio.Services.Database;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MartenStudio.Tests.Database;

/// <summary>
/// Acceptance 2: <c>BrowseDatabase</c> against the capability, <c>ReadOnly</c> and the write policy - and
/// the resource the policy is shown, which never carries a tenant.
/// </summary>
/// <remarks>
/// A real studio container over a store that never connects, as <c>CapabilityGatingMatrixTests</c> uses:
/// every refusal asserted here happens before a connection could be opened, so a gate that ran too late
/// would fail on the unreachable host rather than quietly pass.
/// </remarks>
public class DatabaseAccessTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-database-access.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";
    private const string WritePolicy = "write-policy";

    /// <summary>The visitor has a tenant selected; nothing the browser reads is that tenant's.</summary>
    private static readonly StudioScope TenantScope = new("default", string.Empty, "acme");

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// The whole truth table: only capability on, <c>ReadOnly</c> off and the policy's yes let a visitor
    /// through, and each refusal is the one the first closed gate owns.
    /// </summary>
    /// <remarks>
    /// The expected refusal is passed by name: xunit needs a public test method, and
    /// <see cref="DatabaseRefusal" /> is internal.
    /// </remarks>
    [Theory]
    [InlineData(false, false, true, nameof(DatabaseRefusal.CapabilityOff))]
    [InlineData(false, false, false, nameof(DatabaseRefusal.CapabilityOff))]
    [InlineData(false, true, true, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(false, true, false, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, true, true, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, true, false, nameof(DatabaseRefusal.ReadOnly))]
    [InlineData(true, false, false, nameof(DatabaseRefusal.WritePolicy))]
    [InlineData(true, false, true, nameof(DatabaseRefusal.None))]
    public async Task Capability_ReadOnly_and_the_write_policy_decide_in_that_order(
        bool capability,
        bool readOnly,
        bool policyAllows,
        string expectedRefusal)
    {
        DatabaseRefusal expected = Enum.Parse<DatabaseRefusal>(expectedRefusal);

        await using Harness harness = Harness.Create(options =>
        {
            options.Capabilities.BrowseDatabase = capability;
            options.ReadOnly = readOnly;
        });

        harness.Policies.Allow(resource => resource.Capability is null || policyAllows);

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, "Test", "quartz.qrtz_triggers", Token);

        grant.Refusal.Should().Be(expected);
        grant.Allowed.Should().Be(expected == DatabaseRefusal.None);

        if (expected == DatabaseRefusal.None)
        {
            grant.Resolved!.Scope.TenantId.Should().BeNull("the scope is resolved as the database as a whole");
            harness.Ring.GetLatest().Should().BeEmpty("a grant is recorded by whoever then reads");
            return;
        }

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be("Test");
        entry.Target.Should().Be("quartz.qrtz_triggers");

        grant.Reason.Should().Contain(expected switch
        {
            DatabaseRefusal.CapabilityOff => "MartenStudioOptions.Capabilities.BrowseDatabase",
            DatabaseRefusal.ReadOnly => "MartenStudioOptions.ReadOnly",
            _ => "WriteAuthorizationPolicy",
        });

        if (expected is DatabaseRefusal.CapabilityOff or DatabaseRefusal.ReadOnly)
        {
            harness.Policies.Calls.Should().BeEmpty(
                "a capability that is off is answered before anybody is asked who is asking");
        }
    }

    /// <summary>
    /// The resource the policy sees has no tenant, although the visitor's scope has one - and the write
    /// policy is asked with the capability named.
    /// </summary>
    [Fact]
    public async Task The_policy_is_asked_about_the_database_as_a_whole_with_no_tenant()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);

        await harness.Access.RequireBrowseAsync(TenantScope, "Test", "x.y", Token);

        harness.Policies.Calls.Should().NotBeEmpty();
        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.TenantId == null,
            "non-Marten data is not tenant-scoped, so a read of it is a read for every tenant");

        harness.Policies.Calls.Should().Contain(static x =>
            x.Policy == WritePolicy && x.Resource.Capability == nameof(StudioCapability.BrowseDatabase));
        harness.Policies.Calls.Should().Contain(static x => x.Policy == StorePolicy && x.Resource.Capability == null);
    }

    /// <summary>
    /// The page's question asks exactly what the enforcement asks, so a control drawn enabled is one whose
    /// service call passes - and a tenant-restricted handler says no to both, naming the same policy: the
    /// store policy, which is asked first and refuses a resource with no tenant.
    /// </summary>
    [Fact]
    public async Task The_pages_question_and_the_enforcement_agree_for_a_tenant_restricted_visitor()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);

        // A handler that only ever lets this visitor see their own tenant - the README's shape.
        harness.Policies.Allow(static resource => resource.TenantId == "acme");

        (bool enabled, bool? authorized) = await harness.Access.EvaluateAsync(TenantScope, Token);

        enabled.Should().BeTrue();
        authorized.Should().BeFalse();

        DatabasePolicyAnswer answer = await harness.Access.EvaluatePoliciesAsync(TenantScope, Token);
        answer.Refusal.Should().Be(DatabaseRefusal.StorePolicy);

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, "Test", "x.y", Token);

        grant.Refusal.Should().Be(DatabaseRefusal.StorePolicy);
        grant.Reason.Should().Be(DatabaseGate.StorePolicyDenial);
    }

    /// <summary>
    /// F10: the two policies are asked one at a time, and the refusal - its value, its sentence, and the
    /// policy event 9203 names - is the one that said no. Asking only through the resolver named the write
    /// policy for both, which sends whoever reads the log to the wrong setting.
    /// </summary>
    [Theory]
    [InlineData(false, nameof(DatabaseRefusal.StorePolicy), StorePolicy, "StoreAuthorizationPolicy")]
    [InlineData(true, nameof(DatabaseRefusal.WritePolicy), WritePolicy, "WriteAuthorizationPolicy")]
    public async Task A_refusal_names_the_policy_that_said_no(
        bool storePolicyAllows,
        string expectedRefusal,
        string expectedPolicy,
        string expectedSetting)
    {
        var logs = new CapturingLoggerProvider();

        await using Harness harness = Harness.Create(
            static options => options.Capabilities.BrowseDatabase = true,
            services => services.AddSingleton<ILoggerProvider>(logs));

        harness.Policies.Allow(resource => resource.Capability is null ? storePolicyAllows : false);

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, "Test", "quartz.qrtz_triggers", Token);

        grant.Refusal.Should().Be(Enum.Parse<DatabaseRefusal>(expectedRefusal));
        grant.Reason.Should().Contain(expectedSetting);

        CapturedLogEntry refused = logs.Entries.Should().ContainSingle(static x => x.EventId.Id == 9203).Which;
        refused.Message.Should().EndWith("by policy " + expectedPolicy);

        harness.Ring.GetLatest().Should().ContainSingle().Which.TenantId.Should().BeNull();

        DatabasePolicyAnswer page = await harness.Access.EvaluatePoliciesAsync(TenantScope, Token);
        page.Refusal.Should().Be(grant.Refusal, "the page names the same policy the enforcement named");
    }

    /// <summary>
    /// F10: a read the capability refuses is refused before the visitor's tenant-bearing scope is ever
    /// resolved - so tenant discovery, which can query the database, never runs for it. The visitor here has
    /// a tenant the store cannot answer for: had the tenant been resolved first, every call would have come
    /// back "not a tenant" instead of naming the capability.
    /// </summary>
    [Fact]
    public async Task A_read_the_capability_refuses_never_resolves_the_visitors_tenant()
    {
        await using Harness harness = Harness.Create(static _ => { });

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        (await objects.ListAsync(TenantScope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables, "quartz"), Token))
            .Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        (await objects.GetObjectAsync(TenantScope, "quartz", "qrtz_triggers", Token))
            .Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        (await objects.GetDefinitionAsync(TenantScope, new DatabaseObjectRef(DatabaseObjectKind.View, "quartz", "v"), Token))
            .Refusal.Should().Be(DatabaseRefusal.CapabilityOff);
        (await objects.GetSequenceValueAsync(TenantScope, "quartz", "qrtz_seq", Token))
            .Refusal.Should().Be(DatabaseRefusal.CapabilityOff);

        harness.Policies.Calls.Should().OnlyContain(static x => x.Resource.Capability == null,
            "only the store policy for the visitor's own scope is asked before the capability refuses");

        // The store's own structure needs no capability, so there the tenant is resolved - and refused.
        DatabaseObjectDetail own = await objects.GetObjectAsync(TenantScope, "public", "orders", Token);
        own.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        own.Reason.Should().Contain("'acme' is not a tenant");
    }

    /// <summary>
    /// F7: a hidden type Marten learns after the circuit read the declarations is hidden on the very next
    /// call - the remembered classification is checked against every store's count of known document
    /// types, and read again when one has grown.
    /// </summary>
    /// <remarks>
    /// <c>FindOrResolveDocumentType</c> is what a session's first touch of an unregistered type runs
    /// (<c>StorageFeatures.FindMapping</c>); calling it directly teaches Marten the type without opening a
    /// connection to this unreachable host.
    /// </remarks>
    [Fact]
    public async Task A_hidden_type_Marten_learns_after_the_circuit_opened_is_hidden_on_the_next_call()
    {
        await using Harness harness = Harness.Create(
            static options => options.IsDocumentTypeVisible = static type => type != typeof(LateSecret));

        StudioScopeResolver resolver = harness.Resolve<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        DatabaseDeclarations before = harness.Access.ReadDeclarations(resolved);
        before.Succeeded.Should().BeTrue(before.Failure);
        before.Classifier!.HiddenTables.Should().BeEmpty("nothing has touched LateSecret yet");
        before.Classifier.MayHideDocumentTypes.Should().BeTrue("the host set IsDocumentTypeVisible");

        harness.Access.ReadDeclarations(resolved).Should().BeSameAs(before, "nothing changed, so the circuit's read stands");

        // A session's first touch of the type.
        IDocumentType learned = resolved.Store.Options.FindOrResolveDocumentType(typeof(LateSecret));
        string schema = learned.TableName.Schema;
        string table = learned.TableName.Name;

        before.Classifier.ClassifyRelation(schema, table)!.Owner.Should().Be(DatabaseObjectOwner.MartenInfrastructure,
            "this is the stale answer: the table named by its mt_ prefix, not known to be hidden");

        DatabaseDeclarations after = harness.Access.ReadDeclarations(resolved);

        after.Should().NotBeSameAs(before);
        after.Classifier!.IsHiddenTable(schema, table).Should().BeTrue();
        after.Classifier.ClassifyRelation(schema, table).Should().BeNull("a hidden type's table is absent from every list");
    }

    [Fact]
    public async Task With_the_capability_off_the_page_asks_no_policy_at_all()
    {
        await using Harness harness = Harness.Create(static _ => { });

        (bool enabled, bool? authorized) = await harness.Access.EvaluateAsync(TenantScope, Token);

        enabled.Should().BeFalse();
        authorized.Should().BeNull();
        harness.Policies.Calls.Should().BeEmpty();
    }

    /// <summary>
    /// The row gate refuses a schema <c>BrowsableSchemas</c> cannot admit from the options alone - before
    /// the database is asked, which on this unreachable host is the only way it can answer at all.
    /// </summary>
    [Fact]
    public async Task The_row_gate_refuses_a_schema_the_list_cannot_admit_before_touching_the_database()
    {
        await using Harness harness = Harness.Create(static options =>
        {
            options.Capabilities.BrowseDatabase = true;
            options.BrowsableSchemas.Add("quartz");
        });

        DatabaseRowAccessResult result = await harness.Access.RequireRowAccessAsync(TenantScope, "legacy", "orders", cancellationToken: Token);

        result.Allowed.Should().BeFalse();
        result.Refusal.Should().Be(DatabaseRefusal.SchemaNotBrowsable);
        result.Reason.Should().Contain("MartenStudioOptions.BrowsableSchemas");

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Succeeded.Should().BeFalse();
        entry.Action.Should().Be(DatabaseAccess.RowsAction);
        entry.TenantId.Should().BeNull();
        entry.Capability.Should().Be(nameof(StudioCapability.BrowseDatabase));
    }

    /// <summary>
    /// Acceptance 4, fail closed across stores: a registered store that will not build is a store whose
    /// tables would all read as "Other", so the whole classification is refused - and the store is named, with
    /// the reason it gave, only to a visitor that store's store policy passes (SEC-fix F3, AGENTS.md D27).
    /// </summary>
    [Fact]
    public async Task A_registered_store_that_will_not_build_fails_the_classification_closed()
    {
        await using Harness harness = Harness.Create(
            static _ => { },
            static services => services.AddSingleton<IBrokenStore>(
                static _ => throw new InvalidOperationException("its connection string is wrong")));

        StudioScopeResolver resolver = harness.Resolve<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        DatabaseDeclarations declarations = harness.Access.ReadDeclarations(resolved);

        declarations.Succeeded.Should().BeFalse();
        declarations.Classifier.Should().BeNull();
        declarations.Failure.Should().StartWith(DatabaseAccess.UnnamedStore + " could not be built")
            .And.NotContain(nameof(IBrokenStore), "the sentence any visitor may read names no other store");
        declarations.FailedStoreKey.Should().Be(nameof(IBrokenStore));
        declarations.NamedFailure.Should().Contain(nameof(IBrokenStore)).And.Contain("its connection string is wrong");

        // A visitor the broken store's own policy refuses reads "a registered Marten store", on every page the
        // sentence reaches: the browser's overview, an object page, the relationships screen's table notice.
        harness.Policies.Allow(static resource => resource.StoreName != nameof(IBrokenStore));

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();
        var scope = new StudioScope("default", string.Empty, null);

        DatabaseBrowserOverview refused = await objects.GetOverviewAsync(scope, Token);

        refused.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        refused.Schemas.Should().BeEmpty("nothing is classified 'Other' when the classification cannot be completed");
        refused.Reason.Should().Be(declarations.Failure);

        DatabaseObjectDetail detail = await objects.GetObjectAsync(scope, "quartz", "qrtz_triggers", Token);
        detail.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        detail.Reason.Should().NotContain(nameof(IBrokenStore)).And.NotContain("connection string");

        harness.Policies.Calls.Should().Contain(static x =>
            x.Resource.StoreName == nameof(IBrokenStore) && x.Resource.TenantId == null && x.Resource.Capability == null,
            "the broken store's own store policy is what was asked");

        // Its store policy passes: named, with its reason.
        harness.Policies.Allow(static _ => true);

        DatabaseBrowserOverview named = await objects.GetOverviewAsync(scope, Token);

        named.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        named.Reason.Should().Be(declarations.NamedFailure);

        (await objects.GetObjectAsync(scope, "quartz", "qrtz_triggers", Token)).Reason.Should().Contain(nameof(IBrokenStore));
    }

    /// <summary>
    /// SEC-fix F4: another store's databases are enumerated only to name the resource a policy is shown - so with
    /// no store policy, and no write policy while the capability is on, nothing is enumerated at all. On a
    /// master-table or sharded tenancy an enumeration is a query, and the first one in the process is that
    /// store's own <c>CreateOrUpdate</c>; every document open reads this gate.
    /// </summary>
    [Theory]
    [InlineData(null, null, true, false)]
    [InlineData(null, null, false, false)]
    [InlineData(null, WritePolicy, false, false)]
    [InlineData(null, WritePolicy, true, true)]
    [InlineData(StorePolicy, null, false, true)]
    [InlineData(StorePolicy, WritePolicy, true, true)]
    public async Task Another_stores_databases_are_enumerated_only_when_a_policy_will_be_asked(
        string? storePolicy,
        string? writePolicy,
        bool capability,
        bool enumerated)
    {
        int builds = 0;

        await using Harness harness = Harness.Create(
            options =>
            {
                options.StoreAuthorizationPolicy = storePolicy;
                options.WriteAuthorizationPolicy = writePolicy;
                options.Capabilities.BrowseDatabase = capability;
            },
            services => services.AddMartenStore<IElsewhereStore>(options =>
            {
                // A tenancy with databases of its own - the shape whose enumeration can be a query - seen through a
                // proxy that counts every BuildDatabases(), which is what AllDatabases() is.
                options.MultiTenantedDatabases(tenancy =>
                    tenancy.AddMultipleTenantDatabase(DummyConnectionString, "elsewhere-db").ForTenants("acme"));
                options.DatabaseSchemaName = "elsewhere";

                MartenStudio.Tests.Projections.ObservedTenancy observed =
                    MartenStudio.Tests.Projections.ObservedTenancy.Over(options.Tenancy, masterTableShape: false);
                observed.OnBuildDatabases = () => Interlocked.Increment(ref builds);
                options.Tenancy = (global::Marten.Storage.ITenancy) (object) observed;
            }));

        ResolvedScope resolved = await harness.Resolve<StudioScopeResolver>()
            .ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        // Built first, so whatever building it costs is not counted against the gate.
        harness.Resolve<IElsewhereStore>().Should().NotBeNull();
        int before = Volatile.Read(ref builds);

        try
        {
            await harness.Access.GateAsync(TenantScope, resolved, Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The schema list needs a database, which this test has none of; the other stores came first.
        }

        int during = Volatile.Read(ref builds) - before;

        if (enumerated)
        {
            during.Should().BePositive("a policy is asked about that store, by its own identity for this database");
        }
        else
        {
            during.Should().Be(0, "nobody is asked, so nothing is enumerated");
        }
    }

    [Fact]
    public async Task Every_registered_stores_declarations_are_read_and_the_resolved_stores_schemas_are_its_own()
    {
        await using Harness harness = Harness.Create(static _ => { });

        StudioScopeResolver resolver = harness.Resolve<StudioScopeResolver>();
        ResolvedScope resolved = await resolver.ResolveAsync(new StudioScope("default", string.Empty, null), null, Token);

        DatabaseDeclarations declarations = harness.Access.ReadDeclarations(resolved);

        declarations.Succeeded.Should().BeTrue(declarations.Failure);
        declarations.StoreSchemas.Should().Contain("public");
        declarations.Classifier!.Stores.Should().ContainSingle(static x => x.StoreKey == "default");
    }

    /// <summary>
    /// Every failure is a value: a scope the visitor may not have comes back as a result the page draws,
    /// never as an exception - and a read that was refused its scope is not a <c>BrowseDatabase</c> read.
    /// The store policy's refusal of a list, an object or a definition is audited as what it is - a scope
    /// refusal, with no capability named - because its sentence is the one a missing store gets, and the
    /// trail is where the difference is kept.
    /// </summary>
    [Fact]
    public async Task A_scope_the_visitor_may_not_have_is_a_value_and_not_an_exception()
    {
        await using Harness harness = Harness.Create(static options => options.Capabilities.BrowseDatabase = true);
        harness.Policies.DenyEverything();

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        DatabaseBrowserOverview overview = await objects.GetOverviewAsync(TenantScope, Token);
        overview.Refusal.Should().Be(DatabaseRefusal.Unavailable);
        overview.Reason.Should().Be("Not authorized for the requested store, database or tenant.");

        DatabaseObjectList list = await objects.ListAsync(TenantScope, new DatabaseObjectQuery(DatabaseObjectCategory.Tables), Token);
        list.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        DatabaseObjectDetail detail = await objects.GetObjectAsync(TenantScope, "public", "orders", Token);
        detail.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(
            TenantScope, new DatabaseObjectRef(DatabaseObjectKind.View, "public", "v"), Token);
        definition.Refusal.Should().Be(DatabaseRefusal.Unavailable);

        IReadOnlyList<StudioActionLogEntry> audited = harness.Ring.GetLatest();
        audited.Select(static x => x.Action).Should().BeEquivalentTo(
            [DatabaseAccess.ListAction, DatabaseAccess.ObjectAction, DatabaseAccess.DefinitionAction],
            "each by-name read the store policy refused is in the trail");
        audited.Should().OnlyContain(static x => !x.Succeeded && x.Capability == null && x.Message == "Not authorized for this store, database or tenant.");
    }

    /// <summary>
    /// DB-1-fix re-review, item 4: in a schema the resolved store does not declare, a hidden type's table
    /// or function is asked the capability, the policies and the schema list first, like every other name
    /// there - "not found" where the rest get "capability off" would say which names are hidden types'.
    /// </summary>
    [Theory]
    [InlineData(false, true, nameof(DatabaseRefusal.CapabilityOff))]
    [InlineData(true, false, nameof(DatabaseRefusal.WritePolicy))]
    [InlineData(true, true, nameof(DatabaseRefusal.SchemaNotBrowsable))]
    public async Task A_hidden_name_in_another_stores_schema_is_refused_exactly_as_any_other_name_there(
        bool capability,
        bool policyAllows,
        string expectedRefusal)
    {
        DatabaseRefusal expected = Enum.Parse<DatabaseRefusal>(expectedRefusal);

        await using Harness harness = Harness.Create(
            options =>
            {
                options.Capabilities.BrowseDatabase = capability;
                options.IsDocumentTypeVisible = static type => type != typeof(DbSecret);
                options.BrowsableSchemas.Add("quartz");
            },
            static services => services.AddMartenStore<IElsewhereStore>(options =>
            {
                options.Connection(DummyConnectionString);
                options.DatabaseSchemaName = "elsewhere";
                options.Schema.For<DbSecret>();
            }));

        if (!policyAllows)
        {
            harness.Policies.Allow(static resource => resource.Capability is null);
        }

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        foreach (DatabaseObjectRef reference in new[]
                 {
                     new DatabaseObjectRef(DatabaseObjectKind.View, "elsewhere", "mt_doc_dbsecret"),
                     new DatabaseObjectRef(DatabaseObjectKind.Function, "elsewhere", "mt_upsert_dbsecret", Arguments: "doc jsonb"),
                     new DatabaseObjectRef(DatabaseObjectKind.View, "elsewhere", "mt_doc_nosuchthing"),
                     new DatabaseObjectRef(DatabaseObjectKind.View, "elsewhere", "some_view"),
                 })
        {
            DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(TenantScope, reference, Token);

            definition.Refusal.Should().Be(expected, reference.Name);
        }
    }

    /// <summary>
    /// DB-1-fix re-review, item 5: another store's policies are asked about <em>this</em> database, by that
    /// store's own identity for it - never with the visitor's database id, which is this store's spelling
    /// and, empty, means "this store's default" and so "that store's default" to the other.
    /// </summary>
    [Fact]
    public async Task Another_stores_policy_is_asked_about_this_database_as_that_store_names_it()
    {
        await using Harness harness = Harness.Create(
            static options => options.Capabilities.BrowseDatabase = true,
            static services => services.AddMartenStore<IElsewhereStore>(options =>
            {
                options.Connection(DummyConnectionString.Replace("Host=marten-studio-database-access.invalid", "Host=MARTEN-STUDIO-DATABASE-ACCESS.invalid", StringComparison.Ordinal));
                options.DatabaseSchemaName = "elsewhere";
            }));

        DatabaseBrowseGrant grant = await harness.Access.RequireBrowseAsync(TenantScope, DatabaseAccess.ListAction, "elsewhere", Token);
        grant.Allowed.Should().BeTrue(grant.Reason);

        IReadOnlyList<global::Marten.Storage.IMartenDatabase> elsewhere = await harness.Resolve<IElsewhereStore>().Storage.AllDatabases();
        string theirs = elsewhere.Single().Id.Identity;

        try
        {
            await harness.Access.GateAsync(TenantScope, grant.Resolved!, Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The schema list needs a database, which this test has none of; the policies were asked first.
        }

        MartenStoreResource asked = harness.Policies.Calls.Select(static x => x.Resource).Last(static x => x.StoreName == nameof(IElsewhereStore));

        TenantScope.DatabaseId.Should().BeEmpty("the premise: the visitor's own id means this store's default");
        asked.DatabaseIdentifier.Should().Be(theirs, "that store's own identity for the resolved database");
        asked.DatabaseIdentifier.Should().BeEquivalentTo(grant.Resolved!.Database.Id.Identity, "the same database, however its host is spelled");
        asked.TenantId.Should().BeNull();
    }

    [Fact]
    public async Task A_table_a_sequence_and_an_aggregate_have_no_definition_to_ask_for()
    {
        await using Harness harness = Harness.Create(static _ => { });

        IDatabaseObjectService objects = harness.Resolve<IDatabaseObjectService>();

        foreach (DatabaseObjectKind kind in new[] { DatabaseObjectKind.Table, DatabaseObjectKind.Sequence, DatabaseObjectKind.Aggregate })
        {
            DatabaseObjectDefinition definition = await objects.GetDefinitionAsync(
                TenantScope, new DatabaseObjectRef(kind, "public", "anything"), Token);

            definition.Refusal.Should().Be(DatabaseRefusal.NotApplicable, kind.ToString());
        }

        harness.Policies.Calls.Should().BeEmpty("nothing is asked for a request that does not apply");
    }

    /// <summary>A store an application registered that cannot be built.</summary>
    public interface IBrokenStore : IDocumentStore;

    /// <summary>A document type the host hides, and never registers with <c>Schema.For</c>.</summary>
    public class LateSecret
    {
        /// <summary>The id.</summary>
        public Guid Id { get; set; }
    }

    /// <summary>A real studio container over a store that never connects.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, IServiceScope scope, TestStoreAuthorizationService policies)
        {
            this.provider = provider;
            this.scope = scope;
            Policies = policies;
        }

        public TestStoreAuthorizationService Policies { get; }

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public DatabaseAccess Access => Resolve<DatabaseAccess>();

        public T Resolve<T>() where T : notnull => scope.ServiceProvider.GetRequiredService<T>();

        public static Harness Create(Action<MartenStudioOptions> configure, Action<IServiceCollection>? extra = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var policies = new TestStoreAuthorizationService();

            var services = new ServiceCollection();
            services.AddLogging();
            services.AddMarten(x => x.Connection(DummyConnectionString));
            services.AddMartenStudio(options =>
            {
                options.StoreAuthorizationPolicy = StorePolicy;
                options.WriteAuthorizationPolicy = WritePolicy;
                configure(options);
            });

            extra?.Invoke(services);

            services.AddSingleton<IAuthorizationService>(policies);
            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            return new Harness(provider, provider.CreateScope(), policies);
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }
}

/// <summary>A second store, in a schema of its own, that registers the type the host hides.</summary>
public interface IElsewhereStore : IDocumentStore;
