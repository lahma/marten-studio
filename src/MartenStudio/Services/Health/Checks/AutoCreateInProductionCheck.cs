using JasperFx;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using PostgresStudio.Services.Health;

namespace MartenStudio.Services.Health.Checks;

/// <summary>What K01 reads.</summary>
/// <param name="AutoCreate">The database's <c>AutoCreate</c>, which is what runs on first touch.</param>
/// <param name="EnvironmentName">
/// <c>IHostEnvironment.EnvironmentName</c>, or <see langword="null" /> when the host registers none - a value,
/// not an error: the check then says it cannot tell.
/// </param>
internal sealed record AutoCreateFacts(AutoCreate AutoCreate, string? EnvironmentName);

/// <summary>
/// K01: Marten may run DDL on first touch in Production.
/// </summary>
/// <remarks>
/// <para>
/// <c>AutoCreate.All</c> and <c>AutoCreate.CreateOrUpdate</c> both let the first query that reaches an object
/// whose shape differs from the configuration migrate it, there and then, inside that request - and
/// <c>CreateOrUpdate</c> is not additive (AGENTS.md hard rule 14): Weasel's update drops undeclared indexes
/// and extra columns and rewrites changed ones. <c>CreateOnly</c> only creates what is missing, and
/// <c>None</c> nothing; neither is flagged.
/// </para>
/// <para>
/// Tier 0, no I/O: the database's <c>AutoCreate</c> and the host's <c>IHostEnvironment</c>, which a host may
/// not register at all - then the check says so in its status row and finds nothing.
/// </para>
/// </remarks>
[HealthCheck("K01", HealthArea.Configuration, HealthCostTier.InProcess, "AutoCreate in Production", Gate = HealthGate.None)]
internal sealed class AutoCreateInProductionCheck : HealthCheck<AutoCreateFacts>
{
    /// <inheritdoc />
    public override Task<AutoCreateFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        string? environment = context.Services.GetService<IHostEnvironment>()?.EnvironmentName;
        return Task.FromResult(new AutoCreateFacts(context.Database.AutoCreate, environment));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AutoCreateFacts facts, HealthEvaluation evaluation)
    {
        if (string.IsNullOrWhiteSpace(facts.EnvironmentName))
        {
            // Absent, or registered with no name - a container that never ran a host builder answers "".
            return HealthVerdict.Quiet(
                "The host registers no IHostEnvironment that names an environment, so the studio cannot tell whether this is Production and says nothing about AutoCreate.");
        }

        if (!string.Equals(facts.EnvironmentName, Environments.Production, StringComparison.OrdinalIgnoreCase))
        {
            return HealthVerdict.Quiet(
                "The environment is " + facts.EnvironmentName + "; this check is about Production.");
        }

        if (facts.AutoCreate is not (AutoCreate.All or AutoCreate.CreateOrUpdate))
        {
            return HealthVerdict.Quiet("AutoCreate is " + facts.AutoCreate + ", which never alters or drops an existing object.");
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K01",
            Severity = HealthSeverity.Warning,
            Title = "Marten may change this database's schema on first use, in Production",
            WhyItMatters =
                "With AutoCreate." + facts.AutoCreate + ", the first request that touches a document or event table whose shape "
                + "differs from the configuration migrates it there and then, inside that request and as the application's own "
                + "database role. AutoCreate.CreateOrUpdate is not additive: Weasel drops every index on a Marten table that the "
                + "configuration does not declare, drops extra columns and alters changed ones"
                + (facts.AutoCreate == AutoCreate.All
                    ? ", and AutoCreate.All also lets it drop and recreate an object whose change cannot be applied in place. "
                    : ". ")
                + "In Production that is a schema change nobody reviewed, run by whichever request happened to come first.",
            Evidence =
            [
                new HealthEvidence("AutoCreate", facts.AutoCreate.ToString()),
                new HealthEvidence("Environment", facts.EnvironmentName),
                new HealthEvidence("Read from", "IMartenDatabase.AutoCreate (StoreOptions.AutoCreateSchemaObjects) and IHostEnvironment.EnvironmentName"),
            ],
            Caveat =
                "Read from this process's configuration. A host that also applies its schema at deploy time is still exposed: "
                + "the first-use path runs whatever the deploy missed.",
            Remedy = new HealthRemedy(
                """
                // In AddMarten(...): no schema changes on first use.
                opts.AutoCreateSchemaObjects = AutoCreate.None;

                // ...and apply them once, at startup or at deploy:
                builder.Services.AddMarten(opts => { /* ... */ }).ApplyAllDatabaseChangesOnStartup();
                // or, with app.RunJasperFxCommands(args): dotnet run -- db-patch (review), then db-apply
                """,
                "The apply still runs Weasel's CreateOrUpdate, which is not additive either - it drops undeclared indexes and "
                + "extra columns - but it runs once, under Marten's apply lock, at a moment you chose and can review with db-patch, "
                + "rather than inside whichever request touches a table first."),
        });
    }
}
