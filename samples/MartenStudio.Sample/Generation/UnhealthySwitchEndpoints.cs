using System.Net;

using MartenStudio.Sample.Auth;

using Microsoft.AspNetCore.Antiforgery;

namespace MartenStudio.Sample.Generation;

/// <summary>
/// The endpoints behind the "Make it unhealthy" panel: turn a switch on, turn it off, and the state the panel polls.
/// </summary>
/// <remarks>
/// <para>
/// <b>The same three gates as the demo-data endpoints</b>, and for the same reason (<see cref="DemoDataEndpoints" />):
/// mapped only in Development or with <c>--allow-data-generation</c>, every one behind the <c>MartenStudioAdmin</c>
/// policy that only the <c>admin</c> demo user satisfies, and every POST antiforgery-validated. A button that pins a
/// production event store's projections is not something a cross-site page should be able to press, sample or not.
/// </para>
/// <para>
/// Turning a switch on returns at once - the run belongs to <see cref="UnhealthySwitchBoard" />, not to the request;
/// turning it off waits, for a bounded time, until its clean-up is done, so the page that reloads afterwards shows it off.
/// </para>
/// </remarks>
internal static class UnhealthySwitchEndpoints
{
    /// <summary>Where the switches live: <c>{BasePath}/{slug}/on</c> and <c>…/off</c>.</summary>
    public const string BasePath = "/sample/unhealthy";

    /// <summary>Where the panel polls.</summary>
    public const string StatusPath = BasePath + "/status";

    /// <summary>The query-string key the landing page reads the panel's one-line result out of.</summary>
    public const string MessageQueryKey = "unhealthy";

    /// <summary>The anchor of the panel on the landing page.</summary>
    public const string Anchor = "unhealthy";

    /// <summary>Where a switch is turned on.</summary>
    public static string OnPath(UnhealthySwitch @switch) => BasePath + "/" + Required(@switch).Slug + "/on";

    /// <summary>Where a switch is turned off.</summary>
    public static string OffPath(UnhealthySwitch @switch) => BasePath + "/" + Required(@switch).Slug + "/off";

    /// <summary>Maps the three endpoints, all behind the admin policy.</summary>
    /// <param name="app">The endpoint builder.</param>
    /// <remarks>Call it only where <see cref="SampleOptions.DataGenerationEnabled" /> says so, beside <c>MapDemoData</c>.</remarks>
    public static void MapUnhealthySwitches(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.MapPost(BasePath + "/{slug}/on", async (string slug, HttpContext context, IAntiforgery antiforgery, UnhealthySwitchBoard board) =>
        {
            if (!await DemoDataEndpoints.IsRequestValidAsync(context, antiforgery).ConfigureAwait(false))
            {
                return;
            }

            if (board.Find(slug) is not { } @switch)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            Redirect(context, board.TryTurnOn(@switch.Kind, out string? error)
                ? @switch.Title + " is on. It turns itself off in " + UnhealthySwitchBoard.Describe(UnhealthySwitchBoard.DefaultTimeout)
                  + "; look for " + string.Join(", ", @switch.Codes) + " on the Health page."
                : error ?? "Refused.");
        }).RequireAuthorization(SamplePolicies.StudioAdmin);

        app.MapPost(BasePath + "/{slug}/off", async (string slug, HttpContext context, IAntiforgery antiforgery, UnhealthySwitchBoard board) =>
        {
            if (!await DemoDataEndpoints.IsRequestValidAsync(context, antiforgery).ConfigureAwait(false))
            {
                return;
            }

            if (board.Find(slug) is not { } @switch)
            {
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            bool finished = await board.TurnOffAsync(@switch.Kind).ConfigureAwait(false);
            UnhealthySwitchStatus after = board.Status(@switch.Kind);

            // A run whose clean-up failed has finished too, and is not "cleaned up": say what the board says about it.
            Redirect(context, !finished
                ? @switch.Title + " is still cleaning up; the panel says when it is done."
                : after.State == UnhealthySwitchState.Failed
                    ? @switch.Title + ": " + (after.LastOutcome ?? "Failed.")
                    : @switch.Title + " is off, and cleaned up.");
        }).RequireAuthorization(SamplePolicies.StudioAdmin);

        app.MapGet(StatusPath, (UnhealthySwitchBoard board) => Results.Json(UnhealthySwitchesStatus.From(board)))
            .RequireAuthorization(SamplePolicies.StudioAdmin);
    }

    private static void Redirect(HttpContext context, string message) =>
        context.Response.Redirect("/?" + MessageQueryKey + "=" + WebUtility.UrlEncode(message) + "#" + Anchor);

    private static UnhealthySwitch Required(UnhealthySwitch @switch)
    {
        ArgumentNullException.ThrowIfNull(@switch);
        return @switch;
    }
}

/// <summary>The JSON the panel polls: every switch, flat.</summary>
/// <param name="Switches">One entry per switch, in the panel's order.</param>
internal sealed record UnhealthySwitchesStatus(IReadOnlyList<UnhealthySwitchJson> Switches)
{
    /// <summary>The board's state now.</summary>
    public static UnhealthySwitchesStatus From(UnhealthySwitchBoard board)
    {
        ArgumentNullException.ThrowIfNull(board);

        return new UnhealthySwitchesStatus(
        [
            .. board.Switches.Select(x =>
            {
                UnhealthySwitchStatus status = board.Status(x.Kind);

                return new UnhealthySwitchJson(
                    x.Slug,
                    x.Title,
                    status.State.ToString(),
                    status.IsRunning,
                    status.Detail,
                    status.StartedAt,
                    status.TurnsOffAt,
                    status.LastOutcome,
                    x.Codes,
                    board.Refusal(x.Kind));
            }),
        ]);
    }
}

/// <summary>One switch, as the panel's script reads it.</summary>
/// <param name="Slug">Its URL name.</param>
/// <param name="Title">Its name.</param>
/// <param name="State">Off, Starting, On, Stopping or Failed.</param>
/// <param name="Running">Whether anything is held, or about to be.</param>
/// <param name="Detail">What it is doing, while it runs.</param>
/// <param name="StartedAt">When it was last turned on.</param>
/// <param name="TurnsOffAt">When it turns itself off, while it runs.</param>
/// <param name="LastOutcome">How its last run ended.</param>
/// <param name="Codes">The health check codes to look for.</param>
/// <param name="Refused">Why it may not be turned on here, or <see langword="null" />.</param>
internal sealed record UnhealthySwitchJson(
    string Slug,
    string Title,
    string State,
    bool Running,
    string? Detail,
    DateTimeOffset? StartedAt,
    DateTimeOffset? TurnsOffAt,
    string? LastOutcome,
    IReadOnlyList<string> Codes,
    string? Refused);
