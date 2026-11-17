using DataStudio.Internal;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Postgres.Services.Health.Checks;

/// <summary>What K07 reads.</summary>
/// <param name="Options">The studio's capabilities and option names.</param>
/// <param name="ServedAnonymously">
/// Whether the studio's pages carry <c>AllowAnonymous</c> (<see cref="StudioMappedEndpoints.ServedAnonymously" />), or
/// <see langword="null" /> when the mapping is not known to this process.
/// </param>
/// <param name="EnvironmentName">The host's environment, or <see langword="null" />.</param>
internal sealed record AnonymousStudioFacts(StudioLintFacts Options, bool? ServedAnonymously, string? EnvironmentName)
{
    /// <summary>
    /// The capabilities that are on in every other composed module, one entry per module that has any (a module added beside the database, whose own are in
    /// <see cref="Options" />). Empty when there is none, and for a shell whose switch is read-only: the shell's <c>ReadOnly</c> turns every module off,
    /// and a module's own turns only that module off.
    /// </summary>
    public IReadOnlyList<StudioModuleCapabilitiesOn> OtherModules { get; init; } = [];

    /// <summary>Every capability that is on, in any module: the database's own by name, another module's by name and module.</summary>
    public IReadOnlyList<string> EnabledEverywhere =>
    [
        .. Options.Enabled.Select(static x => x.ToString()),
        .. OtherModules.SelectMany(static module => module.Enabled.Select(name => name + " (" + module.Module + ")")),
    ];

    /// <summary>Whether the host says this is Production.</summary>
    public bool IsProduction => HostEnvironmentName.IsProduction(EnvironmentName);
}

/// <summary>One module's capabilities that are on, as K07 names them: the module's product name, and each capability's name.</summary>
/// <param name="Module">The module's product name.</param>
/// <param name="Enabled">The names of its capabilities that are on.</param>
internal sealed record StudioModuleCapabilitiesOn(string Module, IReadOnlyList<string> Enabled);

/// <summary>
/// K07: the studio's pages are served to anybody (<c>AllowAnonymous</c>), and a capability is on.
/// </summary>
/// <remarks>
/// <para>
/// <b>The mapping, observed.</b> <see cref="StudioMappedEndpoints.ServedAnonymously" /> is read off the finished endpoints by
/// the start-up guard - the caller's <c>AllowAnonymous()</c>, a group's, or one the policy did not override - and is the same
/// fact the layout's banner draws. A studio served anonymously with every capability off - in every module, the database's own and
/// each other module's (<see cref="AnonymousStudioFacts.OtherModules" />) - is a read-only browser, which the banner already says; with one on,
/// anyone who can reach the address can use it (D4, D5). A module's own <c>ReadOnly</c> turns off that module only.
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
        return Task.FromResult(
            new AnonymousStudioFacts(StudioLintFacts.Of(context.Settings), anonymous, HostEnvironmentName.Of(context))
            {
                OtherModules = OtherModulesOn(context.Services),
            });
    }

    /// <summary>
    /// What every other composed module has on, read through the shell's descriptor: each module's own capability groups, live, as the chip lists
    /// them. The shell's <c>ReadOnly</c> turns all of it off; the database module's <c>ReadOnly</c> does not reach another module, so a host that
    /// set it and left another module's capabilities on is not quiet.
    /// </summary>
    /// <param name="services">The host's container.</param>
    internal static List<StudioModuleCapabilitiesOn> OtherModulesOn(IServiceProvider services)
    {
        StudioShellDescriptor? shell = services.GetService<StudioShellDescriptor>();
        if (shell is null || services.GetService<IShellSettings>() is not { ReadOnly: false })
        {
            return [];
        }

        string? own = services.GetService<StudioDescriptor>()?.Key;

        return
        [
            .. shell.Modules
                .Where(module => !string.Equals(module.Key, own, StringComparison.Ordinal))
                .Select(static module => new StudioModuleCapabilitiesOn(
                    module.ProductName,
                    [.. module.CapabilityGroups.SelectMany(static group => group.Capabilities).Where(static capability => capability.Configured).Select(static capability => capability.Name)]))
                .Where(static module => module.Enabled.Count > 0),
        ];
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

        IReadOnlyList<string> everything = facts.EnabledEverywhere;

        if (everything.Count == 0)
        {
            return HealthVerdict.Quiet(
                "The studio is served to anyone (AllowAnonymous), with every capability off"
                + (options.ReadOnly ? " (" + options.ReadOnlyOptionName + ")" : string.Empty)
                + ": it browses and changes nothing.");
        }

        string capabilities = string.Join(", ", everything);

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K07",
            Severity = facts.IsProduction ? HealthSeverity.Critical : HealthSeverity.Warning,
            Title = "The studio is open to anonymous visitors, with capabilities on",
            WhyItMatters =
                "The studio's pages carry AllowAnonymous, so nobody is asked who they are, and " + capabilities + " "
                + (everything.Count == 1 ? "is" : "are") + " on: anyone who can reach the address can use "
                + (everything.Count == 1 ? "it" : "them") + " - "
                + (options.IsEnabled(StudioCapability.RunSql) ? "read every row the console's role can, " : string.Empty)
                + "and every write a capability allows. A " + evaluation.Text.ScopePolicyNoun + " or write policy still decides what "
                + "an anonymous visitor reaches, if the host set one; with none, nothing does."
                + (facts.OtherModules.Count > 0 ? " The policies of " + string.Join(" and ", facts.OtherModules.Select(static x => x.Module)) + " decide what it reaches of its own." : string.Empty),
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
                + "app." + StudioLintFacts.MapMethod + "().RequireAuthorization();\n\n"
                + "// ...or keep it anonymous, with every capability off:\n"
                + "options.Capabilities = new " + options.CapabilitiesTypeName + "();",
                "RequireAuthorization() authorizes the studio's pages and its circuit with the host's default policy; "
                + StudioLintFacts.AuthorizationPolicyOptionName + " names another, and authorizes the static assets with them."),
        });
    }
}
