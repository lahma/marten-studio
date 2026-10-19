using JasperFx;
using JasperFx.Events.Daemon;

using Marten;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Events;

using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Npgsql;

namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// DL-fix: on a store whose dead letters are soft-deleted, the navigation badge counts exactly the rows the dead-letter
/// list shows.
/// </summary>
/// <remarks>
/// <c>Policies.AllDocumentsSoftDeleted()</c> reaches <c>DeadLetterEvent</c>, so a Discard there flags the row
/// <c>mt_deleted</c> rather than removing it. The list left such rows out, as Marten's own query does; the badge counted
/// the whole table, so after a Discard it kept counting a dead letter its page no longer listed. Both now read through
/// one predicate (<c>DeadLetterQueries.LiveRows</c>).
/// </remarks>
public class DeadLetterSoftDeleteLiveTests(PostgresFixture fixture)
{
    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [PostgresFact]
    public async Task After_a_discard_under_soft_delete_the_badge_counts_the_rows_the_list_shows()
    {
        string schema = await fixture.CreateSchemaAsync("dl_soft_delete", Token);

        await using ServiceProvider provider = Build(schema);
        IDocumentStore store = provider.GetRequiredService<IDocumentStore>();

        DeadLetterEvent[] letters = [Letter("first", 1), Letter("second", 2), Letter("third", 3)];

        await store.Storage.ApplyAllConfiguredChangesToDatabaseAsync();

        await using (IDocumentSession session = store.LightweightSession())
        {
            foreach (DeadLetterEvent letter in letters)
            {
                session.Store(letter);
            }

            await session.SaveChangesAsync(Token);
        }

        await using AsyncServiceScope scope = provider.CreateAsyncScope();
        IEventDataService events = scope.ServiceProvider.GetRequiredService<IEventDataService>();

        (await events.CountDeadLettersAsync(Scope, Token)).Should().Be(3);

        await events.DiscardDeadLetterAsync(Scope, letters[1].Id, Token);

        DeadLetterPage page = await events.ListDeadLettersAsync(Scope, new DeadLetterQuery(), Token);
        long? count = await events.CountDeadLettersAsync(Scope, Token);

        page.Error.Should().BeNull();
        page.Rows.Select(static x => x.Id).Should().BeEquivalentTo([letters[0].Id, letters[2].Id]);
        count.Should().Be(page.Rows.Count, "the badge counts exactly the rows the page it links to shows");

        (await IsSoftDeletedAsync(schema, letters[1].Id)).Should().BeTrue(
            "the discard was a soft delete - the row is still in the table, which is the case the badge used to count");
    }

    private ServiceProvider Build(string schema)
    {
        var services = new ServiceCollection();

        services.AddLogging(static builder => builder.SetMinimumLevel(LogLevel.Warning));
        services.AddAuthorization();
        services.AddSingleton<AuthenticationStateProvider, StubAuthenticationStateProvider>();

        services.AddMarten(options =>
        {
            options.Connection(fixture.ConnectionString);
            options.DatabaseSchemaName = schema;
            options.Events.DatabaseSchemaName = schema;
            options.AutoCreateSchemaObjects = AutoCreate.CreateOrUpdate;

            options.Events.AddEventType<SoftDeleteHappened>();
            options.Policies.AllDocumentsSoftDeleted();
        });

        services.AddMartenStudio(static options =>
        {
            options.AuthorizationPolicy = null;
            options.DiscoverTenantIds = false;
            options.Capabilities = MartenStudioCapabilities.All();
        });

        return services.BuildServiceProvider();
    }

    private async Task<bool> IsSoftDeletedAsync(string schema, Guid id)
    {
        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync(Token);

        await using var command = new NpgsqlCommand(
            "select mt_deleted from " + SqlIdentifier.Qualify(schema, EventTableInfo.DeadLetterTable) + " where id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);

        return await command.ExecuteScalarAsync(Token) is true;
    }

    private static DeadLetterEvent Letter(string message, long sequence) => new()
    {
        Id = Guid.CreateVersion7(),
        ProjectionName = "Ledger",
        ShardName = "All",
        Timestamp = DateTimeOffset.UtcNow.AddMinutes(sequence),
        ExceptionType = "System.InvalidOperationException",
        ExceptionMessage = message,
        EventSequence = sequence,
        TenantId = "*DEFAULT*",
    };

    /// <summary>An event type, so the event store - and with it the dead-letter mapping - is active.</summary>
    /// <param name="What">Something.</param>
    public sealed record SoftDeleteHappened(string What);
}
