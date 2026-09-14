using System.Globalization;
using System.Net;
using System.Text;

using MartenStudio.Sample.Auth;
using MartenStudio.SampleDomain.Generation;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The "Demo data" section of the sample's landing page.
/// </summary>
/// <remarks>
/// <para>
/// Plain HTML and about twenty lines of inline script, matching the rest of this host. There is no
/// framework here on purpose: the landing page is a string, the studio is the thing worth looking at,
/// and a build step for a progress bar would be a build step for a progress bar.
/// </para>
/// <para>
/// The section is rendered in one of three shapes: the panel, for an admin on a host where generation is
/// enabled; a one-line note saying which switch would turn it on, for anybody else on such a host; and a
/// one-line note saying it is off, in Production without the switch. It is never silently absent -
/// somebody reading the page should be able to tell that the feature exists and why they cannot see it.
/// </para>
/// </remarks>
internal static class DemoDataPanel
{
    /// <summary>
    /// Renders the section.
    /// </summary>
    /// <param name="enabled">Whether the endpoints are mapped at all.</param>
    /// <param name="isAdmin">Whether the visitor satisfies the admin policy.</param>
    /// <param name="requestToken">The antiforgery request token for this response.</param>
    /// <param name="message">The one-line result of the last POST, from the query string.</param>
    public static string Render(bool enabled, bool isAdmin, string requestToken, string? message)
    {
        if (!enabled)
        {
            return """
                <h2 id="demo-data">Demo data</h2>
                <p>
                  This host can generate a large demo data set - up to about 1.2 million documents and as
                  many events - so that the studio can be exercised at production scale. It is off here:
                  the panel appears in <code>Development</code>, or when the host is started with
                  <code>--allow-data-generation</code>.
                </p>
                """;
        }

        if (!isAdmin)
        {
            return """
                <h2 id="demo-data">Demo data</h2>
                <p>
                  Demo-data generation is enabled on this host but needs the <code>MartenStudioAdmin</code>
                  policy. Sign in as <code>admin</code> to see the panel.
                </p>
                """;
        }

        var builder = new StringBuilder(8 * 1024);

        builder.Append("""<h2 id="demo-data">Demo data</h2>""");

        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.Append("<p class=\"note\">").Append(WebUtility.HtmlEncode(message)).Append("</p>");
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""
            <p>
              Writes documents into every demo collection and appends order streams, which the inline
              <code>OrderSummary</code> projection consumes as they are written and the async daemon
              catches up on afterwards. Every generated document carries a <code>GeneratedRun</code>
              marker, so <em>Truncate</em> removes exactly what was generated and leaves the seeder's
              own demo data alone. One stream in {{DemoDataPlan.For(DemoDataSize.Large).PoisonStreamInterval:N0}}
              carries the SKU <code>ShipmentTracker</code> throws on, so dead letters accumulate.
            </p>
            <form method="post" action="{{DemoDataEndpoints.GeneratePath}}" id="demo-generate">
              <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(requestToken)}}" />
              <fieldset>
                <legend>Size</legend>
            """);

        foreach (DemoDataSize size in new[] { DemoDataSize.Small, DemoDataSize.Medium, DemoDataSize.Large })
        {
            DemoDataPlan preset = DemoDataPlan.For(size);
            builder.Append(string.Format(
                CultureInfo.InvariantCulture,
                """
                <label class="radio"><input type="radio" name="size" value="{0}"{1} />
                  <strong>{0}</strong> &mdash; {2:N0} documents, {3:N0} events, {4:N0} streams</label>
                """,
                size,
                size == DemoDataSize.Medium ? " checked" : string.Empty,
                preset.DocumentTarget,
                preset.EventTarget,
                preset.Streams));
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""
                <label class="radio"><input type="radio" name="size" value="custom" />
                  <strong>Custom</strong> &mdash; the boxes below (a preset ignores them)</label>
              </fieldset>
              <div class="counts">
                {{NumberField("customers", "Customers", 60000)}}
                {{NumberField("orders", "Orders", 20000)}}
                {{NumberField("softDeletedOrders", "of which soft-deleted", 2000)}}
                {{NumberField("invoices", "Invoices (across tenants)", 10000)}}
                {{NumberField("products", "Products", 5000)}}
                {{NumberField("vehicles", "Vehicles (cars + trucks)", 5000)}}
                {{NumberField("auditNotes", "Audit notes", 0)}}
                {{NumberField("streams", "Event streams", 20000)}}
                {{NumberField("minEvents", "Events per stream, min", 4)}}
                {{NumberField("maxEvents", "Events per stream, max", 6)}}
                {{NumberField("seed", "Content seed", 20260914)}}
              </div>
              <button type="submit">Generate</button>
            </form>
            <form method="post" action="{{DemoDataEndpoints.CancelPath}}" class="inline">
              <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(requestToken)}}" />
              <button type="submit">Cancel the running job</button>
            </form>

            <h3>Progress</h3>
            <div class="bar"><div class="bar-fill" id="demo-bar"></div></div>
            <p id="demo-status">Loading&hellip;</p>
            <p id="demo-daemon" class="muted"></p>

            <h3>Truncate generated data</h3>
            <p>
              Deletes every document carrying a <code>GeneratedRun</code> marker and archives the streams
              the generator appended. Marten has no "delete these streams" operation at all, so archiving
              is the honest stream-level answer; to remove the event rows themselves, type
              <code>{{DemoDataEndpoints.DeleteAllEventDataConfirmation}}</code> below - which deletes
              <strong>every</strong> event in this store, the seeded demo streams included, and resets
              every projection's progress.
            </p>
            <form method="post" action="{{DemoDataEndpoints.TruncatePath}}" class="inline">
              <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(requestToken)}}" />
              <input name="confirmEventData" placeholder="(leave empty to archive instead)" size="28" />
              <button type="submit">Truncate</button>
            </form>

            <script>
              const fmt = n => Number(n).toLocaleString();
              const secs = s => s == null ? '-' : (s < 90 ? s.toFixed(0) + 's' : (s / 60).toFixed(1) + 'm');
              async function poll() {
                try {
                  const r = await fetch('{{DemoDataEndpoints.StatusPath}}', { headers: { accept: 'application/json' } });
                  if (!r.ok) { return; }
                  const s = await r.json();
                  document.getElementById('demo-bar').style.width = (s.fraction * 100).toFixed(1) + '%';
                  document.getElementById('demo-status').textContent =
                    s.state + (s.kind === 'None' ? '' : ' (' + s.kind + ')') + ' - ' + s.phase +
                    ' - ' + fmt(s.documents) + '/' + fmt(s.documentTarget) + ' documents, ' +
                    fmt(s.events) + '/' + fmt(s.eventTarget) + ' events, ' +
                    fmt(s.streams) + ' streams' + (s.streamsArchived ? ', ' + fmt(s.streamsArchived) + ' archived' : '') +
                    ' - ' + fmt(s.rowsPerSecond) + ' rows/s, elapsed ' + secs(s.elapsedSeconds) +
                    ', eta ' + secs(s.etaSeconds) + (s.error ? ' - ' + s.error : '') +
                    (s.runId ? ' - run ' + s.runId : '');
                  document.getElementById('demo-daemon').textContent = s.daemonAvailable
                    ? 'Async daemon: high-water ' + fmt(s.highWaterMark) + ', worst shard ' + fmt(s.maxProjectionLag) +
                      ' events behind across ' + s.shardCount + ' shards.'
                    : 'Async daemon: no progression rows yet.';
                } catch { /* the host is restarting; the next tick will do */ }
              }
              poll();
              setInterval(poll, 1000);
            </script>
            """);

        return builder.ToString();
    }

    /// <summary>The stylesheet the panel needs, folded into the landing page's own <c>&lt;style&gt;</c>.</summary>
    public const string Styles = """
        fieldset { border: 1px solid #cbd5e1; border-radius: 4px; margin: 1rem 0; }
        legend { font-weight: 600; padding: 0 .4rem; }
        label.radio { display: block; margin: .25rem 0; }
        .counts { display: grid; grid-template-columns: repeat(auto-fit, minmax(15rem, 1fr)); gap: .4rem 1rem; }
        .counts label { display: flex; justify-content: space-between; gap: .5rem; align-items: baseline; }
        .counts input { width: 8rem; font: inherit; }
        form.inline { display: inline-flex; gap: .5rem; margin: .25rem 0 1rem; }
        button { font: inherit; padding: .4rem .9rem; }
        .bar { height: .75rem; background: #e2e8f0; border-radius: 999px; overflow: hidden; }
        .bar-fill { height: 100%; width: 0; background: #2563eb; transition: width .3s linear; }
        .muted { color: #64748b; }
        .note { background: #f1f5f9; border-left: 3px solid #2563eb; padding: .5rem .75rem; }
        """;

    private static string NumberField(string name, string label, int value) => string.Format(
        CultureInfo.InvariantCulture,
        """<label>{1}<input type="number" name="{0}" value="{2}" min="0" /></label>""",
        name,
        WebUtility.HtmlEncode(label),
        value);
}
