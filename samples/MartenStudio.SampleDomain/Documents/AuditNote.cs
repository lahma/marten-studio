namespace MartenStudio.SampleDomain.Documents;

/// <summary>
/// A document with every one of Marten's optional metadata columns turned on, so the studio's "explain
/// this document" pane has the full set to render.
/// </summary>
/// <remarks>
/// Correlation, causation, headers and last-modified-by are all off by default and all cost a column, so
/// most stores have none of them. This one has all of them - together with <see cref="MinimalNote" />,
/// which has none, it brackets the whole range the metadata pane has to survive.
/// </remarks>
public sealed class AuditNote
{
    public Guid Id { get; set; }

    public string Subject { get; set; } = string.Empty;

    public string Body { get; set; } = string.Empty;

    public string Severity { get; set; } = "info";
}

/// <summary>
/// A document with <c>DisableInformationalFields()</c>: its table is <c>id</c> and <c>data</c> and nothing
/// else.
/// </summary>
/// <remarks>
/// The opposite bracket to <see cref="AuditNote" />, and the one that catches the mistake AGENTS.md hard
/// rule 10 warns about - a select list that names <c>mt_last_modified</c> without checking whether the
/// column exists fails on exactly this type, and on no other.
/// </remarks>
public sealed class MinimalNote
{
    public Guid Id { get; set; }

    public string Text { get; set; } = string.Empty;
}
