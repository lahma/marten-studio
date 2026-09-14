namespace MartenStudio.SampleDomain.Generation;

/// <summary>
/// A demo document that knows which generation run wrote it.
/// </summary>
/// <remarks>
/// <para>
/// This is what makes "truncate generated data" a safe thing to offer. Without a marker the only way to
/// remove generated rows would be to delete the collection, which would take the seeded demo data with
/// it - and a sample host is exactly the kind of process somebody points at a database that matters.
/// With the marker, truncation is <c>DeleteWhere(x =&gt; x.GeneratedRun != null)</c> and the seeder's own
/// twenty-five customers are untouched, because their property is null.
/// </para>
/// <para>
/// The property is written into the document's JSON like any other, but only when it is set: the sample
/// types annotate it <c>[JsonIgnore(WhenWritingNull)]</c>, so a seeded document's JSON is byte for byte
/// what it was before this existed - which matters because the studio's document editor shows a
/// round-trip diff (D7), and a property that appears as <c>null</c> in every document would be noise in
/// every one of those diffs.
/// </para>
/// </remarks>
public interface IGeneratedDocument
{
    /// <summary>The run that wrote this document, or <see langword="null" /> for hand-seeded demo data.</summary>
    string? GeneratedRun { get; set; }
}

/// <summary>
/// One record per generation run, so that a later truncation can find what the run wrote.
/// </summary>
/// <remarks>
/// <para>
/// Documents carry their run id and can be deleted by predicate. Streams cannot: Marten has no
/// "delete these streams" operation at all, and the only stream ids the generator wrote are the ones it
/// derived from the run id - which a truncation running in a later process has no other way to know. So
/// the run is written down.
/// </para>
/// <para>
/// Deliberately not registered in <see cref="SampleStore" />. The table appears the first time a run
/// completes, which is also the first time it has anything to say, and a demo store whose collections
/// rail lists an empty bookkeeping table before anybody has generated anything is a worse demo.
/// </para>
/// </remarks>
public sealed class DemoDataRun
{
    /// <summary>The run id, which is also the marker every document of the run carries.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Which preset the run was started from.</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>The content seed.</summary>
    public int Seed { get; set; }

    /// <summary>How many streams the run appended. Their ids are derived from <see cref="Id" />.</summary>
    public int Streams { get; set; }

    /// <summary>How many documents the run wrote.</summary>
    public long Documents { get; set; }

    /// <summary>How many events the run appended.</summary>
    public long Events { get; set; }

    /// <summary>When it started.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When it finished, or <see langword="null" /> if it was cancelled or failed.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Whether the run ran to completion.</summary>
    public bool Completed { get; set; }
}
