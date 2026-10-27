using System.Globalization;

using JasperFx;
using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events.Projections;

using MartenStudio.Integration.Tests.Documents;
using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using PostgresStudio.Internal.Sql;
using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>An event the grouping projection refuses with an <see cref="InvalidOperationException" />.</summary>
/// <param name="Reason">Why it is here.</param>
public record GroupPoison(string Reason);

/// <summary>An event the grouping projection refuses with a <see cref="DivideByZeroException" />.</summary>
/// <param name="Divisor">Always zero.</param>
public record GroupBadMath(int Divisor);

/// <summary>An event only the hand-written letters of the cap test name.</summary>
/// <param name="Index">Which one.</param>
public record GroupCapEvent(int Index);

/// <summary>What the grouping projection would write, if it ever did.</summary>
public class GroupNote
{
    /// <summary>The id.</summary>
    public Guid Id { get; set; }
}

/// <summary>
/// An async projection that throws on two event types with two exception types, so a real daemon writes real dead
/// letters that fall into two groups. Internal for <c>PoisonProjection</c>'s reason (the generator's public dispatcher).
/// </summary>
internal partial class GroupingProjection : EventProjection
{
    /// <summary>Throws, every time.</summary>
    public static GroupNote Create(GroupPoison poison) =>
        throw new InvalidOperationException("GroupingProjection refuses " + poison.Reason);

    /// <summary>Divides by zero, every time.</summary>
    public static GroupNote Create(GroupBadMath math) =>
        throw new DivideByZeroException("GroupingProjection divided by " + math.Divisor.ToString(CultureInfo.InvariantCulture));
}

/// <summary>
/// A conjoined event store with event skipping, two tenants, and the grouping projection's dead letters, written by a
/// daemon run once here: acme 3 <see cref="GroupPoison" /> and 2 <see cref="GroupBadMath" />, globex 2 <see cref="GroupPoison" />.
/// </summary>
public sealed class DeadLetterGroupsFixture(PostgresFixture postgres) : IAsyncLifetime
{
    /// <summary>The first tenant.</summary>
    public const string Acme = "acme";

    /// <summary>The second tenant.</summary>
    public const string Globex = "globex";

    private static readonly TimeSpan DaemonDeadline = TimeSpan.FromSeconds(60);

    private readonly List<ServiceProvider> providers = [];

    /// <summary>The grouped view's cache clock: the cache is the production one, and a test that writes letters past the studio moves it on.</summary>
    private readonly Health.MovableClock groupClock = new(DateTimeOffset.UtcNow);

    /// <summary>The schema.</summary>
    public string Schema { get; } = "m3_dead_letter_groups";

    /// <summary>The store.</summary>
    public DocumentStore Store { get; private set; } = null!;

    /// <summary>The studio's event service, every capability on.</summary>
    internal IEventDataService Events { get; private set; } = null!;

    /// <summary>The audit ring <see cref="Events" /> writes to.</summary>
    internal StudioActionLogService Audit { get; private set; } = null!;

    /// <summary>A studio with no capability on.</summary>
    internal IEventDataService ReadOnlyEvents { get; private set; } = null!;

    /// <summary>The audit ring <see cref="ReadOnlyEvents" /> writes to.</summary>
    internal StudioActionLogService ReadOnlyAudit { get; private set; } = null!;

    /// <summary>A studio whose host hides <c>DeadLetterEvent</c>.</summary>
    internal IEventDataService HidingEvents { get; private set; } = null!;

    /// <summary>The store and write policies' answers - everything allowed unless a test says otherwise.</summary>
    internal FakeStoreAuthorizationService Policy { get; } = new();

    /// <summary>The sequences of acme's poison events.</summary>
    public List<long> AcmePoison { get; } = [];

    /// <summary>The sequences of acme's bad-maths events.</summary>
    public List<long> AcmeBadMath { get; } = [];

    /// <summary>The sequences of globex's poison events.</summary>
    public List<long> GlobexPoison { get; } = [];

    /// <inheritdoc />
    public async ValueTask InitializeAsync()
    {
        if (!DockerAvailability.IsAvailable)
        {
            return;
        }

        await postgres.CreateSchemaAsync(Schema);

        Store = DocumentStore.For(options =>
        {
            options.Connection(postgres.ConnectionString);
            options.DatabaseSchemaName = Schema;
            options.Events.DatabaseSchemaName = Schema;
            options.AutoCreateSchemaObjects = AutoCreate.All;
            options.Events.TenancyStyle = TenancyStyle.Conjoined;
            options.Policies.AllDocumentsAreMultiTenanted();
            options.Events.EnableEventSkippingInProjectionsOrSubscriptions = true;
            options.Events.AddEventType<GroupPoison>();
            options.Events.AddEventType<GroupBadMath>();
            options.Events.AddEventType<GroupCapEvent>();
            options.Projections.Add(new GroupingProjection(), ProjectionLifecycle.Async);
        });

        await Store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        AcmePoison.AddRange(await AppendAsync(Acme, 3, static index => new GroupPoison("acme-" + index.ToString(CultureInfo.InvariantCulture))));
        AcmeBadMath.AddRange(await AppendAsync(Acme, 2, static _ => new GroupBadMath(0)));
        GlobexPoison.AddRange(await AppendAsync(Globex, 2, static index => new GroupPoison("globex-" + index.ToString(CultureInfo.InvariantCulture))));

        await RunDaemonUntilAsync(7);

        (Events, Audit) = Build(MartenStudioCapabilities.All(), hideDeadLetters: false);
        (ReadOnlyEvents, ReadOnlyAudit) = Build(new MartenStudioCapabilities(), hideDeadLetters: false);
        (HidingEvents, _) = Build(MartenStudioCapabilities.All(), hideDeadLetters: true);
    }

    /// <summary>A scope of the default store and database: <paramref name="tenant" />'s, or every tenant's.</summary>
    internal static StudioScope Scope(string? tenant = null) => new(MartenStoreRegistry.DefaultStoreKey, string.Empty, tenant);

    /// <summary>Appends <paramref name="count" /> one-event streams to <paramref name="tenant" />, and answers their sequences.</summary>
    public async Task<List<long>> AppendAsync(string tenant, int count, Func<int, object> create)
    {
        List<Guid> streams = [];

        await using (IDocumentSession session = Store.LightweightSession(tenant))
        {
            for (int index = 0; index < count; index++)
            {
                Guid id = Guid.CreateVersion7();
                streams.Add(id);
                session.Events.StartStream(id, create(index));
            }

            await session.SaveChangesAsync();
        }

        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select seq_id from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where stream_id = any(@ids) order by seq_id", connection);
        command.Parameters.AddWithValue("ids", streams.ToArray());

        List<long> sequences = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            sequences.Add(reader.GetInt64(0));
        }

        return sequences;
    }

    /// <summary>
    /// Stores dead letters by hand - the cap test's thousand, which a daemon would take minutes to write - one per sequence,
    /// shaped as the daemon shapes them (JasperFx's <c>DeadLetterEvent</c> constructor).
    /// </summary>
    public async Task StoreLettersAsync(string projection, string exceptionType, string tenant, IEnumerable<long> sequences)
    {
        await using IDocumentSession session = Store.LightweightSession();

        foreach (long sequence in sequences)
        {
            session.Store(new DeadLetterEvent
            {
                Id = Guid.CreateVersion7(),
                ProjectionName = projection,
                ShardName = "All",
                Timestamp = DateTimeOffset.UtcNow,
                ExceptionMessage = "Failure to apply event #" + sequence.ToString(CultureInfo.InvariantCulture) + " Id(" + Guid.NewGuid() + ")",
                ExceptionType = exceptionType,
                EventSequence = sequence,
                TenantId = tenant,
            });
        }

        await session.SaveChangesAsync();

        // Written past the studio, so its shared read of the groups (one per interval, D10) is moved past its interval.
        groupClock.Advance(TimeSpan.FromMinutes(1));
    }

    /// <summary>Which of <paramref name="sequences" /> are skipped, read straight from the table.</summary>
    public async Task<List<long>> SkippedAsync(IEnumerable<long> sequences)
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand(
            "select seq_id from " + SqlIdentifier.Qualify(Schema, "mt_events") + " where seq_id = any(@seqs) and is_skipped order by seq_id",
            connection);
        command.Parameters.AddWithValue("seqs", sequences.ToArray());

        List<long> skipped = [];
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            skipped.Add(reader.GetInt64(0));
        }

        return skipped;
    }

    /// <summary>An open connection to the store's database.</summary>
    public async Task<NpgsqlConnection> OpenAsync()
    {
        var connection = new NpgsqlConnection(postgres.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    /// <summary>
    /// The daemon, run here and only here (hard rule 11 is about studio code: this fixture owns its store and no other
    /// daemon exists), until Marten has recorded <paramref name="letters" /> dead letters. Waits on the condition, never a delay.
    /// </summary>
    private async Task RunDaemonUntilAsync(long letters)
    {
        using IProjectionDaemon daemon = await Store.BuildProjectionDaemonAsync();
        await daemon.StartAllAsync();

        try
        {
            using var poll = new PeriodicTimer(TimeSpan.FromMilliseconds(200));
            using var deadline = new CancellationTokenSource(DaemonDeadline);

            while (await poll.WaitForNextTickAsync(deadline.Token))
            {
                if (await CountLettersAsync() >= letters)
                {
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"The daemon did not record {letters} dead letters within {DaemonDeadline}; it recorded {await CountLettersAsync()}.");
        }
        finally
        {
            await daemon.StopAllAsync();
        }
    }

    private async Task<long> CountLettersAsync()
    {
        await using NpgsqlConnection connection = await OpenAsync();
        await using var command = new NpgsqlCommand("select count(*) from " + SqlIdentifier.Qualify(Schema, "mt_doc_deadletterevent"), connection);
        return (long) (await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// A studio, every capability on, whose grouped view groups at most <paramref name="lettersGrouped" /> letters - the
    /// production bound is 20,000, which a test would take minutes to write past.
    /// </summary>
    internal IEventDataService WithLettersGrouped(int lettersGrouped) =>
        Build(MartenStudioCapabilities.All(), hideDeadLetters: false, lettersGrouped).Events;

    private (IEventDataService Events, StudioActionLogService Audit) Build(
        MartenStudioCapabilities capabilities,
        bool hideDeadLetters,
        int lettersGrouped = DeadLetterGroupLimits.MaxLettersGrouped)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton<IDocumentStore>(Store);
        services.AddSingleton<IAuthorizationService>(Policy);
        services.AddSingleton(new DeadLetterGroupCache(groupClock, TimeSpan.FromSeconds(5)) { LettersGrouped = lettersGrouped });
        services.AddMartenStudio(options =>
        {
            options.Capabilities = capabilities;
            options.StoreAuthorizationPolicy = "studio-store";
            options.WriteAuthorizationPolicy = "studio-write";
            options.DiscoverTenantIds = false;
            options.KnownTenantIds.Add(Acme);
            options.KnownTenantIds.Add(Globex);

            if (hideDeadLetters)
            {
                options.IsDocumentTypeVisible = static type => type != typeof(DeadLetterEvent);
            }
        });

        ServiceProvider provider = services.BuildServiceProvider();
        providers.Add(provider);

        return (provider.CreateScope().ServiceProvider.GetRequiredService<IEventDataService>(), provider.GetRequiredService<StudioActionLogService>());
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (ServiceProvider provider in providers)
        {
            await provider.DisposeAsync();
        }

        if (Store is not null)
        {
            await Store.DisposeAsync();
        }

        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// M3, live: groups form from real daemon dead letters, a group opens in the list, and "Skip this group" skips exactly
/// that group's events - in the scope's tenant, at most a thousand at a time, audited once - through single Skip's path.
/// </summary>
/// <remarks>
/// The tests share one database. Each mutating test owns its group, and every assertion about another group is one no
/// test changes: nothing here skips globex's poison events.
/// </remarks>
public class DeadLetterGroupsLiveTests(DeadLetterGroupsFixture fixture) : IClassFixture<DeadLetterGroupsFixture>
{
    /// <summary>What Marten names an <c>EventProjection</c> it was handed: the type's full name.</summary>
    private static readonly string Projection = typeof(GroupingProjection).FullName!;

    private const string ApplyFingerprint = "Failure to apply event #{n} Id({guid})";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task Groups_form_from_real_daemon_dead_letters()
    {
        DeadLetterGroupList list = await fixture.Events.ListDeadLetterGroupsAsync(DeadLetterGroupsFixture.Scope(), Token);

        list.Error.Should().BeNull();
        list.RecordsSkips.Should().BeTrue();
        list.ReadAt.Should().NotBeNull();

        DeadLetterGroup poison = Single(list, "InvalidOperationException");
        poison.Key.Should().Be(new DeadLetterGroupKey(Projection, "All", "group_poison", "InvalidOperationException", ApplyFingerprint));
        poison.Letters.Should().Be(5);
        poison.Events.Should().Be(5);
        poison.TenantCount.Should().Be(2);
        poison.TenantNames.Should().Equal(DeadLetterGroupsFixture.Acme, DeadLetterGroupsFixture.Globex);
        poison.FirstSeen.Should().NotBeNull().And.BeOnOrBefore(poison.LastSeen!.Value);
        poison.SampleMessage.Should().StartWith("Failure to apply event #");

        DeadLetterGroup badMath = Single(list, "DivideByZeroException");
        badMath.Key.EventType.Should().Be("group_bad_math");
        badMath.Key.Fingerprint.Should().Be(ApplyFingerprint, "Marten stores the daemon's own message, not the projection's");
        badMath.Letters.Should().Be(2);
        badMath.TenantNames.Should().Equal(DeadLetterGroupsFixture.Acme);
    }

    /// <summary>A tenant's scope groups its own letters and looks their events up in itself (hard rule 15).</summary>
    [PostgresFact]
    public async Task A_tenants_scope_groups_only_its_own_letters()
    {
        DeadLetterGroupList acme = await fixture.Events.ListDeadLetterGroupsAsync(DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Acme), Token);
        DeadLetterGroupList globex = await fixture.Events.ListDeadLetterGroupsAsync(DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Globex), Token);

        Single(acme, "InvalidOperationException").Letters.Should().Be(3);
        Single(acme, "InvalidOperationException").TenantNames.Should().Equal(DeadLetterGroupsFixture.Acme);
        Single(globex, "InvalidOperationException").Letters.Should().Be(2);
        globex.Groups.Should().NotContain(static x => x.Key.ExceptionType == "DivideByZeroException", "acme's failures are acme's");
    }

    /// <summary>D29: a tenant is counted for everyone the scope admits, and named only to a visitor that tenant's store policy passes.</summary>
    [PostgresFact]
    public async Task A_tenant_the_visitor_may_not_see_is_counted_and_not_named()
    {
        fixture.Policy.Allow(static resource => resource.TenantId is null or DeadLetterGroupsFixture.Acme);

        try
        {
            DeadLetterGroupList list = await fixture.Events.ListDeadLetterGroupsAsync(DeadLetterGroupsFixture.Scope(), Token);

            DeadLetterGroup poison = Single(list, "InvalidOperationException");
            poison.TenantCount.Should().Be(2);
            poison.TenantNames.Should().Equal(DeadLetterGroupsFixture.Acme);
            poison.TenantsNotNamed.Should().Be(1);
        }
        finally
        {
            fixture.Policy.AllowEverything();
        }
    }

    [PostgresFact]
    public async Task The_list_opened_on_a_group_lists_exactly_its_letters()
    {
        DeadLetterGroupList list = await fixture.Events.ListDeadLetterGroupsAsync(DeadLetterGroupsFixture.Scope(), Token);
        DeadLetterGroup poison = Single(list, "InvalidOperationException");

        DeadLetterPage page = await fixture.Events.ListDeadLettersAsync(
            DeadLetterGroupsFixture.Scope(), new DeadLetterQuery { Group = poison.Key, PageSize = 50 }, Token);

        page.Error.Should().BeNull();
        page.Rows.Should().HaveCount(5);
        page.Rows.Should().OnlyContain(static x => x.ExceptionType == "InvalidOperationException" && x.ProjectionName == Projection);
        page.Rows.Select(static x => x.EventSequence)
            .Should().BeEquivalentTo(fixture.AcmePoison.Concat(fixture.GlobexPoison));
    }

    /// <summary>
    /// SEC-FIX: the list opened on a group - whose <c>?group=</c> anyone can write by hand - looks for its letters only among
    /// the newest letters the grouped view groups, so the SQL fingerprint runs over that many at most; it lists the letters
    /// the group counts, newest first, and says when the window cut. The skip's count is not windowed: it reads all of the
    /// group's letters, as the sentence says.
    /// </summary>
    [PostgresFact]
    public async Task The_list_opened_on_a_group_looks_only_among_the_letters_the_grouped_view_groups_and_says_when_that_cut()
    {
        const int Window = 4;
        StudioScope everyone = DeadLetterGroupsFixture.Scope();

        // Six letters of a group of their own, the newest in the database: more than the test's bound of four in all.
        List<long> sequences = await fixture.AppendAsync(DeadLetterGroupsFixture.Acme, 6, static index => new GroupCapEvent(index));
        await fixture.StoreLettersAsync("WindowProjection", "FormatException", DeadLetterGroupsFixture.Acme, sequences);

        IEventDataService bounded = fixture.WithLettersGrouped(Window);

        DeadLetterGroupList list = await bounded.ListDeadLetterGroupsAsync(everyone, Token);
        list.LettersTruncated.Should().BeTrue("the database holds more letters than the bound");
        DeadLetterGroup group = Single(list, "FormatException");
        group.Letters.Should().Be(Window, "the newest letters are this group's");

        DeadLetterPage page = await bounded.ListDeadLettersAsync(everyone, new DeadLetterQuery { Group = group.Key, PageSize = 50 }, Token);

        page.Error.Should().BeNull();
        page.Rows.Should().HaveCount(Window, "the list shows the letters the group counts, and no older one");
        page.LettersCut.Should().BeTrue("the page says the window cut, in the grouped view's sentence");

        // The production bound, over the same database: every letter of the group, and nothing cut.
        DeadLetterPage whole = await fixture.Events.ListDeadLettersAsync(everyone, new DeadLetterQuery { Group = group.Key, PageSize = 50 }, Token);

        whole.Rows.Should().HaveCount(sequences.Count);
        whole.LettersCut.Should().BeFalse();
        page.Rows.Select(static x => x.Id).Should().Equal(whole.Rows.Take(Window).Select(static x => x.Id), "the window holds the newest of them");

        // The same group's skip is counted over all of its letters - it is not the list - and still only its own.
        DeadLetterGroupSkipPlan plan = await bounded.DescribeDeadLetterGroupSkipAsync(everyone, group.Key, Token);
        plan.NotYetSkipped.Should().Be(sequences.Count);
    }

    /// <summary>
    /// The bulk skip: the group's events and nobody else's, through single Skip's path, as one audited operation that
    /// names the group and the count - and no single "Skip event" entry beside it.
    /// </summary>
    [PostgresFact]
    public async Task Skipping_a_group_skips_exactly_its_events_and_is_audited_once()
    {
        StudioScope everyone = DeadLetterGroupsFixture.Scope();
        DeadLetterGroup badMath = Single(await fixture.Events.ListDeadLetterGroupsAsync(everyone, Token), "DivideByZeroException");

        DeadLetterGroupSkipPlan plan = await fixture.Events.DescribeDeadLetterGroupSkipAsync(everyone, badMath.Key, Token);
        plan.Should().Be(new DeadLetterGroupSkipPlan(2, 2, 0, null));

        long before = fixture.Audit.GetLatest().Count;

        long marked = await fixture.Events.SkipDeadLetterGroupAsync(everyone, badMath.Key, plan.ConfirmationPhrase, Token);

        marked.Should().Be(2);
        (await fixture.SkippedAsync(fixture.AcmeBadMath)).Should().Equal(fixture.AcmeBadMath);
        (await fixture.SkippedAsync(fixture.GlobexPoison)).Should().BeEmpty("another group's events are not this group's");

        List<StudioActionLogEntry> entries = [.. fixture.Audit.GetLatest().Take(fixture.Audit.GetLatest().Count - (int) before)];
        entries.Should().ContainSingle("one operation, one entry");

        StudioActionLogEntry entry = entries[0];
        entry.Action.Should().Be(EventDataService.SkipGroupAction);
        entry.Target.Should().Be(badMath.Key.Describe() + "; 2 events confirmed");
        entry.Succeeded.Should().BeTrue();
        entry.Message.Should().Be("2 events marked as skipped");
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));

        DeadLetterGroup after = Single(await fixture.Events.ListDeadLetterGroupsAsync(everyone, Token), "DivideByZeroException");
        after.EventsSkipped.Should().Be(2, "the write forgot the cached groups");
        (await fixture.Events.DescribeDeadLetterGroupSkipAsync(everyone, badMath.Key, Token)).ToSkip.Should().Be(0);
    }

    /// <summary>From a tenant's scope the group is that tenant's letters, and their events are proved to be that tenant's.</summary>
    [PostgresFact]
    public async Task A_tenant_scoped_skip_never_touches_another_tenants_events()
    {
        StudioScope acme = DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Acme);
        DeadLetterGroup poison = Single(await fixture.Events.ListDeadLetterGroupsAsync(acme, Token), "InvalidOperationException");

        DeadLetterGroupSkipPlan plan = await fixture.Events.DescribeDeadLetterGroupSkipAsync(acme, poison.Key, Token);

        long marked = await fixture.Events.SkipDeadLetterGroupAsync(acme, poison.Key, plan.ConfirmationPhrase, Token);

        marked.Should().Be(3);
        (await fixture.SkippedAsync(fixture.AcmePoison)).Should().Equal(fixture.AcmePoison);
        (await fixture.SkippedAsync(fixture.GlobexPoison)).Should().BeEmpty("globex's letters are in the same group, and not in acme's scope");
    }

    /// <summary>
    /// A thousand events per action: a group of 1,005 skips its lowest 1,000, says so, and needs a second action for the
    /// rest; a third finds nothing left and is refused, audited.
    /// </summary>
    [PostgresFact]
    public async Task The_cap_is_enforced()
    {
        StudioScope acme = DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Acme);
        List<long> sequences = await fixture.AppendAsync(DeadLetterGroupsFixture.Acme, 1_005, static index => new GroupCapEvent(index));
        await fixture.StoreLettersAsync("CapProjection", "TimeoutException", DeadLetterGroupsFixture.Acme, sequences);

        DeadLetterGroup group = Single(await fixture.Events.ListDeadLetterGroupsAsync(acme, Token), "TimeoutException");
        group.Events.Should().Be(1_005);

        DeadLetterGroupSkipPlan first = await fixture.Events.DescribeDeadLetterGroupSkipAsync(acme, group.Key, Token);
        first.ToSkip.Should().Be(DeadLetterGroupLimits.MaxEventsPerSkip);
        first.NotYetSkipped.Should().Be(1_005);
        first.OverCap.Should().BeTrue();
        first.ConfirmationPhrase.Should().Be("1000");

        (await fixture.Events.SkipDeadLetterGroupAsync(acme, group.Key, "1000", Token)).Should().Be(1_000);
        (await fixture.SkippedAsync(sequences)).Should().Equal(sequences.Take(1_000), "the lowest sequences first");
        fixture.Audit.GetLatest()[0].Message.Should().Be(
            "1,000 events marked as skipped; more of the group's events are not skipped yet - one action skips at most 1,000");

        DeadLetterGroupSkipPlan second = await fixture.Events.DescribeDeadLetterGroupSkipAsync(acme, group.Key, Token);
        second.Should().Be(new DeadLetterGroupSkipPlan(5, 5, 1_000, null));

        (await fixture.Events.SkipDeadLetterGroupAsync(acme, group.Key, "5", Token)).Should().Be(5);
        (await fixture.SkippedAsync(sequences)).Should().Equal(sequences);

        Func<Task> third = () => fixture.Events.SkipDeadLetterGroupAsync(acme, group.Key, "0", Token);
        (await third.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(EventDataService.NothingToSkipMessage);

        StudioActionLogEntry refused = fixture.Audit.GetLatest()[0];
        refused.Action.Should().Be(EventDataService.SkipGroupAction);
        refused.Succeeded.Should().BeFalse();
    }

    /// <summary>A typed count that is not the count now is refused, audited with the count that was typed, and skips nothing.</summary>
    [PostgresFact]
    public async Task A_typed_count_that_is_not_the_count_is_refused_and_skips_nothing()
    {
        StudioScope acme = DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Acme);
        List<long> sequences = await fixture.AppendAsync(DeadLetterGroupsFixture.Acme, 3, static index => new GroupCapEvent(index));
        await fixture.StoreLettersAsync("MismatchProjection", "ArgumentException", DeadLetterGroupsFixture.Acme, sequences);

        DeadLetterGroup group = Single(await fixture.Events.ListDeadLetterGroupsAsync(acme, Token), "ArgumentException");

        Func<Task> skipping = () => fixture.Events.SkipDeadLetterGroupAsync(acme, group.Key, "4", Token);

        (await skipping.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(EventDataService.ConfirmationMismatch(3));

        (await fixture.SkippedAsync(sequences)).Should().BeEmpty();

        StudioActionLogEntry entry = fixture.Audit.GetLatest()[0];
        entry.Action.Should().Be(EventDataService.SkipGroupAction);
        entry.Target.Should().Be(group.Key.Describe() + "; 4 events confirmed");
        entry.Succeeded.Should().BeFalse();
    }

    [PostgresFact]
    public async Task Without_the_capability_the_skip_is_refused_audited_and_skips_nothing()
    {
        StudioScope everyone = DeadLetterGroupsFixture.Scope();
        DeadLetterGroup poison = Single(await fixture.ReadOnlyEvents.ListDeadLetterGroupsAsync(everyone, Token), "InvalidOperationException");

        Func<Task> skipping = () => fixture.ReadOnlyEvents.SkipDeadLetterGroupAsync(everyone, poison.Key, "5", Token);

        await skipping.Should().ThrowAsync<StudioCapabilityDeniedException>();
        (await fixture.SkippedAsync(fixture.GlobexPoison)).Should().BeEmpty();
        fixture.ReadOnlyAudit.GetLatest()[0].Should().Match<StudioActionLogEntry>(static x =>
            x.Action == EventDataService.SkipGroupAction && !x.Succeeded && x.Target.EndsWith("; 5 events confirmed", StringComparison.Ordinal));
    }

    /// <summary>The write policy is asked about the scope, as single Skip's is: a visitor it refuses skips nothing, audited as a scope denial.</summary>
    [PostgresFact]
    public async Task A_visitor_the_write_policy_refuses_skips_nothing()
    {
        StudioScope globex = DeadLetterGroupsFixture.Scope(DeadLetterGroupsFixture.Globex);
        DeadLetterGroup poison = Single(await fixture.Events.ListDeadLetterGroupsAsync(globex, Token), "InvalidOperationException");

        fixture.Policy.DenyWrites();

        try
        {
            Func<Task> skipping = () => fixture.Events.SkipDeadLetterGroupAsync(globex, poison.Key, "2", Token);

            await skipping.Should().ThrowAsync<StudioNotAuthorizedException>();
            (await fixture.SkippedAsync(fixture.GlobexPoison)).Should().BeEmpty();
            fixture.Audit.GetLatest()[0].Should().Match<StudioActionLogEntry>(static x =>
                x.Action == EventDataService.SkipGroupAction && !x.Succeeded && x.TenantId == DeadLetterGroupsFixture.Globex);
        }
        finally
        {
            fixture.Policy.AllowEverything();
        }
    }

    /// <summary>A host that hides <c>DeadLetterEvent</c> hides the groups, and the skip - which reads them - is refused.</summary>
    [PostgresFact]
    public async Task A_hidden_dead_letter_type_is_grouped_by_nothing_and_skipped_by_nothing()
    {
        StudioScope everyone = DeadLetterGroupsFixture.Scope();

        (await fixture.HidingEvents.ListDeadLetterGroupsAsync(everyone, Token)).IsHidden.Should().BeTrue();

        var key = new DeadLetterGroupKey(Projection, "All", "group_poison", "InvalidOperationException", ApplyFingerprint);
        Func<Task> skipping = () => fixture.HidingEvents.SkipDeadLetterGroupAsync(everyone, key, "5", Token);

        (await skipping.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(DeadLetterVisibility.HiddenMessage);
        (await fixture.SkippedAsync(fixture.GlobexPoison)).Should().BeEmpty();
    }

    /// <summary>
    /// The rule is written once and run by two engines: PostgreSQL's <c>regexp_replace</c> chain answers what
    /// <see cref="DeadLetterFingerprint.Of" /> answers, over every message the unit tests read and the edge cases between.
    /// </summary>
    [PostgresFact]
    public async Task The_fingerprint_is_the_same_in_PostgreSQL_as_in_CSharp()
    {
        await using NpgsqlConnection connection = await fixture.OpenAsync();

        List<string> mismatches = [];

        foreach (string message in DeadLetterFingerprintCorpus.Messages)
        {
            await using var command = new NpgsqlCommand { Connection = connection };
            command.Parameters.Add(new NpgsqlParameter("m", NpgsqlTypes.NpgsqlDbType.Text) { Value = message });
            command.CommandText = "select " + DeadLetterGroupQueries.FingerprintOf(command, "@m");

            string sql = (string) (await command.ExecuteScalarAsync(Token))!;
            string? csharp = DeadLetterFingerprint.Of(message);

            if (!string.Equals(sql, csharp, StringComparison.Ordinal))
            {
                mismatches.Add($"{message} -> SQL [{sql}] C# [{csharp}]");
            }
        }

        mismatches.Should().BeEmpty();
    }

    private static DeadLetterGroup Single(DeadLetterGroupList list, string exceptionType) =>
        list.Groups.Should().ContainSingle(x => x.Key.ExceptionType == exceptionType).Subject;
}
