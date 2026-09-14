using System.Diagnostics;

using Marten;

using MartenStudio.Services;

using MartenStudio.Tests.Conventions;

namespace MartenStudio.Tests.Services;

/// <summary>
/// The two things the Overview page reads that a page load must never pay for: the store's schema names,
/// and the Postgres version.
/// </summary>
public class StoreInfoServiceTest
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    // -------------------------------------------------------------------------------------------
    // Schema names come from configuration, never from a migration
    // -------------------------------------------------------------------------------------------

    private sealed class SchemaSample
    {
        public Guid Id { get; set; }
    }

    private sealed class OtherSchemaSample
    {
        public Guid Id { get; set; }
    }

    /// <summary>
    /// A configured <c>StoreOptions</c>, read through the interface the service sees. Nothing here opens
    /// a connection: the schema names are configuration, which is the whole point.
    /// </summary>
    private static StoreOptions Configure(Action<StoreOptions> configure)
    {
        StoreOptions options = new();
        options.Connection("Host=localhost;Database=nowhere;Username=nobody");
        configure(options);
        return options;
    }

    [Fact]
    public void The_schema_names_are_the_stores_own_the_event_stores_and_every_document_types()
    {
        IReadOnlyStoreOptions options = Configure(x =>
        {
            x.DatabaseSchemaName = "studio_sample";
            x.Events.DatabaseSchemaName = "studio_sample_events";
            x.Schema.For<SchemaSample>();
            x.Schema.For<OtherSchemaSample>().DatabaseSchemaName("studio_sample_other");
        });

        StoreInfoService.SchemaNames(options).Should().Equal(
            "studio_sample",
            "studio_sample_events",
            "studio_sample_other");
    }

    [Fact]
    public void The_schema_names_are_de_duplicated_and_ordered()
    {
        IReadOnlyStoreOptions options = Configure(x =>
        {
            x.DatabaseSchemaName = "zeta";
            x.Events.DatabaseSchemaName = "zeta";
            x.Schema.For<SchemaSample>().DatabaseSchemaName("alpha");
        });

        StoreInfoService.SchemaNames(options).Should().Equal("alpha", "zeta");
    }

    /// <summary>
    /// <c>IMartenStorage.AllSchemaNames()</c> is not a read: it goes through <c>AllObjects</c> →
    /// <c>BuildFeatureSchemas</c> → <c>MartenDatabase.Sequences</c> → <c>resetSequences</c> →
    /// <c>executeMigration</c>, so calling it from the Overview ran a migration against the host's
    /// database every time somebody looked at the page — and against the default host and port rather
    /// than the configured one, which is how it was noticed. The replacement is pure configuration, and
    /// this is the guard that keeps it that way, because the call reads like an innocent getter.
    /// </summary>
    [Theory]
    [InlineData("AllSchemaNames(")]
    [InlineData("AllObjects(")]
    [InlineData("CreateMigrationAsync(")]
    [InlineData("ApplyAllConfiguredChangesToDatabaseAsync(")]
    public void The_overview_service_never_reaches_for_a_migration(string forbidden)
    {
        string source = File.ReadAllText(
            RepositoryRoot.Combine("src", "MartenStudio", "Services", "StoreInfoService.cs"));

        // The explanation of why this is forbidden names the call, so only code lines count.
        IEnumerable<string> code = source
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n')
            .Where(static line => !line.TrimStart().StartsWith("///", StringComparison.Ordinal)
                && !line.TrimStart().StartsWith("//", StringComparison.Ordinal));

        string.Join('\n', code).Should().NotContain(
            forbidden,
            $"'{forbidden}' opens a connection and can apply schema changes; the Overview is a page someone looks at");
    }

    // -------------------------------------------------------------------------------------------
    // The Postgres version is bounded
    // -------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_postgres_version_is_read_when_it_answers()
    {
        string? version = await StoreInfoService.ReadPostgresVersionAsync(
            static () => new Version(17, 4),
            TimeSpan.FromSeconds(30),
            Token);

        version.Should().Be("17.4");
    }

    /// <summary>
    /// A <c>Task.Run</c> over Npgsql's synchronous version call cannot be cancelled: a Postgres behind a
    /// firewall that drops packets rather than refusing them keeps that thread blocked for as long as the
    /// connect timeout allows, and the Overview page waited with it. The bound turns that into the
    /// "unknown" tile the page already knows how to draw.
    /// </summary>
    [Fact]
    public async Task A_version_read_that_does_not_come_back_in_time_is_unknown()
    {
        using ManualResetEventSlim release = new(initialState: false);
        try
        {
            Stopwatch elapsed = Stopwatch.StartNew();

            string? version = await StoreInfoService.ReadPostgresVersionAsync(
                () =>
                {
                    release.Wait(TimeSpan.FromSeconds(30));
                    return new Version(17, 4);
                },
                TimeSpan.FromMilliseconds(100),
                Token);

            elapsed.Stop();

            version.Should().BeNull("a version that cannot be reported is a value, not an exception");
            elapsed.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20), "the wait is bounded by the timeout, not by the read");
        }
        finally
        {
            release.Set();
        }
    }

    [Fact]
    public async Task A_version_read_that_throws_is_unknown_rather_than_a_broken_page()
    {
        string? version = await StoreInfoService.ReadPostgresVersionAsync(
            static () => throw new InvalidOperationException("no connection"),
            TimeSpan.FromSeconds(30),
            Token);

        version.Should().BeNull();
    }
}
