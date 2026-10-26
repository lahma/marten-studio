using System.Globalization;

using JasperFx.Events.Tags;
using JasperFx.MultiTenancy;

using Marten;
using Marten.Events;

using MartenStudio.Services.Query;
using PostgresStudio.Services.Query;

namespace MartenStudio.Services.Events;

/// <summary>How a store keeps its DCB event tags.</summary>
/// <remarks>
/// Read off <c>EventGraph.DcbStorageMode</c> and <c>EventGraph.TagTypes</c> (verified at Marten 9.39.0, 9.50.2 and
/// 9.51.0): <see cref="TagTables" /> is one <c>mt_event_tag_&lt;suffix&gt;</c> table per registered tag type, keyed
/// <c>(value[, tenant_id], seq_id)</c>; <see cref="HStore" /> is one nullable <c>hstore</c> column on <c>mt_events</c>
/// itself, keyed by the suffix. A store with no tag type registered has neither, whatever its storage mode says. The
/// key and the columns are the same at all three; from 9.51 (#5694) each tag table also declares
/// <c>idx_mt_event_tag_&lt;suffix&gt;_seq_id</c>, so a tag table built below 9.51 shows that index as drift.
/// </remarks>
internal enum EventTagStorage
{
    /// <summary>No tag type is registered.</summary>
    None,

    /// <summary>One table per tag type, its primary key leading with the tag's value.</summary>
    TagTables,

    /// <summary>One <c>hstore</c> column on <c>mt_events</c>, with a GIN index Marten declares over it.</summary>
    HStore,
}

/// <summary>One tag on one event.</summary>
/// <param name="TagType">The tag type, by its table suffix - the name Marten keys both storage modes on.</param>
/// <param name="Value">The value as Postgres renders it: <c>value::text</c>, or the hstore value.</param>
internal sealed record EventTag(string TagType, string Value);

/// <summary>One tag of one event, as an explicit tag read returns it.</summary>
/// <param name="Sequence">The event's <c>seq_id</c>.</param>
/// <param name="TenantId">
/// The event's tenant, on a conjoined store, so a caller pairs a tag with the right event even under
/// <c>UseTenantPartitionedEvents</c>, where every tenant draws <c>seq_id</c> from a sequence of its own and
/// the number alone does not identify an event (Marten's own <c>EventsTable</c> remarks, #5234).
/// </param>
/// <param name="Tag">The tag.</param>
internal sealed record EventTagRow(long Sequence, string? TenantId, EventTag Tag);

/// <summary>What an explicit tag read found.</summary>
/// <param name="Rows">Every tag of every event asked about that the scope may see.</param>
/// <param name="Error">What went wrong, when the read failed.</param>
internal sealed record EventTagRead(IReadOnlyList<EventTagRow> Rows, EventDataError? Error = null)
{
    /// <summary>Nothing to read, or nothing found.</summary>
    public static EventTagRead Empty { get; } = new([]);

    /// <summary>A read that failed.</summary>
    public static EventTagRead Failed(EventDataError error) => new([], error);
}

/// <summary>Whether a page of events came back with its tags.</summary>
internal enum EventTagLoadState
{
    /// <summary>This store registers no tag type, so there is nothing to load.</summary>
    NotApplicable,

    /// <summary>Every event on the page carries its tags.</summary>
    Loaded,

    /// <summary>
    /// The tags were not read with the page, because reading them would scan a tag table larger than the
    /// studio scans without being asked. The page offers to read them.
    /// </summary>
    Deferred,

    /// <summary>The tag read failed; the events are shown without tags.</summary>
    Failed,
}

/// <summary>Whether a page of events came back with its tags, and why not when it did not.</summary>
/// <param name="State">What happened.</param>
/// <param name="Reason">Why the tags are missing, in a sentence the page can show.</param>
internal sealed record EventTagLoad(EventTagLoadState State, string? Reason = null)
{
    /// <summary>No tag types.</summary>
    public static EventTagLoad NotApplicable { get; } = new(EventTagLoadState.NotApplicable);

    /// <summary>Tags are on the rows.</summary>
    public static EventTagLoad Loaded { get; } = new(EventTagLoadState.Loaded);

    /// <summary>Tags were left for the visitor to ask for.</summary>
    public static EventTagLoad Deferred(string reason) => new(EventTagLoadState.Deferred, reason);

    /// <summary>The tag read failed.</summary>
    public static EventTagLoad Failed(string reason) => new(EventTagLoadState.Failed, reason);
}

/// <summary>One registered tag type, as the studio models it from the store's configuration.</summary>
/// <param name="Name">The tag type's CLR simple name (<c>StudentId</c>).</param>
/// <param name="Suffix">Its table suffix (<c>student</c>), which is also its hstore key.</param>
/// <param name="SimpleType">The primitive inside it: <c>string</c>, <c>Guid</c>, <c>int</c>, <c>long</c> or <c>short</c>.</param>
/// <param name="AggregateName">The aggregate it is associated with, when Marten knows one.</param>
internal sealed record EventTagTypeModel(string Name, string Suffix, Type SimpleType, string? AggregateName)
{
    /// <summary>The tag table's name, exactly as Marten's <c>EventTagTable</c> builds it.</summary>
    public string Table => EventTagModel.TablePrefix + Suffix;

    /// <summary>The value type, in words.</summary>
    public string ValueTypeName => SimpleType.Name;
}

/// <summary>
/// The store's DCB tag configuration: storage mode, tag types and tenancy, read from <c>StoreOptions</c>
/// and nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <c>TagTypes</c> and <c>DcbStorageMode</c> live on Marten's <c>EventGraph</c> and on the writable
/// <c>IEventStoreOptions</c>, not on <c>IReadOnlyEventStoreOptions</c> - which is all
/// <c>IDocumentStore.Options</c> exposes - so they are read through the <c>EventGraph</c> that
/// <c>IReadOnlyStoreOptions.Events</c> returns (verified at 9.39.0: <c>StoreOptions</c> implements it as
/// <c>=&gt; EventGraph</c>). A store that hands back something else has no tags the studio can see.
/// </para>
/// <para>
/// The list includes the tag types Marten registers on its own: since 9.x it walks every projection whose
/// identity is a strong-typed id and registers that type (<c>autoDiscoverTagTypesFromProjections</c>), so
/// a store that never called <c>RegisterTagType</c> may still have tag tables.
/// </para>
/// </remarks>
/// <param name="Storage">How tags are kept.</param>
/// <param name="Types">The registered tag types, in registration order.</param>
/// <param name="IsConjoined">Whether the event store is conjoined-tenanted, so tags carry a tenant.</param>
internal sealed record EventTagModel(EventTagStorage Storage, IReadOnlyList<EventTagTypeModel> Types, bool IsConjoined)
{
    /// <summary>The tag tables' common prefix, from Marten's <c>EventTagTable</c>.</summary>
    public const string TablePrefix = "mt_event_tag_";

    /// <summary>The DCB boundary-version side table, from Marten's <c>DcbTagVersionTable</c>.</summary>
    public const string VersionTable = "mt_dcb_tag_version";

    /// <summary>The hstore column the <see cref="EventTagStorage.HStore" /> mode keeps tags in.</summary>
    public const string HStoreColumn = "tags";

    /// <summary>A store with no tags.</summary>
    public static EventTagModel None { get; } = new(EventTagStorage.None, [], false);

    /// <summary>Reads the store's tag configuration.</summary>
    /// <param name="options">The store's own options; nothing is guessed.</param>
    /// <param name="isVisible">
    /// <c>MartenStudioOptions.IsDocumentTypeVisible</c>: a tag type associated with an aggregate it hides keeps
    /// its table and its filter - a tag is not that aggregate's state - but the aggregate's name is not shown.
    /// </param>
    public static EventTagModel From(IReadOnlyStoreOptions options, Func<Type, bool>? isVisible = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (options.Events is not EventGraph graph || graph.TagTypes.Count == 0)
        {
            return None;
        }

        List<EventTagTypeModel> types = [];
        foreach (ITagTypeRegistration registration in graph.TagTypes)
        {
            Type? aggregate = registration.AggregateType;

            types.Add(new EventTagTypeModel(
                registration.TagType.Name,
                registration.TableSuffix,
                registration.SimpleType,
                aggregate is not null && (isVisible is null || isVisible(aggregate)) ? aggregate.Name : null));
        }

        return new EventTagModel(
            graph.DcbStorageMode == DcbStorageMode.HStore ? EventTagStorage.HStore : EventTagStorage.TagTables,
            types,
            options.Events.TenancyStyle == TenancyStyle.Conjoined);
    }

    /// <summary>
    /// The tag type a name refers to: the CLR simple name first, across every type, then the table suffix,
    /// both ignoring case.
    /// </summary>
    /// <remarks>
    /// The rule of JasperFx's own <c>TagTypeRegistrationExtensions.FindByTagName</c> (2.74.0), restated over
    /// the studio's model rather than called, because this model is what the pages hold. Two passes for the
    /// reason JasperFx gives: a suffix that collides with another type's name cannot shadow that type.
    /// </remarks>
    public EventTagTypeModel? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();

        foreach (EventTagTypeModel type in Types)
        {
            if (string.Equals(type.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        foreach (EventTagTypeModel type in Types)
        {
            if (string.Equals(type.Suffix, wanted, StringComparison.OrdinalIgnoreCase))
            {
                return type;
            }
        }

        return null;
    }
}

/// <summary>
/// The feed's <c>tag:&lt;type&gt;=&lt;value&gt;</c> filter, as it travels in the query string.
/// </summary>
/// <param name="TypeName">The tag type, by CLR name or suffix, as typed.</param>
/// <param name="Value">The value, as typed.</param>
internal sealed record EventTagFilter(string TypeName, string Value)
{
    /// <summary>What the filter is written with, and what the box accepts in front of it.</summary>
    public const string Prefix = "tag:";

    /// <summary>
    /// Reads <c>tag:student=abc</c> or <c>student=abc</c>; <see langword="null" /> for anything else.
    /// </summary>
    /// <remarks>
    /// The value is everything after the <em>first</em> <c>=</c>, so a value that itself contains one -
    /// a base64 key, an expression - survives. Surrounding whitespace is trimmed from both halves, the
    /// same courtesy the stream box extends a pasted id.
    /// </remarks>
    public static EventTagFilter? Parse(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        string text = raw.Trim();
        if (text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            text = text[Prefix.Length..];
        }

        int separator = text.IndexOf('=', StringComparison.Ordinal);
        if (separator <= 0 || separator == text.Length - 1)
        {
            return null;
        }

        string type = text[..separator].Trim();
        string value = text[(separator + 1)..].Trim();

        return type.Length == 0 || value.Length == 0 ? null : new EventTagFilter(type, value);
    }

    /// <summary>The query-string form: <c>type=value</c>.</summary>
    public static string Format(string typeName, string value) => typeName + "=" + value;

    /// <summary>The query-string form of this filter.</summary>
    public string Token => Format(TypeName, Value);

    /// <summary>How the filter is written back to the visitor.</summary>
    public string Display => Prefix + Token;
}

/// <summary>Turns a typed tag value into the value its column holds.</summary>
internal static class EventTagValues
{
    /// <summary>
    /// Parses <paramref name="text" /> as a value of <paramref name="simpleType" />, the primitive Marten's
    /// <c>EventTagTable</c> maps to <c>text</c>, <c>uuid</c>, <c>integer</c>, <c>bigint</c> or <c>smallint</c>.
    /// </summary>
    /// <returns>Whether it parsed; <paramref name="error" /> says why not.</returns>
    public static bool TryParse(Type simpleType, string? text, out object? value, out string? error)
    {
        ArgumentNullException.ThrowIfNull(simpleType);

        string trimmed = (text ?? string.Empty).Trim();
        value = null;
        error = null;

        if (simpleType == typeof(string))
        {
            value = trimmed;
            return true;
        }

        if (simpleType == typeof(Guid))
        {
            if (Guid.TryParse(trimmed, CultureInfo.InvariantCulture, out Guid guid))
            {
                value = guid;
                return true;
            }
        }
        else if (simpleType == typeof(int))
        {
            if (int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number))
            {
                value = number;
                return true;
            }
        }
        else if (simpleType == typeof(long))
        {
            if (long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out long number))
            {
                value = number;
                return true;
            }
        }
        else if (simpleType == typeof(short))
        {
            if (short.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out short number))
            {
                value = number;
                return true;
            }
        }
        else
        {
            error = $"Tag values of type {simpleType.Name} cannot be filtered on here.";
            return false;
        }

        error = $"'{PostgresStudio.Services.StudioLogValue.Cap(trimmed)}' is not a {simpleType.Name}, which is what this tag type holds.";
        return false;
    }

    /// <summary>
    /// The text Marten writes into the hstore column for a value: Marten's internal
    /// <c>TagValueStringifier</c> rule at 9.39.0 - a <see cref="Guid" /> as <c>"d"</c>, anything formattable
    /// in the invariant culture, a string as it is.
    /// </summary>
    public static string ToHStoreText(object value) => value switch
    {
        string text => text,
        Guid guid => guid.ToString("d", CultureInfo.InvariantCulture),
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };
}

/// <summary>
/// Whether the database can serve a tag filter from an index, read off <c>pg_indexes</c>.
/// </summary>
/// <remarks>
/// The verdict the documents list draws beside its filter, applied to the feed's one tag filter. Marten
/// declares the index in both storage modes, so a red here is a database that differs from the store's
/// declaration - an index a DBA dropped, or the GIN index a host took out of the migration with
/// <c>IgnoreIndex</c> (Marten's #5268 remarks invite exactly that) and never built.
/// </remarks>
internal static class EventTagIndexes
{
    /// <summary>The GIN index Marten declares over <c>mt_events.tags</c> (<c>EventGraph.HStoreTagIndexName</c>).</summary>
    public const string HStoreIndexName = "idx_mt_events_tags";

    /// <summary>What ends an index's first key column in <c>pg_get_indexdef</c>'s output.</summary>
    private static readonly char[] KeyEnd = [',', ')'];

    /// <summary>
    /// Whether an index definition, as <c>pg_get_indexdef</c> prints it, uses <paramref name="method" />
    /// and has <paramref name="column" /> as its first key column.
    /// </summary>
    /// <remarks>
    /// <c>CREATE UNIQUE INDEX pk_mt_event_tag_student ON s.mt_event_tag_student USING btree (value, seq_id)</c>
    /// is the shape. The first key is the text between <c>USING method (</c> and the first comma or closing
    /// parenthesis, with any double quotes removed: <c>value</c> is a non-reserved keyword and Postgres
    /// prints it bare, but a quoted spelling is the same column.
    /// </remarks>
    public static bool LeadsWith(string? indexDefinition, string column, string method)
    {
        if (string.IsNullOrWhiteSpace(indexDefinition))
        {
            return false;
        }

        string marker = "USING " + method + " (";
        int start = indexDefinition.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0)
        {
            return false;
        }

        start += marker.Length;
        int end = indexDefinition.IndexOfAny(KeyEnd, start);
        if (end < 0)
        {
            return false;
        }

        string first = indexDefinition[start..end].Trim().Replace("\"", string.Empty, StringComparison.Ordinal);
        return string.Equals(first, column, StringComparison.Ordinal);
    }

    /// <summary>The verdict on <c>tag:</c> against one tag table.</summary>
    /// <param name="indexes">The table's indexes, from <c>IndexCatalog</c>.</param>
    /// <param name="table">The table's name, for the sentence.</param>
    public static (IndexVerdictLevel Level, string Reason) TagTable(IReadOnlyList<PostgresIndex> indexes, string table)
    {
        ArgumentNullException.ThrowIfNull(indexes);

        foreach (PostgresIndex index in indexes)
        {
            if (LeadsWith(index.Definition, "value", "btree"))
            {
                return (IndexVerdictLevel.Green,
                    $"Index-backed: {index.Name} on {table} leads with value, so the filter reads only that value's rows.");
            }
        }

        return (IndexVerdictLevel.Red,
            $"No index on {table} leads with value - Marten declares its primary key that way, so this database " +
            "differs from the store's configuration - and the filter reads the whole tag table.");
    }

    /// <summary>The verdict on <c>tag:</c> against the hstore column.</summary>
    /// <param name="indexes">The indexes on <c>mt_events</c>, from <c>IndexCatalog</c>.</param>
    public static (IndexVerdictLevel Level, string Reason) HStore(IReadOnlyList<PostgresIndex> indexes)
    {
        ArgumentNullException.ThrowIfNull(indexes);

        foreach (PostgresIndex index in indexes)
        {
            if (LeadsWith(index.Definition, EventTagModel.HStoreColumn, "gin"))
            {
                return (IndexVerdictLevel.Green,
                    $"Index-backed: the GIN index {index.Name} on mt_events.tags serves the @> containment the filter uses.");
            }
        }

        return (IndexVerdictLevel.Red,
            $"No GIN index on mt_events.tags: Marten declares {HStoreIndexName}, but this database does not have it " +
            "(a store that ignores it with IgnoreIndex is expected to build it itself), so the filter reads every event.");
    }
}

/// <summary>One registered tag type, as the Event types screen lists it.</summary>
/// <param name="Name">The CLR simple name.</param>
/// <param name="Suffix">The table suffix and hstore key.</param>
/// <param name="ValueType">The primitive inside it, in words.</param>
/// <param name="AggregateName">The aggregate Marten associates it with, when it knows one.</param>
/// <param name="Table">Where its rows live: the tag table, or <c>mt_events.tags</c>.</param>
/// <param name="TableExists">Whether that table is in the database at all.</param>
/// <param name="Rows">How many tag rows it holds - an estimate (D8) - or unknown.</param>
/// <param name="RowsNote">Why there is no number, when there is not.</param>
/// <param name="FilterLevel">Whether <c>tag:</c> on it is index-backed.</param>
/// <param name="FilterReason">The sentence behind <paramref name="FilterLevel" />.</param>
internal sealed record EventTagTypeInfo(
    string Name,
    string Suffix,
    string ValueType,
    string? AggregateName,
    string Table,
    bool TableExists,
    EventStoreCount Rows,
    string? RowsNote,
    IndexVerdictLevel FilterLevel,
    string FilterReason);

/// <summary>Every tag type the store registers, with the facts the Event types screen and the feed's filter need.</summary>
/// <param name="Storage">How the store keeps its tags.</param>
/// <param name="Types">The tag types.</param>
/// <param name="VersionTableExists">Whether <c>mt_dcb_tag_version</c> is in the database.</param>
/// <param name="VersionRows">Its row estimate, or unknown.</param>
/// <param name="Error">What went wrong, when the list could not be read.</param>
/// <param name="IsConjoined">Whether the event store is conjoined, so each tag table's key carries the tenant.</param>
internal sealed record EventTagTypeList(
    EventTagStorage Storage,
    IReadOnlyList<EventTagTypeInfo> Types,
    bool VersionTableExists,
    EventStoreCount VersionRows,
    EventDataError? Error = null,
    bool IsConjoined = false)
{
    /// <summary>A store with no tag type.</summary>
    public static EventTagTypeList None { get; } = new(EventTagStorage.None, [], false, EventStoreCount.Unknown);

    /// <summary>A list that could not be read.</summary>
    public static EventTagTypeList Failed(EventDataError error) =>
        new(EventTagStorage.None, [], false, EventStoreCount.Unknown, error);

    /// <summary>Whether there is anything to show.</summary>
    public bool HasTags => Types.Count > 0;

    /// <summary>The tag type a filter names, by the same rule <see cref="EventTagModel.Find" /> uses.</summary>
    public EventTagTypeInfo? Find(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        string wanted = name.Trim();

        return Types.FirstOrDefault(x => string.Equals(x.Name, wanted, StringComparison.OrdinalIgnoreCase))
            ?? Types.FirstOrDefault(x => string.Equals(x.Suffix, wanted, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>What the pages do with an event's tags: hand them to a card, and pair a tag read with its events.</summary>
internal static class EventTagView
{
    /// <summary>A row's tags as the (type, value) pairs <c>EventCard</c> takes.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Pairs(EventRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        if (row.Tags.Count == 0)
        {
            return [];
        }

        List<KeyValuePair<string, string>> pairs = new(row.Tags.Count);
        foreach (EventTag tag in row.Tags)
        {
            pairs.Add(new KeyValuePair<string, string>(tag.TagType, tag.Value));
        }

        return pairs;
    }

    /// <summary>
    /// Pairs tag rows with events: by sequence, and by tenant too wherever both sides name one.
    /// </summary>
    /// <remarks>
    /// The tenant matters under <c>UseTenantPartitionedEvents</c>, where <c>seq_id</c> repeats across tenants
    /// and an "all tenants" page can hold two events with the same number. Tags of an event that is not on
    /// the page are dropped, so a read that returned more than was asked can never decorate the wrong row.
    /// </remarks>
    public static List<EventRow> Merge(IReadOnlyList<EventRow> rows, IReadOnlyList<EventTagRow> tags)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(tags);

        List<EventRow> merged = new(rows.Count);

        foreach (EventRow row in rows)
        {
            List<EventTag> own = [];

            foreach (EventTagRow tag in tags)
            {
                if (tag.Sequence == row.Sequence
                    && (tag.TenantId is null || row.TenantId is null
                        || string.Equals(tag.TenantId, row.TenantId, StringComparison.Ordinal)))
                {
                    own.Add(tag.Tag);
                }
            }

            merged.Add(own.Count == 0 ? row : row with { Tags = own });
        }

        return merged;
    }
}

/// <summary>Sentences the tag screens share, so the feed, the timeline and the Event types screen say one thing.</summary>
internal static class EventTagText
{
    /// <summary>Why a tenant-scoped visitor is shown no tag-table estimate on a conjoined store.</summary>
    /// <remarks>
    /// The documents rail's rule (<c>DocumentDataService.CrossTenantEstimateNote</c>) for the same reason:
    /// <c>pg_class.reltuples</c> counts every tenant's rows, and a number over one tenant's screen that is
    /// everybody's is the cardinality disclosure that note closed.
    /// </remarks>
    public const string CrossTenantEstimateNote =
        "No estimate in this scope: pg_class.reltuples counts every tenant's tags, and this event store is conjoined-tenanted.";

    /// <summary>Why a page's tags were not read with it.</summary>
    /// <param name="table">The tag table that is too large.</param>
    /// <param name="threshold">The studio's <c>ExactCountThreshold</c>.</param>
    public static string Deferred(string table, long threshold) =>
        $"Tags are not shown yet: {table} holds more than {threshold.ToString("N0", CultureInfo.InvariantCulture)} rows " +
        "(MartenStudioOptions.ExactCountThreshold), and a tag table is keyed by value rather than by event, so finding " +
        "one page's tags reads the whole table. Load them to pay that once, for the events on the page now; events that " +
        "arrive later are shown without tags until you load again.";
}
