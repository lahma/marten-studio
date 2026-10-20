using JasperFx.Events.Daemon;

using Marten;
using Marten.Storage;

using MartenStudio.Services;
using MartenStudio.Services.Events;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PostgresStudio.Services;

namespace MartenStudio.Tests.Events;

/// <summary>
/// CL1 item 4: the dead-letter screen honours <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> for
/// <c>DeadLetterEvent</c>, as D06 already did and as D27 says a hidden document type is honoured everywhere.
/// </summary>
/// <remarks>
/// <para>
/// Hidden, the list is <see cref="DeadLetterPage.Hidden" /> and reads nothing, the badge's count is no number, and Discard
/// and Rewind are refused - with <see cref="DeadLetterVisibility.HiddenMessage" />, which never names the type's alias -
/// and audited as refused. A delegate that throws is read as hiding the type (fail closed), and the failure is logged.
/// </para>
/// <para>
/// No database: the store's connection string is never opened, and every refusal here happens before one would be. Each
/// path has a positive control that lets the same call through - to fail later, against the unreachable host, for a
/// reason that is not the gate - which is how "refused" is known to be the gate's.
/// </para>
/// </remarks>
public class DeadLetterVisibilityTests
{
    private const string Unreachable =
        "Host=cl1-dead-letter-visibility.invalid;Database=none;Username=none;Password=none;Timeout=2";

    private const string Hidden = "Dead letters are hidden by MartenStudioOptions.IsDocumentTypeVisible.";

    private static readonly Guid LetterId = new("33333333-4444-5555-6666-777777777777");

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The two ways a host hides the type: by answering false, and by throwing - which fails closed.</summary>
    public static TheoryData<string> Hiding => ["hides", "throws"];

    [Theory]
    [MemberData(nameof(Hiding))]
    public async Task A_hidden_dead_letter_type_lists_as_hidden_and_reads_nothing(string how)
    {
        await using Harness harness = await Harness.CreateAsync(Visibility(how));

        DeadLetterPage page = await harness.Events.ListDeadLettersAsync(harness.Scope, new DeadLetterQuery(), Token);

        page.Should().BeSameAs(DeadLetterPage.Hidden, "before anything is read - against this unreachable host a read would have failed");
        page.IsHidden.Should().BeTrue();
        page.Rows.Should().BeEmpty();
        page.Error.Should().BeNull("the host's configuration is not a failure");
        DeadLetterVisibility.HiddenMessage.Should().Be(Hidden);
    }

    [Theory]
    [MemberData(nameof(Hiding))]
    public async Task A_hidden_dead_letter_type_has_no_number_on_the_badge(string how)
    {
        await using Harness harness = await Harness.CreateAsync(Visibility(how));

        (await harness.Events.CountDeadLettersAsync(harness.Scope, Token)).Should().BeNull("no number, as a hidden collection has none");
    }

    [Theory]
    [MemberData(nameof(Hiding))]
    public async Task Discarding_a_hidden_dead_letter_is_refused_and_audited_as_refused(string how)
    {
        await using Harness harness = await Harness.CreateAsync(Visibility(how));

        Func<Task> discard = () => harness.Events.DiscardDeadLetterAsync(harness.Scope, LetterId, Token);

        (await discard.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(Hidden);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.DiscardDeadLetterAction);
        entry.Target.Should().Be(LetterId.ToString());
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(Hidden);
        entry.Capability.Should().Be(nameof(StudioCapability.ManageDeadLetters));
    }

    [Theory]
    [MemberData(nameof(Hiding))]
    public async Task Rewinding_onto_a_hidden_dead_letter_is_refused_and_audited_as_refused(string how)
    {
        await using Harness harness = await Harness.CreateAsync(Visibility(how));

        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "Poison", 5, Token);

        (await rewind.Should().ThrowAsync<InvalidOperationException>()).Which.Message.Should().Be(Hidden);

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Action.Should().Be(EventDataService.RewindSubscriptionAction);
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().Be(Hidden, "refused before the projection name, the reach or the daemon is asked");
    }

    /// <summary>A delegate that throws is the host's fault: the screen fails closed, and the log says why.</summary>
    [Fact]
    public async Task A_visibility_delegate_that_throws_is_logged_once_and_hides_the_type()
    {
        await using Harness harness = await Harness.CreateAsync(Visibility("throws"));

        await harness.Events.CountDeadLettersAsync(harness.Scope, Token);
        await harness.Events.CountDeadLettersAsync(harness.Scope, Token);

        List<CapturedLogEntry> logged = [.. harness.Logs.Entries.Where(static x => x.EventId.Id == 9217)];
        logged.Should().HaveCount(2, "once per ask");
        logged[0].Level.Should().Be(LogLevel.Warning, "the first in its window");
        logged[0].Message.Should().Contain("IsDocumentTypeVisible").And.Contain("DeadLetterEvent");
        logged[1].Level.Should().Be(LogLevel.Debug, "the badge polls, so the rest of the window is Debug");
    }

    /// <summary>
    /// The CL1 review: the refusal used to name the alias the store gives the type, and a host that re-aliased the type it
    /// hides had that alias printed. The sentence is constant, so it names neither the alias nor the table.
    /// </summary>
    [Fact]
    public async Task The_refusal_never_names_a_hidden_types_alias()
    {
        await using Harness harness = await Harness.CreateAsync(
            static type => type != typeof(DeadLetterEvent),
            static options => options.Schema.For<DeadLetterEvent>().DocumentAlias("secret_failures"));

        (await harness.Events.ListDeadLettersAsync(harness.Scope, new DeadLetterQuery(), Token)).IsHidden.Should().BeTrue();

        Func<Task> discard = () => harness.Events.DiscardDeadLetterAsync(harness.Scope, LetterId, Token);
        (await discard.Should().ThrowAsync<InvalidOperationException>()).Which.Message
            .Should().Be(Hidden).And.NotContain("secret_failures").And.NotContain("mt_doc_");

        harness.Ring.GetLatest().Should().ContainSingle().Which.Message.Should().NotContain("secret_failures");
    }

    /// <summary>
    /// The positive controls: a delegate that shows the type lets every path through to the database - which here does
    /// not answer, so each fails, but not with the gate's sentence and, for a write, not before the session.
    /// </summary>
    [Fact]
    public async Task A_visible_dead_letter_type_reaches_the_database()
    {
        await using Harness harness = await Harness.CreateAsync(static _ => true);

        DeadLetterPage page = await harness.Events.ListDeadLettersAsync(harness.Scope, new DeadLetterQuery(), Token);
        page.IsHidden.Should().BeFalse();
        page.Error.Should().NotBeNull("the host does not answer");
        page.Error!.Message.Should().NotBe(Hidden);

        Func<Task> discard = () => harness.Events.DiscardDeadLetterAsync(harness.Scope, LetterId, Token);
        await discard.Should().ThrowAsync<Exception>();

        StudioActionLogEntry entry = harness.Ring.GetLatest().Should().ContainSingle().Subject;
        entry.Succeeded.Should().BeFalse();
        entry.Message.Should().NotBe(Hidden, "it failed on the way to the table, after the gate had let it through");

        Func<Task> rewind = () => harness.Events.RewindSubscriptionAsync(harness.Scope, "NoSuchProjection", 5, Token);
        (await rewind.Should().ThrowAsync<KeyNotFoundException>()).Which.Message.Should().Contain("NoSuchProjection",
            "past the gate, the rewind asks whether the projection exists");
    }

    [Fact]
    public void The_gate_fails_closed_and_hands_back_what_the_delegate_threw()
    {
        DeadLetterVisibility.IsVisible(null, out Exception? none).Should().BeTrue("no delegate shows everything");
        none.Should().BeNull();

        DeadLetterVisibility.IsVisible(static type => type != typeof(DeadLetterEvent), out Exception? hidden).Should().BeFalse();
        hidden.Should().BeNull("hiding is an answer, not a failure");

        InvalidOperationException boom = new("the host's delegate is broken");
        DeadLetterVisibility.IsVisible(_ => throw boom, out Exception? thrown).Should().BeFalse("a delegate that throws has not said yes");
        thrown.Should().BeSameAs(boom);
    }

    private static Func<Type, bool> Visibility(string how) => how switch
    {
        "hides" => static type => type != typeof(DeadLetterEvent),
        "throws" => static type => type == typeof(DeadLetterEvent) ? throw new InvalidOperationException("the host's delegate is broken") : true,
        _ => throw new ArgumentOutOfRangeException(nameof(how), how, null),
    };

    /// <summary>A studio over a store with an active event store that is never connected to.</summary>
    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly IServiceScope scope;

        private Harness(ServiceProvider provider, CapturingLoggerProvider logs, StudioScope studioScope)
        {
            this.provider = provider;
            scope = provider.CreateScope();
            Logs = logs;
            Scope = studioScope;
        }

        public IEventDataService Events => scope.ServiceProvider.GetRequiredService<IEventDataService>();

        public StudioActionLogService Ring => provider.GetRequiredService<StudioActionLogService>();

        public CapturingLoggerProvider Logs { get; }

        public StudioScope Scope { get; }

        public static async Task<Harness> CreateAsync(Func<Type, bool> isVisible, Action<StoreOptions>? configure = null)
        {
            var users = new TestAuthenticationStateProvider();
            users.SignIn("tester");

            var logs = new CapturingLoggerProvider();

            var services = new ServiceCollection();
            services.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
            services.AddAuthorization();
            services.AddMarten(options =>
            {
                options.Connection(Unreachable);
                options.DatabaseSchemaName = "cl1_visibility";

                // An event type is what makes the event store active, and with it Marten's DeadLetterEvent mapping.
                options.Events.AddEventType<NothingHappened>();
                configure?.Invoke(options);
            });

            services.AddMartenStudio(options =>
            {
                options.Capabilities = MartenStudioCapabilities.All();
                options.IsDocumentTypeVisible = isVisible;
                options.QueryTimeout = TimeSpan.FromSeconds(5);
            });

            services.AddScoped<AuthenticationStateProvider>(_ => users);

            ServiceProvider provider = services.BuildServiceProvider();

            IReadOnlyList<IMartenDatabase> databases = await provider.GetRequiredService<IDocumentStore>().Storage.AllDatabases();

            return new Harness(provider, logs, new StudioScope(MartenStoreRegistry.DefaultStoreKey, databases[0].Id.Identity, null));
        }

        public async ValueTask DisposeAsync()
        {
            scope.Dispose();
            await provider.DisposeAsync();
        }
    }

    /// <summary>An event type, so the event store is active.</summary>
    /// <param name="What">Nothing in particular.</param>
    public sealed record NothingHappened(string What);
}
