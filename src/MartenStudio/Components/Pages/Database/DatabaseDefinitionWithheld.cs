using MartenStudio.Services.Database;

namespace MartenStudio.Components.Pages.Database;

/// <summary>
/// Why a definition the service already marked unavailable is not shown - worked out from what the page was
/// handed, so the page never asks for a definition it has been told it will not get.
/// </summary>
/// <remarks>
/// <para>
/// <b>Asking anyway was the bug.</b> Routines, triggers and views carry <c>DefinitionAvailable</c>, the
/// gate's answer given in advance. A grid that ignored it and fetched on every click turned an ordinary
/// click on a function in the store's own schema, with the gate shut, into a refused
/// <c>BrowseDatabase</c> read: an audit entry and a 9202 or 9203 security Warning, for a control the page
/// itself offered. Those events are for a client driving a control that was never drawn (plan §6a), so the
/// page must not produce them - it draws the sentence here instead, and the Schema screen's Functions tab
/// does the same.
/// </para>
/// <para>
/// <b>The service stays the enforcement.</b> This only chooses words. Whatever it says, a definition read
/// still goes through <c>IDatabaseObjectService.GetDefinitionAsync</c>, which refuses and audits on its own;
/// and nothing here names anything the page did not already show - the object's own schema, which is
/// visible, and the names of the options that would open the gate.
/// </para>
/// </remarks>
internal static class DatabaseDefinitionWithheld
{
    /// <summary>A trigger whose function is in a schema this visitor is not shown.</summary>
    public const string TriggerFunctionWithheld =
        "Its function is in a schema this studio does not show you, and its definition names that function, " +
        "so the definition is not shown either.";

    /// <summary>A view that reads more than the gate checks in one go.</summary>
    public const string ViewTooWideToCheck =
        "This view reads more than the studio checks in one go, so it cannot say that nothing under it is " +
        "hidden or withheld, and its query is not shown.";

    /// <summary>What is said when none of the particular reasons applies.</summary>
    public const string NotShown = "This studio does not show you this definition.";

    /// <summary>
    /// What a grid's expander says to a screen reader, before the object's name: whether it shows or hides
    /// the definition, or the reason there is none.
    /// </summary>
    /// <param name="open">Whether the row is open.</param>
    /// <param name="available">Whether the definition may be read.</param>
    public static string ExpanderLabel(bool open, bool available) => (open, available) switch
    {
        (true, true) => "Hide the definition of ",
        (false, true) => "Show the definition of ",
        (true, false) => "Hide why there is no definition of ",
        _ => "Show why there is no definition of ",
    };

    /// <summary>The sentence for one object in a list or on its detail page.</summary>
    /// <param name="item">The object, whose <c>DefinitionAvailable</c> is <see langword="false" />.</param>
    /// <param name="access">The gate as the service described it with the list or the detail, when known.</param>
    /// <param name="configuredEntries"><see cref="MartenStudioOptions.BrowsableSchemas" />, for the per-schema sentence.</param>
    public static string For(
        DatabaseObjectSummary item,
        DatabaseAccessState? access,
        IEnumerable<string> configuredEntries)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(configuredEntries);

        if (item.Kind == DatabaseObjectKind.Aggregate)
        {
            return DatabaseObjectService.AggregateHasNoBody;
        }

        if (item is DatabaseTriggerSummary { FunctionSchema: null })
        {
            return TriggerFunctionWithheld;
        }

        // The gate itself, when it is shut: the sentence names the option or the policy.
        if (access is { IsOpen: false, Denial: { Length: > 0 } denial })
        {
            return denial;
        }

        // Open, but not for this schema - the store's own, say, with BrowsableSchemas not listing it.
        if (access is not null && !access.BrowsableSchemas.Contains(item.Schema, StringComparer.Ordinal))
        {
            return DatabaseGate.SchemaDenial(item.Schema, [.. configuredEntries]);
        }

        // A view whose query would name what it reads: a hidden type's table, a withheld schema, a function.
        if (item is DatabaseRelationSummary { Rows: { Allowed: false, Refusal: DatabaseRefusal.HiddenDependency or DatabaseRefusal.WithheldDependency, Reason: { Length: > 0 } reason } })
        {
            return reason;
        }

        return item is DatabaseRelationSummary ? ViewTooWideToCheck : NotShown;
    }
}
