using System.Globalization;

namespace MartenStudio.SampleDomain.Generation;

/// <summary>
/// The four sizes the demo data generator understands.
/// </summary>
/// <remarks>
/// The sizes exist for two different questions. <see cref="Small" /> and <see cref="Medium" /> answer
/// "does the studio work" - they are quick enough to run while writing a screen. <see cref="Large" />
/// answers "does the studio stay usable", which is a different property and the only one that catches an
/// accidental sequential scan, an exact <c>count(*)</c> on a navigation path, or an offset page that
/// walks half a million rows to throw them away.
/// </remarks>
public enum DemoDataSize
{
    /// <summary>Roughly what <c>SampleDataSeeder</c> writes: enough to look at, nothing to measure.</summary>
    Small,

    /// <summary>About 100 000 documents and 100 000 events. A minute or so, and every screen pages for real.</summary>
    Medium,

    /// <summary>About 1.2 million documents and 1.2 million events. Production scale, on purpose.</summary>
    Large,

    /// <summary>Whatever counts the caller asked for.</summary>
    Custom,
}

/// <summary>
/// Exactly how much of everything one generation run writes.
/// </summary>
/// <remarks>
/// <para>
/// A record of plain numbers rather than a builder: the panel posts a form, the form becomes one of
/// these, and a run is reproducible from the plan plus <see cref="Seed" /> alone. Nothing here reads the
/// clock or the environment.
/// </para>
/// <para>
/// <see cref="Seed" /> fixes the <em>content</em> - the names, addresses, SKUs and amounts - so two runs
/// of the same plan produce the same documents. It deliberately does not fix the <em>identities</em>:
/// those come from the run id (see <see cref="DemoDataGenerator" />), so a second run appends a second
/// set of rows instead of colliding with the first on <c>customer.email</c>'s unique index.
/// </para>
/// </remarks>
public sealed record DemoDataPlan
{
    /// <summary>The most rows one run will write. Roughly twenty times <see cref="DemoDataSize.Large" />.</summary>
    public const long MaxRows = 25_000_000;

    /// <summary>Which preset this came from, or <see cref="DemoDataSize.Custom" />.</summary>
    public DemoDataSize Size { get; init; } = DemoDataSize.Small;

    /// <summary>Fixes the generated content. The same seed gives the same names, prices and addresses.</summary>
    public int Seed { get; init; } = 20260914;

    /// <summary>How many customers.</summary>
    public int Customers { get; init; }

    /// <summary>How many orders, soft-deleted ones included.</summary>
    public int Orders { get; init; }

    /// <summary>
    /// How many of <see cref="Orders" /> are soft-deleted afterwards, so the documents screen's
    /// include-deleted tri-state has something to hide at scale.
    /// </summary>
    public int SoftDeletedOrders { get; init; }

    /// <summary>How many invoices, split evenly across <see cref="SampleStore.TenantIds" />.</summary>
    public int Invoices { get; init; }

    /// <summary>How many products. Their ids are strings, which is the interesting part.</summary>
    public int Products { get; init; }

    /// <summary>How many vehicles, split two to one between cars and trucks in the same table.</summary>
    public int Vehicles { get; init; }

    /// <summary>How many audit notes - the type with every optional metadata column enabled.</summary>
    public int AuditNotes { get; init; }

    /// <summary>How many event streams to append.</summary>
    public int Streams { get; init; }

    /// <summary>The fewest events a generated stream carries.</summary>
    public int MinEventsPerStream { get; init; } = 4;

    /// <summary>The most events a generated stream carries.</summary>
    public int MaxEventsPerStream { get; init; } = 6;

    /// <summary>Documents per <c>BulkInsertAsync</c> call. One COPY and one transaction per batch.</summary>
    public int DocumentBatchSize { get; init; } = 5_000;

    /// <summary>Streams per session. One <c>SaveChangesAsync</c> per batch, so inline projections run.</summary>
    public int StreamBatchSize { get; init; } = 500;

    /// <summary>
    /// Every n-th stream carries the SKU <c>ShipmentTracker</c> throws on, so dead letters accumulate in
    /// proportion to the data rather than staying at the seeder's single row.
    /// </summary>
    public int PoisonStreamInterval { get; init; } = 10_000;

    /// <summary>
    /// One ~2 MB <see cref="Documents.MediaAsset" /> per this many other documents, so that every list
    /// view has a collection it must be seen <em>not</em> fetching.
    /// </summary>
    public int LargeDocumentInterval { get; init; } = 100_000;

    /// <summary>
    /// How many generated documents get extra JSON keys written behind Marten's back, so the document
    /// editor's round-trip diff (D7) has real property loss to report.
    /// </summary>
    public int DriftedDocuments { get; init; } = 8;

    /// <summary>Whether to <c>analyze</c> the generated tables at the end. See the remarks.</summary>
    /// <remarks>
    /// <c>pg_class.reltuples</c> is <c>-1</c> until something analyses the table, and D8 makes that
    /// estimate the number every collection rail shows. A million rows that autovacuum has not reached
    /// yet therefore render as "~0", which is not a studio bug but does make the generated data look
    /// like it never arrived. One <c>analyze</c> at the end of a run costs a few seconds and makes the
    /// screens honest.
    /// </remarks>
    public bool AnalyzeWhenDone { get; init; } = true;

    /// <summary>The documents this plan writes, the big ones included.</summary>
    public long DocumentTarget => NamedDocuments + MediaAssets;

    /// <summary>How many ~2 MB documents fall out of <see cref="LargeDocumentInterval" />.</summary>
    public int MediaAssets => LargeDocumentInterval <= 0
        ? 0
        : (int) Math.Min(int.MaxValue, NamedDocuments / LargeDocumentInterval);

    /// <summary>
    /// How many streams carry the poisoned item: every n-th one, counting from the first, so stream zero
    /// is never poisoned and the last index is <see cref="Streams" /> minus one.
    /// </summary>
    public int PoisonedStreams => PoisonStreamInterval <= 0 || Streams <= 1
        ? 0
        : (Streams - 1) / PoisonStreamInterval;

    /// <summary>The events this plan appends, poison events included.</summary>
    public long EventTarget
    {
        get
        {
            if (Streams <= 0)
            {
                return 0;
            }

            int span = Math.Max(1, MaxEventsPerStream - MinEventsPerStream + 1);
            long whole = Streams / span;
            int remainder = Streams % span;

            long perCycle = 0;
            for (int k = 0; k < span; k++)
            {
                perCycle += MinEventsPerStream + k;
            }

            long tail = 0;
            for (int k = 0; k < remainder; k++)
            {
                tail += MinEventsPerStream + k;
            }

            return (whole * perCycle) + tail + PoisonedStreams;
        }
    }

    /// <summary>Documents and events together - what "rows per second" is measured against.</summary>
    public long RowTarget => DocumentTarget + EventTarget;

    private long NamedDocuments =>
        (long) Customers + Orders + Invoices + Products + Vehicles + AuditNotes;

    /// <summary>The preset for <paramref name="size" />, with <paramref name="seed" /> applied.</summary>
    /// <param name="size">Which preset. <see cref="DemoDataSize.Custom" /> returns an empty plan.</param>
    /// <param name="seed">The content seed.</param>
    public static DemoDataPlan For(DemoDataSize size, int seed = 20260914) => size switch
    {
        // The seeder's own numbers, so "Small" is a way to put a demo-sized set into an empty database
        // without running the host's IInitialData.
        DemoDataSize.Small => new DemoDataPlan
        {
            Size = size,
            Seed = seed,
            Customers = 25,
            Orders = 15,
            SoftDeletedOrders = 3,
            Invoices = 6,
            Products = 12,
            Vehicles = 10,
            AuditNotes = 8,
            Streams = 25,
            MinEventsPerStream = 2,
            MaxEventsPerStream = 4,
            PoisonStreamInterval = 13,
            DriftedDocuments = 2,
        },

        // ~100 000 documents and ~100 000 events: big enough that keyset paging and the reltuples
        // estimate start to matter, small enough to run in the always-on integration test.
        DemoDataSize.Medium => new DemoDataPlan
        {
            Size = size,
            Seed = seed,
            Customers = 60_000,
            Orders = 20_000,
            SoftDeletedOrders = 2_000,
            Invoices = 10_000,
            Products = 5_000,
            Vehicles = 5_000,
            AuditNotes = 0,
            Streams = 20_000,
            MinEventsPerStream = 4,
            MaxEventsPerStream = 6,
            PoisonStreamInterval = 2_000,
            DriftedDocuments = 8,
        },

        // ~1.2 million documents and ~1.2 million events. This is the set the responsiveness budgets
        // are about; nothing smaller distinguishes an index from a sequential scan.
        DemoDataSize.Large => new DemoDataPlan
        {
            Size = size,
            Seed = seed,
            Customers = 600_000,
            Orders = 300_000,
            SoftDeletedOrders = 30_000,
            Invoices = 150_000,
            Products = 50_000,
            Vehicles = 100_000,
            AuditNotes = 5_000,
            Streams = 250_000,
            MinEventsPerStream = 4,
            MaxEventsPerStream = 6,
            PoisonStreamInterval = 10_000,
            DriftedDocuments = 8,
        },

        _ => new DemoDataPlan { Size = DemoDataSize.Custom, Seed = seed },
    };

    /// <summary>Parses a size name from a form field, falling back to <see cref="DemoDataSize.Small" />.</summary>
    /// <param name="value">The posted value.</param>
    public static DemoDataSize ParseSize(string? value) =>
        Enum.TryParse(value, ignoreCase: true, out DemoDataSize size) ? size : DemoDataSize.Small;

    /// <summary>
    /// Refuses a plan that would take the host down rather than stress it.
    /// </summary>
    /// <returns>The complaint, or <see langword="null" /> when the plan is runnable.</returns>
    /// <remarks>
    /// A custom plan comes straight from a form, so the ceiling is here rather than in the endpoint: a
    /// typo that asks for ten billion customers should be a message on the page, not a disk that fills
    /// up overnight.
    /// </remarks>
    public string? Validate()
    {
        if (Customers < 0 || Orders < 0 || Invoices < 0 || Products < 0 || Vehicles < 0
            || AuditNotes < 0 || Streams < 0)
        {
            return "Counts cannot be negative.";
        }

        if (SoftDeletedOrders > Orders)
        {
            return "There cannot be more soft-deleted orders than orders.";
        }

        if (MinEventsPerStream < 1 || MaxEventsPerStream < MinEventsPerStream)
        {
            return "Events per stream must be at least one, and the maximum cannot be below the minimum.";
        }

        if (DocumentBatchSize is < 1 or > 50_000 || StreamBatchSize is < 1 or > 10_000)
        {
            return "Batch sizes are out of range.";
        }

        if (RowTarget > MaxRows)
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "That plan would write {0:N0} rows. The generator refuses above {1:N0}; run it twice if you really want more.",
                RowTarget,
                MaxRows);
        }

        return null;
    }
}
