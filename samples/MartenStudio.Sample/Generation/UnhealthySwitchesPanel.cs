using System.Globalization;
using System.Net;
using System.Text;

using MartenStudio.Sample.Auth;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The "Make it unhealthy" section of the sample's landing page, below the demo-data panel.
/// </summary>
/// <remarks>
/// <para>
/// Plain HTML and a few lines of script, like <see cref="DemoDataPanel" />, and in the same three shapes: the switches,
/// for an admin on a host where they are mapped; a note naming the policy, for anybody else there; and a note naming
/// the switch that maps them, in Production without it. Never silently absent.
/// </para>
/// <para>
/// The page is rendered with every switch's state as it is, so it is right without the script; the script polls
/// <see cref="UnhealthySwitchEndpoints.StatusPath" /> and keeps the state, the detail and the "turns itself off at"
/// line current, and shows the button that fits.
/// </para>
/// </remarks>
internal static class UnhealthySwitchesPanel
{
    /// <summary>Renders the section.</summary>
    /// <param name="enabled">Whether the endpoints are mapped at all.</param>
    /// <param name="isAdmin">Whether the visitor satisfies the admin policy.</param>
    /// <param name="requestToken">The antiforgery request token for this response.</param>
    /// <param name="studioPath">Where the studio is mounted, for the link to its Health page.</param>
    /// <param name="message">The one-line result of the last POST, from the query string.</param>
    /// <param name="board">The switches and their state.</param>
    public static string Render(bool enabled, bool isAdmin, string requestToken, string studioPath, string? message, UnhealthySwitchBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);

        string heading = "<h2 id=\"" + UnhealthySwitchEndpoints.Anchor + "\">Make it unhealthy</h2>";

        if (!enabled)
        {
            return heading + """
                <p>
                  This host has switches that make its database unhealthy on purpose - a pinned high-water mark, a session
                  idle in a transaction, a bloated table and a hogged connection pool - so the studio's Health page has
                  something real to find. They are off here: they appear in <code>Development</code>, or when the host is
                  started with <code>--allow-data-generation</code>.
                </p>
                """;
        }

        if (!isAdmin)
        {
            return heading + """
                <p>
                  The switches that make this database unhealthy on purpose are enabled on this host but need the
                  <code>MartenStudioAdmin</code> policy. Sign in as <code>admin</code> to see them.
                </p>
                """;
        }

        string health = WebUtility.HtmlEncode(studioPath.TrimEnd('/') + "/health");
        string token = WebUtility.HtmlEncode(requestToken);
        var builder = new StringBuilder(8 * 1024);

        builder.Append(heading);

        if (!string.IsNullOrWhiteSpace(message))
        {
            builder.Append("<p class=\"note\">").Append(WebUtility.HtmlEncode(message)).Append("</p>");
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""
            <p>
              Four switches that put this store's database into a state the studio's <a href="{{health}}">Health page</a>
              exists to explain, on purpose, for a demo or a screenshot.
              Each turns itself off after {{UnhealthySwitchBoard.Describe(UnhealthySwitchBoard.DefaultTimeout)}}, and cleans
              up after itself then, when it is turned off and when the host stops: the transaction rolled back, the
              connections closed, autovacuum restored. All four are off when the host starts. The Health page checks once a minute while it is open.
            </p>
            <p class="note" id="unhealthy-database">{{WebUtility.HtmlEncode(board.Database.Description)}}</p>
            <div class="switches">
            """);

        foreach (UnhealthySwitch @switch in board.Switches)
        {
            UnhealthySwitchStatus status = board.Status(@switch.Kind);
            string slug = WebUtility.HtmlEncode(@switch.Slug);
            string codes = string.Join(" ", @switch.Codes.Select(static code => "<code>" + WebUtility.HtmlEncode(code) + "</code>"));
            string detail = status.IsRunning ? status.Detail ?? string.Empty : status.LastOutcome ?? "Off.";
            string until = status.TurnsOffAt is { } at
                ? "Turns itself off at " + at.UtcDateTime.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC."
                : string.Empty;

            // The board refuses it as well; this only says why, and does not offer the button.
            string? refusal = board.Refusal(@switch.Kind);
            string refused = refusal is null
                ? string.Empty
                : "<p class=\"switch-refused\" data-field=\"refused\">" + WebUtility.HtmlEncode(refusal) + "</p>";
            string disabled = refusal is null ? string.Empty : " disabled";

            builder.Append(CultureInfo.InvariantCulture, $$"""
                <section class="switch" id="unhealthy-{{slug}}" data-switch="{{slug}}" data-state="{{status.State}}">
                  <h3>{{WebUtility.HtmlEncode(@switch.Title)}} <span class="switch-state" data-field="state">{{status.State}}</span></h3>
                  <p>{{WebUtility.HtmlEncode(@switch.Description)}}</p>
                  <p><strong>Look for</strong> {{codes}} on the <a href="{{health}}">Health page</a>. {{WebUtility.HtmlEncode(@switch.WhereToLook)}}</p>
                  <p class="switch-detail" data-field="detail">{{WebUtility.HtmlEncode(detail)}}</p>
                  <p class="switch-until muted" data-field="until"{{(status.IsRunning ? string.Empty : " hidden")}}>{{until}}</p>
                  {{refused}}
                  <form method="post" action="{{UnhealthySwitchEndpoints.OnPath(@switch)}}" data-when="off"{{(status.IsRunning ? " hidden" : string.Empty)}}>
                    <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{token}}" />
                    <button type="submit"{{disabled}}>Turn on</button>
                  </form>
                  <form method="post" action="{{UnhealthySwitchEndpoints.OffPath(@switch)}}" data-when="on"{{(status.IsRunning ? string.Empty : " hidden")}}>
                    <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{token}}" />
                    <button type="submit">Turn off</button>
                  </form>
                </section>
                """);
        }

        builder.Append(CultureInfo.InvariantCulture, $$"""
            </div>
            <script>
              async function pollSwitches() {
                try {
                  const r = await fetch('{{UnhealthySwitchEndpoints.StatusPath}}', { headers: { accept: 'application/json' } });
                  if (!r.ok) { return; }
                  const s = await r.json();
                  for (const sw of s.switches) {
                    const card = document.querySelector('[data-switch="' + sw.slug + '"]');
                    if (!card) { continue; }
                    card.dataset.state = sw.state;
                    card.querySelector('[data-field="state"]').textContent = sw.state;
                    card.querySelector('[data-field="detail"]').textContent = sw.running ? (sw.detail || '') : (sw.lastOutcome || 'Off.');
                    const until = card.querySelector('[data-field="until"]');
                    until.hidden = !sw.running || !sw.turnsOffAt;
                    if (sw.turnsOffAt) {
                      const at = new Date(sw.turnsOffAt);
                      until.textContent = 'Turns itself off at ' + at.toLocaleTimeString() + ' (in ' +
                        Math.max(0, Math.round((at - Date.now()) / 60000)) + ' min).';
                    }
                    card.querySelector('form[data-when="off"]').hidden = sw.running;
                    card.querySelector('form[data-when="on"]').hidden = !sw.running;
                  }
                } catch { /* the host is restarting; the next tick will do */ }
              }
              pollSwitches();
              setInterval(pollSwitches, 2000);
            </script>
            """);

        return builder.ToString();
    }

    /// <summary>The stylesheet the panel needs, folded into the landing page's own <c>&lt;style&gt;</c>.</summary>
    public const string Styles = """
        .switches { display: grid; grid-template-columns: repeat(auto-fit, minmax(min(20rem, 100%), 1fr)); gap: 1rem; }
        .switch { border: 1px solid #cbd5e1; border-radius: 4px; padding: .25rem 1rem .75rem; min-width: 0; overflow-wrap: anywhere; }
        .switch h3 { display: flex; justify-content: space-between; gap: .5rem; align-items: baseline; }
        .switch-state { font-size: .8rem; font-weight: 600; padding: .1rem .5rem; border-radius: 999px; background: #e2e8f0; color: #0f172a; }
        .switch[data-state="On"] .switch-state, .switch[data-state="Starting"] .switch-state { background: #b91c1c; color: #fff; }
        .switch[data-state="Failed"] .switch-state { background: #92400e; color: #fff; }
        .switch-refused { border-left: 3px solid #92400e; padding-left: .5rem; }
        """;
}
