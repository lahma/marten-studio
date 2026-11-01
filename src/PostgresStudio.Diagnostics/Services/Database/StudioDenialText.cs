using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Database;

/// <summary>
/// The sentences the database browser and Database › Sessions refuse with in MartenStudio's words, kept where
/// <see cref="StudioText" /> builds its own from them, so the engine's settings need no UI type.
/// </summary>
/// <remarks>
/// The gates that say them - <c>DatabaseGate</c> and <c>SessionActivityService</c> - are the UI's, and name them again
/// under their own constants; the words are here once.
/// </remarks>
internal static class StudioDenialText
{
    /// <summary>What <c>ReadOnly</c> says in MartenStudio (<see cref="StudioText.BrowseReadOnlyDenial" />).</summary>
    public const string BrowseReadOnlyDenial =
        "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.BrowseDatabase off " +
        "with every other capability.";

    /// <summary>What a capability that is off says in MartenStudio (<see cref="StudioText.BrowseCapabilityDenial" />).</summary>
    public const string BrowseCapabilityDenial =
        "Other schemas, and the rows and definitions of objects Marten does not own, need " +
        "MartenStudioOptions.Capabilities.BrowseDatabase.";

    /// <summary>
    /// What the write policy's refusal says in MartenStudio: the account, and the policy that answered for it
    /// (<see cref="StudioText.BrowseWritePolicyDenial" />).
    /// </summary>
    public const string BrowseWritePolicyDenial =
        "Your account may not browse the database here. The write policy (MartenStudioOptions." +
        "WriteAuthorizationPolicy, or StoreAuthorizationPolicy when no write policy is set) refused " +
        "BrowseDatabase for this store and database as a whole, with no tenant selected.";

    /// <summary>
    /// What the store policy's refusal says in MartenStudio (<see cref="StudioText.BrowseStorePolicyDenial" />): asked
    /// first, for the database as a whole, before the write
    /// policy is asked at all.
    /// </summary>
    public const string BrowseStorePolicyDenial =
        "Your account may not browse the database here. The store policy (MartenStudioOptions." +
        "StoreAuthorizationPolicy) refused this store and database as a whole, with no tenant selected.";

    /// <summary>What an empty <c>BrowsableSchemas</c> says in MartenStudio (<see cref="StudioText.BrowseEmptyListDenial" />).</summary>
    public const string BrowseEmptyListDenial =
        "MartenStudioOptions.BrowsableSchemas is empty, so no schema's non-Marten rows or definitions are " +
        "browsable. List the schema names, or \"*\".";

    /// <summary>
    /// What <c>ReadOnly</c> says about a definition in MartenStudio (<see cref="StudioText.DefinitionReadOnlyDenial" />): it
    /// turns both ways in off.
    /// </summary>
    public const string DefinitionReadOnlyDenial =
        "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDefinitions and " +
        "MartenStudioOptions.Capabilities.BrowseDatabase off with every other capability.";

    /// <summary>
    /// What both ways into a definition being off says in MartenStudio (<see cref="StudioText.DefinitionCapabilityDenial" />).
    /// </summary>
    public const string DefinitionCapabilityDenial =
        "The definitions of objects Marten does not own need MartenStudioOptions.Capabilities.ViewDefinitions, for the " +
        "schemas MartenStudioOptions.BrowsableSchemas names exactly, or MartenStudioOptions.Capabilities.BrowseDatabase.";

    /// <summary>
    /// What opens MartenStudio's write-policy refusal of a definition; the names of the capabilities the policy refused
    /// follow (<see cref="StudioText.DefinitionWritePolicyDenialFor" />).
    /// </summary>
    public const string DefinitionWritePolicyDenialHead =
        "Your account may not read definitions here. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, or " +
        "StoreAuthorizationPolicy when no write policy is set) refused ";

    /// <summary>What closes it in MartenStudio.</summary>
    public const string DefinitionWritePolicyDenialTail = " for this store and database as a whole, with no tenant selected.";

    /// <summary>
    /// What a view that reads one of Marten's own tables says about its rows in MartenStudio
    /// (<see cref="StudioText.MartenDependencyDenial" />).
    /// </summary>
    public const string MartenDependencyDenial =
        "This view reads one of Marten's own tables - a document or event table, or Marten's bookkeeping - " +
        "whose rows are never read raw: open them in Documents or Events, where tenancy, soft delete and the " +
        "store's serializer apply.";

    /// <summary>What a capability that is off says.</summary>
    public const string SessionsCapabilityDenial =
        "Process ids, roles, client addresses, application names, connection times and statements need "
        + "MartenStudioOptions.Capabilities.ViewDatabaseActivity.";

    /// <summary>What <c>ReadOnly</c> says.</summary>
    public const string SessionsReadOnlyDenial =
        "MartenStudioOptions.ReadOnly is true, which turns MartenStudioOptions.Capabilities.ViewDatabaseActivity off with every "
        + "other capability.";

    /// <summary>What the store policy's refusal says.</summary>
    public const string SessionsStorePolicyDenial =
        "Your account may not see this database's sessions. The store policy (MartenStudioOptions.StoreAuthorizationPolicy) "
        + "refused this store and database as a whole, with no tenant selected: a session belongs to no tenant.";

    /// <summary>What the write policy's refusal says.</summary>
    public const string SessionsWritePolicyDenial =
        "Your account may not see who is using this database. The write policy (MartenStudioOptions.WriteAuthorizationPolicy, "
        + "or StoreAuthorizationPolicy when no write policy is set) refused ViewDatabaseActivity for this store and database "
        + "as a whole, with no tenant selected.";
}
