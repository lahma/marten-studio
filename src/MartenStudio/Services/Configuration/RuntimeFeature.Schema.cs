using System.Reflection;

using Marten.Storage;

namespace MartenStudio.Services.Configuration;

/// <summary>The Schema screen's light-up: what its buttons may create.</summary>
internal sealed partial class RuntimeFeature
{
    /// <summary>
    /// <c>IMartenDatabase.SequenceFeature</c>, the HiLo feature as a schema to be <em>described</em> - Marten 9.50.3
    /// (#5665, merged as #5670): a default interface property in <c>src/Marten/Storage/IMartenDatabase.cs</c>, which
    /// <c>MartenDatabase</c> implements over a <c>SequenceFactory</c> of its own that is built and never applied, and
    /// which <c>StorageFeatures.AllActiveFeatures</c> yields in place of <c>database.Sequences</c>. Absent at
    /// V9.50.2.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>What it changes.</b> Below 9.50.3 <c>AllObjects()</c> - and so <c>AllSchemaNames()</c>,
    /// <c>ToDatabaseScript()</c> and <c>CreateMigrationAsync()</c>'s object list - reached <c>database.Sequences</c>,
    /// whose factory applies the HiLo feature under the database's own <c>AutoCreate</c>: building a script created
    /// <c>mt_hilo</c> and <c>mt_get_next_hi</c> on a store with a numeric or HiLo id. From 9.50.3 the enumeration is
    /// offline. The runtime lazy still provisions the objects the first time an id is handed out, which is Marten's to
    /// do and not the studio's.
    /// </para>
    /// <para>
    /// <b>What it does not change.</b> Hard rule 14's call fence: the floor still has the hazard, and
    /// <c>CreateMigrationAsync</c> and <c>AssertDatabaseMatchesConfigurationAsync</c> still connect. The Schema
    /// screen's buttons word what they may create from this probe; nothing else reads it.
    /// </para>
    /// <para>
    /// The probe asks for <c>MartenDatabase</c>'s own getter: the interface's default body is <c>=&gt; Sequences</c>,
    /// the historical behaviour.
    /// </para>
    /// </remarks>
    public static readonly RuntimeFeature OfflineSchemaEnumeration = new(
        nameof(OfflineSchemaEnumeration),
        RuntimePackage.Marten,
        "9.50.3",
        "9.50.3",
        "Building a schema script, a migration preview or a schema check describes Marten's HiLo objects without creating them.",
        static () => ImplementedBy(
            typeof(MartenDatabase),
            typeof(IMartenDatabase),
            typeof(IMartenDatabase).GetProperty("SequenceFeature", BindingFlags.Public | BindingFlags.Instance)?.GetMethod));
}
