using Microsoft.Extensions.DependencyInjection;

using PostgresStudio.Internal;

namespace PostgresStudio.Services.Health.Checks;

/// <summary>What K07 reads.</summary>
/// <param name="Options">The studio's capabilities and option names.</param>
/// <param name="ServedAnonymously">
/// Whether the studio's pages carry <c>AllowAnonymous</c> (<see cref="StudioMappedEndpoints.ServedAnonymously" />), or
/// <see langword="null" /> when the mapping is not known to this process.
/// </param>
/// <param name="EnvironmentName">The host's environment, or <see langword="null" />.</param>
internal sealed record AnonymousStudioFacts(StudioLintFacts Options, bool? ServedAnonymously, string? EnvironmentName)
{
    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => HostEnvironmentName.IsProduction(EnvironmentName);
}

/// <summary>
/// K07: the studio's pages are served to anybody (<c>AllowAnonymous</c>), and a capability is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mapping, observed.</b> <see cref="StudioMappedEndpoints.ServedAnonymously" /> is read off the finished endpoints by
/// the start-up guard - the caller's <c>AllowAnonymous()</c>, a group's, or one the policy did not override - and is the same
/// fact the layout's banner draws. A studio served anonymously with every capability off is a read-only browser, which the
/// banner already says; with one on, anyone who can reach the address can use it (D4, D5).
/// </para>
/// <para>
/// <b>Critical in Production, Warning anywhere else</b>, the environment read as K01 reads it - a host that names none is
/// "anywhere else". A process that maps no studio, or whose mapping the guard has not observed yet, is quiet and says so.
/// </para>
/// </remarks>
[HealthCheck("K07", HealthArea.Configuration, HealthCostTier.InProcess, "Studio open to anonymous visitors with a capability on", Gate = HealthGate.None)]
internal sealed class AnonymousStudioCheck : HealthCheck<AnonymousStudioFacts>
{
    /// <inheritdoc />
    public override Task<AnonymousStudioFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        bool? anonymous = context.Services.GetService<StudioMappedEndpoints>()?.ServedAnonymously;
        return Task.FromResult(new AnonymousStudioFacts(StudioLintFacts.Of(context.Settings), anonymous, HostEnvironmentName.Of(context)));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(AnonymousStudioFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        StudioLintFacts options = facts.Options;

        if (facts.ServedAnonymously is null)
        {
            return HealthVerdict.Quiet("This process has no record of how the studio was mapped, so whether it is served anonymously is not known.");
        }

        if (facts.ServedAnonymously == false)
        {
            return HealthVerdict.Quiet("The studio's pages are not served anonymously: the mapping or AuthorizationPolicy asks who the visitor is.");
        }

        if (options.Enabled.Count == 0)
        {
            return HealthVerdict.Quiet(
                "The studio is served to anyone (AllowAnonymous), with every capability off"
                + (options.ReadOnly ? " (" + options.OptionName("ReadOnly") + ")" : string.Empty)
                + ": it browses and changes nothing.");
        }

        string capabilities = string.Join(", ", options.Enabled.Select(static x => x.ToString()));

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K07",
            Severity = facts.IsProduction ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = "The studio is open to anonymous visitors, with capabilities on",
            WhyItMatters =
                "The studio's pages carry AllowAnonymous, so nobody is asked who they are, and " + capabilities + " "
                + (options.Enabled.Count == 1 ? "is" : "are") + " on: anyone who can reach the address can use "
                + (options.Enabled.Count == 1 ? "it" : "them") + " - "
                + (options.IsEnabled(StudioCapability.RunSql) ? "read every row the console's role can, " : string.Empty)
                + "and every write a capability allows. A " + evaluation.Text.ScopePolicyNoun + " or write policy still decides what "
                + "an anonymous visitor reaches, if the host set one; with none, nothing does.",
            Evidence =
            [
                new("Mapping", "the studio's pages allow anonymous visitors"),
                new("Capabilities on", capabilities),
                new("Environment", HostEnvironmentName.Describe(facts.EnvironmentName)),
            ],
            Caveat = facts.IsProduction
                ? "Read from the studio's mapped endpoints and this process's " + options.OptionsTypeName + ". This is Production."
                : "Read from the studio's mapped endpoints and this process's " + options.OptionsTypeName + ". Warning rather than Critical: "
                  + "the environment is " + HostEnvironmentName.Describe(facts.EnvironmentName) + ".",
            Remedy = new HealthRemedy(
                "// Ask who the visitor is:\n"
                + "app." + options.MapMethod + "().RequireAuthorization();\n\n"
                + "// ...or keep it anonymous, with every capability off:\n"
                + "options.Capabilities = new " + options.CapabilitiesTypeName + "();",
                "RequireAuthorization() authorizes the studio's pages and its circuit with the host's default policy; "
                + options.OptionName("AuthorizationPolicy") + " names another, and authorizes the static assets with them."),
        });
    }
}
