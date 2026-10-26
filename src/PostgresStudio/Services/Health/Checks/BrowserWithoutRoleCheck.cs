namespace PostgresStudio.Services.Health.Checks;

/// <summary>
/// K06: the database browser is on and may show every schema (<c>BrowsableSchemas</c> contains <c>"*"</c>), and no
/// <c>SqlConsoleRole</c> narrows what it reads - event 9230's condition, as a finding (D27).
/// </summary>
/// <remarks>
/// <para>
/// <b>Why it matters.</b> <c>"*"</c> admits every schema the reading role has <c>USAGE</c> on, and without
/// <c>SqlConsoleRole</c> the reading role is the data source's own login: every table it may select from is browsable, row by
/// row, by anyone granted <c>BrowseDatabase</c>. <c>BrowsableSchemas</c> limits what the screens show; the role is the
/// boundary.
/// </para>
/// <para>
/// <b>9230 is the start-up log line, this is the page.</b> The log says it once when the host starts, whether or not the
/// capability is on; the finding is raised only while <c>BrowseDatabase</c> is on, because only then does any visitor read
/// through it. <c>ReadOnly</c> mutes it.
/// </para>
/// </remarks>
[HealthCheck("K06", HealthArea.Configuration, HealthCostTier.InProcess, "Database browser open to every schema without a narrowed role", Gate = HealthGate.None)]
internal sealed class BrowserWithoutRoleCheck : HealthCheck<StudioLintFacts>
{
    /// <inheritdoc />
    public override Task<StudioLintFacts> GatherAsync(HealthCheckContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        return Task.FromResult(StudioLintFacts.Of(context.Settings));
    }

    /// <inheritdoc />
    public override HealthVerdict Evaluate(StudioLintFacts facts, HealthEvaluation evaluation)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(evaluation);

        string browse = facts.OptionName("Capabilities.BrowseDatabase");
        string schemas = facts.OptionName("BrowsableSchemas");

        if (facts.ReadOnly)
        {
            return HealthVerdict.Quiet(facts.OptionName("ReadOnly") + " is true, so the database browser reads nothing past " + evaluation.Text.SourcePossessive + " own structure.");
        }

        if (!facts.IsEnabled(StudioCapability.BrowseDatabase))
        {
            return HealthVerdict.Quiet("The database browser reads no rows or definitions (" + browse + " is false).");
        }

        if (!facts.BrowsesEverySchema)
        {
            return HealthVerdict.Quiet(schemas + " names its schemas exactly, so the browser shows only those.");
        }

        if (facts.SqlConsoleRole)
        {
            return HealthVerdict.Quiet(schemas + " contains \"*\", and " + facts.OptionName("SqlConsoleRole") + " narrows what the browser reads.");
        }

        return HealthVerdict.Of(new HealthFinding
        {
            Code = "K06",
            Severity = HealthSeverity.Warning,
            Title = "The database browser may show every schema, read as " + evaluation.Text.SourcePossessive + " own Postgres role",
            WhyItMatters =
                schemas + " contains \"*\" - every schema the reading role may use, but Postgres' own and extensions' - and "
                + facts.OptionName("SqlConsoleRole") + " is not set, so the reading role is the one " + SourceWords.DataSource(evaluation.Text)
                + " logs in as. Every table that role may select from is browsable, row by row, by anyone granted "
                + "BrowseDatabase. " + schemas + " limits what the screens show; the role they read as is the boundary (event 9230 says "
                + "the same at start-up).",
            Evidence =
            [
                new("Capability", browse + " = true"),
                new("BrowsableSchemas", "contains \"*\""),
                new("SqlConsoleRole", "not set"),
            ],
            Caveat = "Read from this process's " + facts.OptionsTypeName + ".",
            Remedy = facts.SqlConsoleRoleRemedy(),
        });
    }
}
