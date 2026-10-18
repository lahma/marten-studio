using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.Extensions.DependencyInjection;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// Dead letters, produced the way a real one is produced: an async projection that throws on one event,
/// a daemon that runs it, and Marten's own default continuous error policy - which skips apply errors
/// and records a <c>DeadLetterEvent</c>.
/// </summary>
/// <remarks>
/// <para>
/// The daemon runs once, in <see cref="PoisonStoreFixture" />, before any test here starts - so every
/// test begins from real dead letters and none of them pays for a daemon. The studio code under test
/// never touches a daemon at all: the dead-letter screen only ever reads documents.
/// </para>
/// <para>
/// The fixture is the class's, so these tests share one database. The seed poisons several streams
/// precisely because one of these tests discards a record.
/// </para>
/// </remarks>
public class DeadLetterLiveTests(PoisonStoreFixture fixture) : IClassFixture<PoisonStoreFixture>
{
    private EventsFixture Events => fixture.Events;

    [PostgresFact]
    public async Task A_projection_that_throws_produces_a_dead_letter_the_studio_can_read()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        DeadLetterPage page = await Events.Service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery(), token);

        page.Error.Should().BeNull();
        page.Rows.Should().NotBeEmpty();

        DeadLetterRow row = page.Rows[0];
        row.Id.Should().NotBeEmpty();
        row.ProjectionName.Should().NotBeNullOrWhiteSpace();
        row.ShardName.Should().NotBeNullOrWhiteSpace();
        row.EventSequence.Should().BeGreaterThan(0);
        row.TenantId.Should().NotBeNullOrWhiteSpace();

        // Worth recording, because it is not what you would guess: the two exception fields come from
        // two different exceptions. ExceptionType is the *inner* one the projection threw
        // (InvalidOperationException), while ExceptionMessage is the daemon's ApplyEventException
        // wrapper, which names the event that could not be applied rather than what the projection said
        // about it. That is exactly why the screen puts the offending event's body beside the exception:
        // on its own the message does not say what went wrong.
        row.ExceptionType.Should().Be("InvalidOperationException");
        row.ExceptionMessage.Should().Contain("Failure to apply event");
        row.ExceptionMessage.Should().Contain(row.EventSequence.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// The count the nav badge would use. Zero and "could not count" are different answers, so it is
    /// nullable - and here it is a real number.
    /// </summary>
    [PostgresFact]
    public async Task The_dead_letters_can_be_counted_without_listing_them()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        long? count = await Events.Service.CountDeadLettersAsync(Events.Scope, token);

        count.Should().NotBeNull();
        count.Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task The_offending_event_is_readable_by_its_sequence()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        DeadLetterPage page = await Events.Service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        DeadLetterRow row = page.Rows[0];

        EventRow? offending = await Events.Service.GetEventBySequenceAsync(Events.Scope, row.EventSequence, token);

        offending.Should().NotBeNull();
        offending!.Sequence.Should().Be(row.EventSequence);
        offending.EventType.Should().Be("poison_pill");
        offending.Json.Should().Contain("deliberately unapplicable");
    }

    [PostgresFact]
    public async Task The_filters_narrow_the_list_by_projection_and_by_exception_type()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        IEventDataService service = Events.Service;

        DeadLetterPage all = await service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        string projection = all.Rows[0].ProjectionName;

        DeadLetterPage byProjection = await service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery { ProjectionName = projection }, token);
        DeadLetterPage byOther = await service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery { ProjectionName = "NoSuchProjection" }, token);
        DeadLetterPage byException = await service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery { ExceptionType = "InvalidOperation" }, token);
        DeadLetterPage byOtherException = await service.ListDeadLettersAsync(
            Events.Scope, new DeadLetterQuery { ExceptionType = "NoSuchException" }, token);

        byProjection.Rows.Should().NotBeEmpty();
        byOther.Rows.Should().BeEmpty();
        byException.Rows.Should().NotBeEmpty("the exception type filter is a contains match");
        byOtherException.Rows.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task Discarding_is_refused_at_the_service_when_the_capability_is_off()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        DeadLetterPage page = await Events.Service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        Guid id = page.Rows[0].Id;

        Func<Task> discard = () => Events.ReadOnlyService.DiscardDeadLetterAsync(Events.Scope, id, token);

        (await discard.Should().ThrowAsync<StudioCapabilityDeniedException>())
            .Which.Message.Should().Contain("MartenStudioOptions.Capabilities.ManageDeadLetters");

        DeadLetterPage after = await Events.Service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        after.Rows.Should().Contain(x => x.Id == id, "a refusal must not have changed anything");

        Events.ReadOnlyAudit.GetLatest().Should().Contain(x =>
            x.Action == "Discard dead letter" && x.Target == id.ToString() && !x.Succeeded);
    }

    [PostgresFact]
    public async Task Discarding_deletes_the_record_and_audits_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        IEventDataService service = Events.Service;

        DeadLetterPage before = await service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        Guid id = before.Rows[0].Id;

        await service.DiscardDeadLetterAsync(Events.Scope, id, token);

        DeadLetterPage after = await service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        after.Rows.Should().NotContain(x => x.Id == id);

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Discard dead letter" && x.Target == id.ToString() && x.Succeeded);
    }

    [PostgresFact]
    public async Task Discarding_a_dead_letter_that_is_already_gone_is_not_an_error()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        await Events.Service.DiscardDeadLetterAsync(Events.Scope, Guid.NewGuid(), token);

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Discard dead letter" && x.Succeeded && x.Message == "already gone");
    }

    [PostgresFact]
    public async Task Skipping_the_offending_event_marks_it_and_audits_it()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        IEventDataService service = Events.Service;

        DeadLetterPage page = await service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        long sequence = page.Rows[0].EventSequence;

        await service.SkipEventAsync(Events.Scope, sequence, token);

        EventRow? offending = await service.GetEventBySequenceAsync(Events.Scope, sequence, token);
        offending!.IsSkipped.Should().BeTrue();

        Events.Audit.GetLatest().Should().Contain(x =>
            x.Action == "Skip event" && x.Succeeded && x.Message == "marked as skipped");
    }

    /// <summary>
    /// CL1 item 4, live: a host that hides <c>DeadLetterEvent</c> gets no dead-letter list, no number on the badge and
    /// no discard - over real dead letters the daemon recorded, which a studio that shows the type still lists, and
    /// which the refused discard leaves exactly as they were.
    /// </summary>
    [PostgresFact]
    public async Task A_host_that_hides_DeadLetterEvent_gets_no_list_no_count_and_no_discard()
    {
        CancellationToken token = TestContext.Current.CancellationToken;

        DeadLetterPage shown = await Events.Service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        shown.Rows.Should().NotBeEmpty("the premise: the daemon recorded dead letters, and a studio that shows the type lists them");
        Guid id = shown.Rows[0].Id;
        long before = await CountRowsAsync(token);

        await using ServiceProvider provider = StudioHiding(Events.Store);
        await using AsyncServiceScope circuit = provider.CreateAsyncScope();
        IEventDataService hidden = circuit.ServiceProvider.GetRequiredService<IEventDataService>();

        DeadLetterPage page = await hidden.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token);
        page.IsHidden.Should().BeTrue("the page says the dead letters are hidden, and reads none of them");
        page.Rows.Should().BeEmpty();
        page.Error.Should().BeNull();

        (await hidden.CountDeadLettersAsync(Events.Scope, token)).Should().BeNull("a hidden type has no number on the badge");

        Func<Task> discard = () => hidden.DiscardDeadLetterAsync(Events.Scope, id, token);
        (await discard.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Dead letters are hidden by MartenStudioOptions.IsDocumentTypeVisible.");

        StudioActionLogEntry refused = provider.GetRequiredService<StudioActionLogService>().GetLatest().Should().ContainSingle().Subject;
        refused.Action.Should().Be("Discard dead letter");
        refused.Target.Should().Be(id.ToString());
        refused.Succeeded.Should().BeFalse("audited as refused");

        Func<Task> rewind = () => hidden.RewindSubscriptionAsync(Events.Scope, shown.Rows[0].ProjectionName, shown.Rows[0].EventSequence, token);
        (await rewind.Should().ThrowAsync<InvalidOperationException>())
            .Which.Message.Should().Be("Dead letters are hidden by MartenStudioOptions.IsDocumentTypeVisible.", "refused before the daemon is asked");
        provider.GetRequiredService<StudioActionLogService>().GetLatest().Should().HaveCount(2)
            .And.OnlyContain(static x => !x.Succeeded);

        (await CountRowsAsync(token)).Should().Be(before, "the refusals deleted nothing");
        (await Events.Service.ListDeadLettersAsync(Events.Scope, new DeadLetterQuery(), token)).Rows
            .Should().Contain(x => x.Id == id, "and the studio that shows the type still lists it");
    }

    /// <summary>Every dead letter in the table, counted raw - what the refusal must not have changed.</summary>
    private async Task<long> CountRowsAsync(CancellationToken token)
    {
        await using Npgsql.NpgsqlConnection connection = await Events.OpenAsync(token);
        await using Npgsql.NpgsqlCommand command = new(
            "select count(*) from " + MartenStudio.Internal.Sql.SqlIdentifier.Qualify(Events.Schema, "mt_doc_deadletterevent"), connection);

        return (long) (await command.ExecuteScalarAsync(token))!;
    }

    /// <summary>A studio over the same store whose host hides <c>DeadLetterEvent</c>, with every capability on.</summary>
    private static ServiceProvider StudioHiding(Marten.IDocumentStore store)
    {
        var services = new ServiceCollection();

        services.AddLogging();
        services.AddAuthorization();
        services.AddSingleton<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, StubAuthenticationStateProvider>();
        services.AddSingleton(store);

        services.AddMartenStudio(options =>
        {
            options.Capabilities = MartenStudioCapabilities.All();
            options.DiscoverTenantIds = false;
            options.IsDocumentTypeVisible = static type => type != typeof(JasperFx.Events.Daemon.DeadLetterEvent);
        });

        return services.BuildServiceProvider();
    }
}
