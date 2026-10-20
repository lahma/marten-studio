using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services;

/// <summary>
/// The capabilities - the mutating operations and the reads authorized like writes - as an enum, so a
/// service method can name the one it is about and the guard can answer for it. Every studio's options
/// map the members it offers onto a public flag of the same name (<see cref="IStudioSettings.IsConfigured" />).
/// </summary>
/// <remarks>
/// New members go at the end: the numeric values are not persisted anywhere, but the declaration order is
/// the order the capability chip lists them in.
/// </remarks>
internal enum StudioCapability
{
    /// <summary>Edit a document's JSON and save it through the store's serializer and session.</summary>
    EditDocuments,

    /// <summary>Delete, soft-delete or undelete documents.</summary>
    DeleteDocuments,

    /// <summary>Archive event streams.</summary>
    ArchiveStreams,

    /// <summary>Discard dead-letter events, mark events as skipped, or rewind a subscription to a sequence.</summary>
    ManageDeadLetters,

    /// <summary>Start and stop projection agents and the async daemon hosted in this process.</summary>
    ControlDaemon,

    /// <summary>Rebuild projections.</summary>
    RebuildProjections,

    /// <summary>Advance the high-water mark or correct projection progression in the database.</summary>
    CorrectProgression,

    /// <summary>Apply pending schema migrations to the database.</summary>
    ApplySchemaChanges,

    /// <summary>Run read-only SQL from the query page, inside a read-only transaction under a statement timeout.</summary>
    RunSql,

    /// <summary>Read the rows and the definitions of the database objects beyond a source's own (D27).</summary>
    BrowseDatabase,

    /// <summary>See other sessions' process ids, roles, client addresses and statements (D29's Activity gate).</summary>
    ViewDatabaseActivity,

    /// <summary>Compact event streams.</summary>
    CompactStreams,

    /// <summary>Run LINQ queries from the Query page (D28).</summary>
    RunLinqQueries
}

/// <summary>Why a capability was refused.</summary>
internal enum CapabilityDenialReason
{
    /// <summary>The studio's master switch (<see cref="IStudioSettings.ReadOnly" />) is on, so every capability is off.</summary>
    ReadOnly,

    /// <summary>The individual capability is off.</summary>
    Disabled
}

/// <summary>
/// What a mutating service method throws when the capability it needs is not enabled.
/// </summary>
/// <remarks>
/// The message names the exact option a host would have to set, because "not permitted" with nothing to
/// act on is the failure mode of every feature-flagged admin UI. Thrown by the service, not by the
/// component: hiding a button is a convenience, and a Blazor circuit is a long-lived object a client can
/// drive (AGENTS.md hard rule 5).
/// </remarks>
internal sealed class StudioCapabilityDeniedException : InvalidOperationException
{
    /// <summary>A refusal worded from the studio's own settings - its product name and its options' spelling.</summary>
    /// <param name="capability">The capability that was refused.</param>
    /// <param name="reason">Whether the master switch or the individual capability refused it.</param>
    /// <param name="settings">The studio's settings, for the names the message says.</param>
    public StudioCapabilityDeniedException(StudioCapability capability, CapabilityDenialReason reason, IStudioSettings settings)
        : this(
            capability,
            reason,
            Require(settings).ProductName,
            settings.ReadOnlyOption(),
            settings.CapabilityOption(capability))
    {
    }

    private StudioCapabilityDeniedException(
        StudioCapability capability,
        CapabilityDenialReason reason,
        string productName,
        string readOnlyOption,
        string capabilityOption)
        : base(reason == CapabilityDenialReason.ReadOnly
            ? $"{productName} refused '{capability}' because {readOnlyOption} is true, which turns every capability off."
            : $"{productName} refused '{capability}' because {capabilityOption} is false.")
    {
        Capability = capability;
        Reason = reason;
        CapabilityOption = capabilityOption;
    }

    /// <summary>The capability that was refused.</summary>
    public StudioCapability Capability { get; }

    /// <summary>Whether the master switch or the individual capability refused it.</summary>
    public CapabilityDenialReason Reason { get; }

    /// <summary>
    /// The capability's own option - <c>MartenStudioOptions.Capabilities.EditDocuments</c> - which would have to be set
    /// whichever switch refused it: what event 9202 names.
    /// </summary>
    public string CapabilityOption { get; }

    private static IStudioSettings Require(IStudioSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return settings;
    }
}

/// <summary>
/// Answers whether one mutating operation is enabled at all in this process.
/// </summary>
/// <remarks>
/// Singleton, and deliberately the simplest thing in the codebase: <c>!ReadOnly &amp;&amp; Capabilities.X</c>,
/// read live from <see cref="IStudioSettings" />. It is process-wide and knows nothing about the visitor - who may
/// do what is <see cref="StudioAuthorization" />'s question, and the two are asked one after the other so that
/// neither can be mistaken for the other.
/// </remarks>
internal sealed class StudioCapabilityGuard
{
    private readonly IStudioSettings settings;

    public StudioCapabilityGuard(IStudioSettings settings)
    {
        this.settings = settings;
    }

    /// <summary>Every capability member, in declaration order, whichever studio offers it.</summary>
    public static IReadOnlyList<StudioCapability> All { get; } = Enum.GetValues<StudioCapability>();

    /// <summary>
    /// The capabilities that change nothing and are still authorized like writes - what the capability
    /// chip lists under "Reads authorized like writes" rather than under "Mutating operations".
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every one is a read treated as a write for authorization (D13): the service asks the write policy with the
    /// capability named, and the master switch turns them off with everything else. A page that told a host "every
    /// mutating operation is off" when the console and the database browser were off too would be describing a
    /// narrower switch than the one they set.
    /// </para>
    /// <para>
    /// Two of them read beyond the store's own documents and events (the console, the database browser),
    /// one names other sessions and their statements (<c>ViewDatabaseActivity</c>), and one evaluates a typed
    /// expression in this process (<c>RunLinqQueries</c>). What they share is the rule
    /// <c>StudioActionLog.GetVisibleAsync</c> applies to an audit entry recorded under one of them: it names
    /// what was read, so it is shown only to a visitor who may make the same read.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<StudioCapability> ReadsBeyondTheStore { get; } =
    [
        StudioCapability.RunSql,
        StudioCapability.BrowseDatabase,
        StudioCapability.ViewDatabaseActivity,
        StudioCapability.RunLinqQueries,
    ];

    /// <summary>
    /// Every capability that changes something - <see cref="All" /> without
    /// <see cref="ReadsBeyondTheStore" />, in declaration order.
    /// </summary>
    public static IReadOnlyList<StudioCapability> Mutating { get; } =
        [.. All.Where(static x => !ReadsBeyondTheStore.Contains(x))];

    /// <summary>The capabilities this studio offers, in the order the chip lists them (<see cref="IStudioSettings.Capabilities" />).</summary>
    public IReadOnlyList<StudioCapability> Offered => settings.Capabilities;

    /// <summary>The offered reads authorized like writes, in <see cref="ReadsBeyondTheStore" /> order.</summary>
    public IReadOnlyList<StudioCapability> OfferedReads =>
        [.. ReadsBeyondTheStore.Where(x => settings.Capabilities.Contains(x))];

    /// <summary>The offered mutating capabilities, in <see cref="Mutating" /> order.</summary>
    public IReadOnlyList<StudioCapability> OfferedMutating =>
        [.. Mutating.Where(x => settings.Capabilities.Contains(x))];

    /// <summary>Whether the master switch is on, which turns every capability off.</summary>
    public bool ReadOnly => settings.ReadOnly;

    /// <summary>How many of the offered capabilities are effectively enabled.</summary>
    public int EnabledCount
    {
        get
        {
            int count = 0;
            foreach (StudioCapability capability in settings.Capabilities)
            {
                if (IsEnabled(capability))
                {
                    count++;
                }
            }

            return count;
        }
    }

    /// <summary>Whether <paramref name="capability" /> is enabled, master switch included.</summary>
    public bool IsEnabled(StudioCapability capability) =>
        !settings.ReadOnly && settings.IsConfigured(capability);

    /// <summary>
    /// Throws unless <paramref name="capability" /> is enabled. The first line of every mutating service
    /// method.
    /// </summary>
    /// <exception cref="StudioCapabilityDeniedException">The capability is not enabled.</exception>
    public void Require(StudioCapability capability)
    {
        if (settings.ReadOnly)
        {
            throw new StudioCapabilityDeniedException(capability, CapabilityDenialReason.ReadOnly, settings);
        }

        if (!settings.IsConfigured(capability))
        {
            throw new StudioCapabilityDeniedException(capability, CapabilityDenialReason.Disabled, settings);
        }
    }

    /// <summary>
    /// The property a host would set to enable <paramref name="capability" />, spelled the way it is
    /// written in the host's own registration - which is what a disabled control says out loud.
    /// </summary>
    public string OptionFor(StudioCapability capability) => settings.CapabilityOption(capability);
}
