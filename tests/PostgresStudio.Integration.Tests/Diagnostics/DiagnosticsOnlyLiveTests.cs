using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

namespace PostgresStudio.Integration.Tests.Diagnostics;

/// <summary>
/// <c>AddPostgresDiagnostics()</c> alone, live (0.11-C, issue #29): the engine with no studio registered answers the same
/// findings for one database as the engine a studio registers, and the checks it does not run are the ones that judge a
/// studio's exposure - which a host with no studio has nothing to judge.
/// </summary>
public class DiagnosticsOnlyLiveTests(PostgresFixture postgres)
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <summary>The checks that read a studio's own settings and mapping, so run only where a studio is registered.</summary>
    private static readonly string[] StudioPosture = ["K05", "K06", "K07"];

    [PostgresFact]
    public async Task The_engine_alone_returns_the_same_findings_for_one_database_as_the_engine_a_studio_registers()
    {
        await using OwnedDatabases owned = await OwnedDatabases.CreateAsync(postgres, ["ms_dx_engine_only"], Token);

        // One thing to find, so that the comparison is of something: an index nobody uses, on a table nobody reads.
        await using (NpgsqlConnection seed = new(owned[0]))
        {
            await seed.OpenAsync(Token);
            await using NpgsqlCommand create = new(
                "create table public.events (id bigint primary key, kind int); "
                + "create index events_kind_a on public.events (kind); create index events_kind_b on public.events (kind); "
                + "insert into public.events select g, g % 7 from pg_catalog.generate_series(1, 500) g; analyze public.events",
                seed);
            await create.ExecuteNonQueryAsync(Token);
        }

        // The engine alone: a plain container, with no ASP.NET Core service in it.
        ServiceCollection engine = new();
        engine.AddLogging();
        engine.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
        engine.AddPostgresDiagnostics();

        // The same engine, registered by the studio, over the same database and the same options.
        ServiceCollection studio = new();
        studio.AddLogging();
        studio.AddAuthorization();
        studio.AddSingleton<AuthenticationStateProvider, Nobody>();
        studio.AddSingleton(_ => NpgsqlDataSource.Create(owned[0]));
        studio.AddPostgresStudio();

        await using ServiceProvider engineProvider = engine.BuildServiceProvider(validateScopes: true);
        await using ServiceProvider studioProvider = studio.BuildServiceProvider(validateScopes: true);

        PostgresDiagnosticsSnapshot alone = await ReadAsync(engineProvider);
        PostgresDiagnosticsSnapshot registered = await ReadAsync(studioProvider);

        alone.Outcome.Should().Be(PostgresReadOutcome.Read, alone.Reason);
        registered.Outcome.Should().Be(PostgresReadOutcome.Read, registered.Reason);
        alone.Database.Should().Be(registered.Database);

        // Not vacuous: the database has something to say.
        registered.Findings.Should().NotBeEmpty("a table with two identical indexes has findings");

        alone.Findings.Select(static x => x.Key).Order(StringComparer.Ordinal)
            .Should().Equal(registered.Findings.Select(static x => x.Key).Order(StringComparer.Ordinal), "the same engine reads the same database");

        // Every check but the studio's own posture ones, which a host with no studio has nothing to judge.
        string[] codes = [.. alone.Checks.Select(static x => x.Code)];
        codes.Should().NotIntersectWith(StudioPosture, "the engine alone has no studio whose exposure to judge");
        registered.Checks.Select(static x => x.Code).Where(static x => !StudioPosture.Contains(x, StringComparer.Ordinal))
            .Order(StringComparer.Ordinal)
            .Should().Equal(codes.Order(StringComparer.Ordinal));
        registered.Checks.Select(static x => x.Code).Should().Contain(StudioPosture, "the studio runs them");
    }

    private static async Task<PostgresDiagnosticsSnapshot> ReadAsync(ServiceProvider provider) =>
        await provider.GetRequiredService<IPostgresDiagnostics>()
            .GetSnapshotAsync(new PostgresDatabaseRef("default", ""), PostgresDiagnosticsRequest.FindingsOnly, Token);

    private sealed class Nobody : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() =>
            Task.FromResult(new AuthenticationState(new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())));
    }
}
