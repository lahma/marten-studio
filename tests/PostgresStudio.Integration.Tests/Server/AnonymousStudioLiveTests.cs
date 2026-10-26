using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Integration.Tests.Health;
using PostgresStudio.Internal;
using PostgresStudio.Services.Health;

namespace PostgresStudio.Integration.Tests.Server;

/// <summary>
/// K07 on a real web host (W5-S2): Postgres Studio mapped on Kestrel and started, so the start-up guard reads the finished
/// endpoints, then swept - anonymous with a capability on fires, Critical in Production; authorized, it is quiet.
/// </summary>
public class AnonymousStudioLiveTests(PostgresFixture postgres)
{
    [PostgresFact]
    [HealthFires("K07")]
    public async Task K07_fires_Critical_for_an_anonymous_studio_with_a_capability_on_in_Production()
    {
        await using PostureHost host = await PostureHost.ServeAsync(
            postgres.ConnectionString,
            static studio => studio.AllowAnonymous(),
            static options => options.Capabilities.RunSql = true,
            environment: "Production");

        host.Services.GetRequiredService<StudioMappedEndpoints>().ServedAnonymously.Should().BeTrue("the guard read the mapping at start-up");

        HealthFinding finding = PostureHost.Result(await host.SweepAsync(), "K07").Findings.Should().ContainSingle().Subject;
        finding.Severity.Should().Be(HealthSeverity.Critical);
        finding.Evidence.Should().Contain(x => x.Label == "Capabilities on" && x.Value == "RunSql");
        finding.Remedy!.Code.Should().Contain("app.MapPostgresStudio().RequireAuthorization();");
    }

    [PostgresFact]
    public async Task K07_is_a_Warning_outside_Production()
    {
        await using PostureHost host = await PostureHost.ServeAsync(
            postgres.ConnectionString,
            static studio => studio.AllowAnonymous(),
            static options => options.Capabilities.RunSql = true,
            environment: "Development");

        PostureHost.Result(await host.SweepAsync(), "K07").Findings.Should().ContainSingle().Which.Severity.Should().Be(HealthSeverity.Warning);
    }

    [PostgresFact]
    [HealthQuiet("K07")]
    public async Task K07_is_quiet_for_an_authorized_mapping_and_for_an_anonymous_one_with_every_capability_off()
    {
        await using (PostureHost authorized = await PostureHost.ServeAsync(
            postgres.ConnectionString,
            static studio => studio.RequireAuthorization(),
            static options => options.Capabilities.RunSql = true))
        {
            HealthCheckResult result = PostureHost.Result(await authorized.SweepAsync(), "K07");
            result.Outcome.Should().Be(HealthCheckOutcome.Checked);
            result.Findings.Should().BeEmpty();
            result.Note.Should().Contain("not served anonymously");
        }

        await using (PostureHost readOnly = await PostureHost.ServeAsync(
            postgres.ConnectionString,
            static studio => studio.AllowAnonymous()))
        {
            HealthCheckResult result = PostureHost.Result(await readOnly.SweepAsync(), "K07");
            result.Findings.Should().BeEmpty();
            result.Note.Should().Contain("every capability off");
        }
    }
}
