using JasperFx;
using JasperFx.Metadata;

namespace MartenStudio.Integration.Tests.Documents;

// ---------------------------------------------------------------------------------------------------
// The document shapes the write path has to get right that the demo domain does not have.
//
// They live here rather than in samples/MartenStudio.SampleDomain because the demo domain is what a
// reader looks at to understand the product: a hierarchy that exists only so a test can prove
// mt_doc_type survives a save would be noise there. Every one of them is registered by a
// DocumentWriteFixture subclass through ConfigureExtras, on that class's own schema.
// ---------------------------------------------------------------------------------------------------

/// <summary>
/// The root of a subclass hierarchy, and abstract on purpose.
/// </summary>
/// <remarks>
/// Abstract is the case that makes the bug in AllKnownDocumentTypes()-only resolution visible twice
/// over: the alias <c>asset</c> resolves to this type, and this type cannot be deserialized into at all,
/// so a row with no readable <c>mt_doc_type</c> has to be a refusal rather than an exception.
/// </remarks>
public abstract class Asset
{
    /// <summary>The identity. Shared by every subclass, because they share one table.</summary>
    public Guid Id { get; set; }

    /// <summary>A member the root itself has, so a subclass row has something in common with its parent.</summary>
    public string Location { get; set; } = string.Empty;
}

/// <summary>A subclass with a member of its own, which is the easy half of the hierarchy problem.</summary>
public sealed class Laptop : Asset
{
    /// <summary>A member only this subclass has.</summary>
    public string Serial { get; set; } = string.Empty;
}

/// <summary>
/// A subclass with no members of its own at all — the hard half.
/// </summary>
/// <remarks>
/// A save that deserialized this row as <see cref="Asset" /> and stored it back would lose nothing out
/// of the JSON, so the round-trip differ would report no drops and the dialog would say "nothing will
/// change" — while the row quietly stopped being a <see cref="Screen" />. Only <c>mt_doc_type</c> and
/// <c>mt_dotnet_type</c> would move, and nobody is looking at those.
/// </remarks>
public sealed class Screen : Asset
{
}

/// <summary>
/// A document with a string id, which Marten allows to contain anything a string can — including the
/// <c>/</c> that D9 says is why ids travel in the query string.
/// </summary>
public sealed class Note
{
    /// <summary>The identity, assigned by the caller.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Something to change.</summary>
    public string Body { get; set; } = string.Empty;
}

/// <summary>
/// A document that opts into Marten's numeric revisions by implementing <see cref="IRevisioned" />.
/// </summary>
/// <remarks>
/// The revision lives in the same <c>mt_version</c> column as the Guid version, as an <c>integer</c>
/// rather than a <c>uuid</c> — so this type is what proves the write path reads the column for what it
/// holds rather than for what a default mapping would put there.
/// </remarks>
public sealed class Ticket : IRevisioned
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to change.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Marten keeps this in step with <c>mt_version</c>.</summary>
    public int Version { get; set; }
}

/// <summary>
/// A document that carries Marten's Guid version on a member of its own by implementing
/// <see cref="IVersioned" />.
/// </summary>
/// <remarks>
/// The member is what makes this different from an ordinary optimistic-concurrency type: the version is
/// in the JSON as well as in <c>mt_version</c>, which is the case <c>WritePreview</c>'s remark about
/// version members is about.
/// </remarks>
public sealed class Manifest : IVersioned
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to change.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Marten keeps this in step with <c>mt_version</c>.</summary>
    public Guid Version { get; set; }
}

/// <summary>What a <see cref="Gadget" /> is doing, stored as its name rather than its number.</summary>
public enum GadgetState
{
    /// <summary>Not doing anything.</summary>
    Idle,

    /// <summary>Doing something.</summary>
    Running,
}

/// <summary>
/// A document for a store whose serializer is not configured the way Marten's default is: camel-cased
/// member names and enums written as strings.
/// </summary>
/// <remarks>
/// The round-trip preview has to go through <c>store.Options.Serializer()</c> (AGENTS.md hard rule 10).
/// A studio that used a <c>JsonSerializer</c> of its own would read <c>displayName</c> out of the stored
/// JSON, fail to bind it to <c>DisplayName</c>, and report every property in the document as dropped and
/// re-added — a dialog full of data loss that is not happening.
/// </remarks>
public sealed class Gadget
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>A Pascal-cased member, stored as <c>displayName</c>.</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>An enum, stored as <c>"Running"</c> rather than as <c>1</c>.</summary>
    public GadgetState State { get; set; }

    /// <summary>A nested object, so the casing has to be right more than one level down.</summary>
    public GadgetPart Part { get; set; } = new(string.Empty, 0);
}

/// <summary>One part of a <see cref="Gadget" />, inside its JSON.</summary>
/// <param name="PartName">Stored as <c>partName</c>.</param>
/// <param name="Count">Stored as <c>count</c>.</param>
public sealed record GadgetPart(string PartName, int Count);
