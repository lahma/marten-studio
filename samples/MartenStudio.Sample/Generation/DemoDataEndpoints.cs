using System.Globalization;
using System.Net;

using Marten;

using MartenStudio.Sample.Auth;
using MartenStudio.SampleDomain.Generation;

using Microsoft.AspNetCore.Antiforgery;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The four endpoints behind the demo-data panel.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three gates, all of them real.</b> The endpoints are only mapped at all when the host is in
/// Development or was started with <c>--allow-data-generation</c>; every one of them requires the
/// <c>MartenStudioAdmin</c> policy, which only the <c>admin</c> demo user satisfies; and every POST
/// validates an antiforgery token, exactly as the sign-in form does. A "generate a million documents"
/// button that a cross-site page could press is a denial-of-service gadget, and the fact that it lives
/// in a sample is not a defence - samples are what people copy.
/// </para>
/// <para>
/// <b>Nothing here waits for the work.</b> The POSTs hand a plan to <see cref="DemoDataJob" /> and
/// redirect; the panel polls <c>GET /sample/generate/status</c>. A request thread that waited for a
/// million-row generation would be a request thread held for minutes and a browser that gave up long
/// before the answer.
/// </para>
/// </remarks>
internal static class DemoDataEndpoints
{
    /// <summary>Where the panel posts, so the landing page and the endpoints cannot disagree.</summary>
    public const string GeneratePath = "/sample/generate";

    /// <summary>Where the panel asks it to stop.</summary>
    public const string CancelPath = "/sample/generate/cancel";

    /// <summary>Where the panel polls.</summary>
    public const string StatusPath = "/sample/generate/status";

    /// <summary>Where the panel asks for generated data to be removed.</summary>
    public const string TruncatePath = "/sample/truncate";

    /// <summary>
    /// Maps the demo-data endpoints, all behind the admin policy.
    /// </summary>
    /// <param name="app">The endpoint builder.</param>
    /// <remarks>
    /// Call this only when <see cref="SampleOptions.DataGenerationEnabled" /> says so. The gate is at the
    /// call site rather than inside, so that a production host does not have the endpoints at all -
    /// answering 404 rather than 403 to something that should not exist.
    /// </remarks>
    public static void MapDemoData(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(GeneratePath, async (HttpContext context, IAntiforgery antiforgery, DemoDataJob job) =>
        {
            if (!await IsRequestValidAsync(context, antiforgery).ConfigureAwait(false))
            {
                return;
            }

            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);
            DemoDataPlan plan = ReadPlan(form);

            Redirect(context, job.TryStartGenerate(plan, out string? error)
                ? "Started: " + plan.Size + ", " + plan.RowTarget.ToString("N0", CultureInfo.InvariantCulture) + " rows."
                : error ?? "Refused.");
        }).RequireAuthorization(SamplePolicies.StudioAdmin);

        app.MapPost(CancelPath, async (HttpContext context, IAntiforgery antiforgery, DemoDataJob job) =>
        {
            if (!await IsRequestValidAsync(context, antiforgery).ConfigureAwait(false))
            {
                return;
            }

            job.Cancel();
            Redirect(context, "Cancelling. The job stops after the batch it is in.");
        }).RequireAuthorization(SamplePolicies.StudioAdmin);

        app.MapPost(TruncatePath, async (HttpContext context, IAntiforgery antiforgery, DemoDataJob job) =>
        {
            if (!await IsRequestValidAsync(context, antiforgery).ConfigureAwait(false))
            {
                return;
            }

            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted).ConfigureAwait(false);

            // The destructive half is opt-in by typing, not by ticking: DeleteAllEventDataAsync takes the
            // seeded demo streams and every projection's progress with it, and there is no narrower
            // cleaner in Marten.
            bool deleteAllEventData = string.Equals(
                form["confirmEventData"].ToString().Trim(),
                DeleteAllEventDataConfirmation,
                StringComparison.Ordinal);

            Redirect(context, job.TryStartTruncate(deleteAllEventData, out string? error)
                ? deleteAllEventData
                    ? "Truncating generated documents and deleting ALL event data."
                    : "Truncating generated documents and archiving generated streams."
                : error ?? "Refused.");
        }).RequireAuthorization(SamplePolicies.StudioAdmin);

        app.MapGet(StatusPath, async (DemoDataJob job, IDocumentStore store, CancellationToken cancellationToken) =>
        {
            DemoDataProgress progress = job.Progress;
            DemoDataDaemonLag lag = await DemoDataDaemonLagReader.ReadAsync(store, cancellationToken)
                .ConfigureAwait(false);

            return Results.Json(new DemoDataStatus(
                progress.State.ToString(),
                progress.Kind.ToString(),
                progress.RunId,
                progress.Size,
                progress.Phase,
                progress.Documents,
                progress.DocumentTarget,
                progress.Events,
                progress.EventTarget,
                progress.Streams,
                progress.StreamTarget,
                progress.StreamsArchived,
                Math.Round(progress.RowsPerSecond, 1),
                Math.Round(progress.Elapsed.TotalSeconds, 1),
                progress.Eta is { } eta ? Math.Round(eta.TotalSeconds, 1) : null,
                Math.Round(progress.Fraction, 4),
                progress.Error,
                lag.Available,
                lag.HighWaterMark,
                lag.MaxLag,
                lag.Shards.Count));
        }).RequireAuthorization(SamplePolicies.StudioAdmin);
    }

    /// <summary>What has to be typed before every event in the store is deleted.</summary>
    public const string DeleteAllEventDataConfirmation = "DELETE ALL EVENT DATA";

    /// <summary>The query-string key the landing page reads its one-line result out of.</summary>
    public const string MessageQueryKey = "demo";

    /// <summary>Turns the posted form into a plan.</summary>
    /// <param name="form">The posted fields.</param>
    /// <remarks>
    /// A preset ignores the count boxes entirely; only <c>custom</c> reads them. That is deliberate -
    /// "Large, except 7 products" is not a size, it is a custom plan, and letting a preset be partly
    /// overridden makes the panel's reported row target a guess.
    /// </remarks>
    internal static DemoDataPlan ReadPlan(IFormCollection form)
    {
        ArgumentNullException.ThrowIfNull(form);

        DemoDataSize size = DemoDataPlan.ParseSize(form["size"]);
        int seed = Field(form, "seed", 20260914);

        if (size != DemoDataSize.Custom)
        {
            return DemoDataPlan.For(size, seed);
        }

        return new DemoDataPlan
        {
            Size = DemoDataSize.Custom,
            Seed = seed,
            Customers = Field(form, "customers", 0),
            Orders = Field(form, "orders", 0),
            SoftDeletedOrders = Field(form, "softDeletedOrders", 0),
            Invoices = Field(form, "invoices", 0),
            Products = Field(form, "products", 0),
            Vehicles = Field(form, "vehicles", 0),
            AuditNotes = Field(form, "auditNotes", 0),
            Streams = Field(form, "streams", 0),
            MinEventsPerStream = Field(form, "minEvents", 4),
            MaxEventsPerStream = Field(form, "maxEvents", 6),
        };
    }

    private static int Field(IFormCollection form, string name, int fallback) =>
        int.TryParse(form[name], NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : fallback;

    private static void Redirect(HttpContext context, string message) =>
        context.Response.Redirect("/?" + MessageQueryKey + "=" + WebUtility.UrlEncode(message) + "#demo-data");

    /// <summary>
    /// Validates the antiforgery token, answering <c>400</c> when it does not hold.
    /// </summary>
    /// <remarks>
    /// Explicitly, for the same reason <c>LoginEndpoints</c> does it explicitly: the middleware only
    /// validates endpoints carrying <c>IAntiforgeryMetadata</c>, which a minimal-API handler that reads
    /// its own form does not get.
    /// </remarks>
    private static async Task<bool> IsRequestValidAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try
        {
            await antiforgery.ValidateRequestAsync(context).ConfigureAwait(false);
            return true;
        }
        catch (AntiforgeryValidationException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(
                "The form could not be verified. Reload the page and try again.",
                context.RequestAborted).ConfigureAwait(false);
            return false;
        }
    }
}

/// <summary>The JSON the panel polls, flat so that twenty lines of script can render it.</summary>
/// <param name="State">Idle, Running, Completed, Cancelled or Failed.</param>
/// <param name="Kind">None, Generate or Truncate.</param>
/// <param name="RunId">The run marker, which is also what truncation looks for.</param>
/// <param name="Size">The preset name.</param>
/// <param name="Phase">What the job is doing.</param>
/// <param name="Documents">Documents written.</param>
/// <param name="DocumentTarget">Documents the plan asks for.</param>
/// <param name="Events">Events appended.</param>
/// <param name="EventTarget">Events the plan asks for.</param>
/// <param name="Streams">Streams started.</param>
/// <param name="StreamTarget">Streams the plan asks for.</param>
/// <param name="StreamsArchived">Streams a truncation has archived.</param>
/// <param name="RowsPerSecond">Rows per second so far.</param>
/// <param name="ElapsedSeconds">How long it has been going.</param>
/// <param name="EtaSeconds">How much longer, at the current rate.</param>
/// <param name="Fraction">How far along, 0 to 1.</param>
/// <param name="Error">Why it failed, or why it was cancelled.</param>
/// <param name="DaemonAvailable">Whether the progression table could be read.</param>
/// <param name="HighWaterMark">The daemon's high-water sequence.</param>
/// <param name="MaxProjectionLag">How far behind the worst shard is.</param>
/// <param name="ShardCount">How many shards there are.</param>
internal sealed record DemoDataStatus(
    string State,
    string Kind,
    string? RunId,
    string Size,
    string Phase,
    long Documents,
    long DocumentTarget,
    long Events,
    long EventTarget,
    long Streams,
    long StreamTarget,
    long StreamsArchived,
    double RowsPerSecond,
    double ElapsedSeconds,
    double? EtaSeconds,
    double Fraction,
    string? Error,
    bool DaemonAvailable,
    long HighWaterMark,
    long MaxProjectionLag,
    int ShardCount);
