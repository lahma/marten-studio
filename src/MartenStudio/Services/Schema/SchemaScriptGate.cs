using MartenStudio.Services.Database;

namespace MartenStudio.Services.Schema;

/// <summary>What Marten would run against a database, in the four shapes the Schema screen shows it.</summary>
internal enum SchemaScript
{
    /// <summary><c>AssertDatabaseMatchesConfigurationAsync</c> and the migration's deltas.</summary>
    Check,

    /// <summary>The migration, rendered as SQL and not run.</summary>
    Preview,

    /// <summary><c>IMartenDatabase.ToDatabaseScript()</c>: everything the store would create.</summary>
    Ddl,

    /// <summary>The migration an apply runs, which the audit entry records.</summary>
    Apply,
}

/// <summary>Why this visitor may not see what Marten would run against the database in scope.</summary>
/// <param name="Kind">
/// Which gate said no: <see cref="DatabaseRefusal.StorePolicy" /> for the tenant-less store policy, or the
/// database browser's own refusal (<see cref="DatabaseRefusal.CapabilityOff" />,
/// <see cref="DatabaseRefusal.ReadOnly" />, <see cref="DatabaseRefusal.WritePolicy" />) while the host hides
/// document types.
/// </param>
/// <param name="Reason">The sentence, naming the option that would change it.</param>
internal sealed record SchemaScriptRefusal(DatabaseRefusal Kind, string Reason);

/// <summary>
/// The sentences the Drift and DDL tabs show when the script is withheld, and why it can be.
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything Marten would run spans the whole database.</b> <c>ToDatabaseScript()</c>,
/// <c>CreateMigrationAsync()</c> and <c>AssertDatabaseMatchesConfigurationAsync()</c> describe every object
/// the store configures - every tenant's, since under Marten-managed tenant partitioning each tenant's
/// partition is a <c>CREATE TABLE … partition of … for values in ('tenant')</c> named after the tenant, and
/// every document type's, the ones <see cref="MartenStudioOptions.IsDocumentTypeVisible" /> hides included.
/// Measured by the DB-7 review: a visitor the store policy let see one tenant read every tenant's name in the
/// DDL tab, and the hidden type's table was in all three outputs while the Tables tab withheld it.
/// </para>
/// <para>
/// So the three outputs, and the apply that runs the fourth, need the store policy for the database as a
/// whole - <c>scope with { TenantId = null }</c> - and, while the store hides any document type Marten knows,
/// the database browser's gate as well: <c>Capabilities.BrowseDatabase</c> and the write policy asked with no
/// tenant, exactly as a browser read asks it. A host with no policy and no hidden type sees no change.
/// </para>
/// </remarks>
internal static class SchemaScriptGate
{
    /// <summary>What the refusal calls the output it withholds.</summary>
    public static string Noun(SchemaScript script) => script switch
    {
        SchemaScript.Check => "The drift check",
        SchemaScript.Preview => "The migration preview",
        SchemaScript.Ddl => "The database script",
        _ => "The migration an apply runs",
    };

    /// <summary>What the tenant-less store policy's refusal says.</summary>
    public static string StorePolicyDenial(SchemaScript script) =>
        Noun(script) + " spans every tenant of this database - under tenant partitioning it names each " +
        "tenant's partition - so it is shown only to a visitor the store policy " +
        "(MartenStudioOptions.StoreAuthorizationPolicy) allows for this store and database as a whole, with " +
        "no tenant selected, and it refused your account that.";

    /// <summary>
    /// What a refusal by the database browser's gate says, while the host hides document types.
    /// </summary>
    /// <param name="script">The output withheld.</param>
    /// <param name="refusal">Which part of the gate said no.</param>
    /// <param name="fallback">The gate's own sentence, for a refusal that is none of the usual four.</param>
    public static string HiddenTypesDenial(SchemaScript script, DatabaseRefusal refusal, string? fallback) =>
        Noun(script) + " would print the tables of document types the host hides from the studio " +
        "(MartenStudioOptions.IsDocumentTypeVisible), so it is shown only past the database browser's gate. " +
        refusal switch
        {
            DatabaseRefusal.ReadOnly => DatabaseGate.ReadOnlyDenial,
            DatabaseRefusal.CapabilityOff => "It needs MartenStudioOptions.Capabilities.BrowseDatabase.",
            DatabaseRefusal.StorePolicy =>
                "The store policy (MartenStudioOptions.StoreAuthorizationPolicy) refused your account this store " +
                "and database as a whole, with no tenant selected.",
            DatabaseRefusal.WritePolicy =>
                "The write policy (MartenStudioOptions.WriteAuthorizationPolicy, or StoreAuthorizationPolicy when " +
                "no write policy is set) refused your account BrowseDatabase for this store and database as a " +
                "whole, with no tenant selected.",
            _ => fallback ?? "The database browser's gate could not be read.",
        };

    /// <summary>
    /// What the audit entry of an apply says in place of the SQL, while the host hides document types.
    /// </summary>
    /// <remarks>
    /// The Activity ring is read under the store policy alone, entry by entry, so a reader who may not browse
    /// the database would otherwise read a hidden type's table in the SQL an administrator applied. The
    /// application's log keeps the whole script under event 9206.
    /// </remarks>
    public const string RingSqlWithheld =
        "(withheld from the Activity ring: it names the tables of document types the host hides from the " +
        "studio; event 9206 in the application's log carries it)";
}
