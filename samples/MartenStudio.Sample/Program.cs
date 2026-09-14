using System.Net;
using System.Security.Claims;

using Marten;

using MartenStudio;
using MartenStudio.Sample;
using MartenStudio.Sample.Auth;
using MartenStudio.Sample.Generation;
using MartenStudio.SampleDomain;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;

SampleOptions sample = SampleOptions.Parse(args);

// Not `args`: a bare `--anonymous` lets the command-line configuration provider bind the switch to the
// *next* token and then drop what is left, so `--anonymous --urls http://localhost:5210` silently lost
// the URL. SampleOptions.HostArguments rewrites the demo's own switches to `--key=value` and passes
// every other argument through untouched. See the note on SampleOptions.
WebApplicationBuilder builder = WebApplication.CreateBuilder(SampleOptions.HostArguments(args));

// Started before AddMarten, because AddMarten needs the connection string. No silent localhost fallback:
// either something configured a database, or a throwaway container is started, or the host refuses (D18).
string? connectionString = builder.Configuration.GetConnectionString("Marten");
if (string.IsNullOrWhiteSpace(connectionString))
{
    connectionString = await EphemeralPostgres.StartAsync();
}

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "marten-studio-sample";
    });

builder.Services.AddAuthorization(SamplePolicies.Configure);

// One demo-data job per process, whether or not the endpoints that drive it are mapped: the singleton
// is also what stops a run when the host shuts down, and registering it conditionally would make that
// depend on a command-line switch.
builder.Services.AddSingleton<DemoDataJob>();
builder.Services.AddHostedService(static services => services.GetRequiredService<DemoDataJob>());

#region readme_register
builder.Services
    .AddMarten(options => SampleStore.Configure(options, connectionString))
    .UseLightweightSessions()
    .AddAsyncDaemon(JasperFx.Events.Daemon.DaemonMode.Solo)
    .InitializeWith(new SampleDataSeeder());

builder.Services.AddMartenStudio(options =>
{
    // Who gets in, and who may change anything: the studio evaluates both against the host's own
    // authorization, and never authenticates anybody itself.
    //
    // --anonymous leaves AuthorizationPolicy unset on purpose. AllowAnonymous() on the mapping does win
    // over a configured policy - the studio's endpoints carry both and AllowAnonymous is what the
    // authorization middleware honours - but saying "this policy governs the studio" and then "anyone
    // may open it" in the same application is two answers to one question, and a demo should show the
    // one it means.
    options.AuthorizationPolicy = sample.Anonymous ? null : SamplePolicies.Studio;
    options.WriteAuthorizationPolicy = SamplePolicies.StudioWrite;

    // Every mutating operation is off until the host says otherwise. `All()` is the one-line, greppable
    // opt-in; `ReadOnly` overrides it whatever it says.
    options.Capabilities = sample.ReadOnly ? new MartenStudioCapabilities() : MartenStudioCapabilities.All();
    options.ReadOnly = sample.ReadOnly;

    // The demo's invoices are conjoined multi-tenant. Naming the tenants here is the cheapest of the
    // three discovery tiers and the only one that can answer before any events have been written.
    foreach (string tenantId in SampleStore.TenantIds)
    {
        options.KnownTenantIds.Add(tenantId);
    }
});
#endregion

WebApplication app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

#region readme_map
IEndpointConventionBuilder studio = sample.Path is { Length: > 0 } path
    ? app.MapMartenStudio(path)
    : app.MapMartenStudio();

if (sample.Anonymous)
{
    // --anonymous: what an unauthenticated studio looks like. The startup guard accepts this because it
    // is an answer; it refuses only a mapping that says nothing at all.
    studio.AllowAnonymous();
}
else
{
    studio.RequireAuthorization(SamplePolicies.Studio);
}
#endregion

app.MapLogin();

// Development, or --allow-data-generation, and nothing else. Outside that the endpoints are not mapped
// at all, so a POST is a 404 rather than a 403 - there is nothing there to refuse.
bool dataGeneration = sample.DataGenerationEnabled(app.Environment);
if (dataGeneration)
{
    app.MapDemoData();
}

app.MapGet("/", async (HttpContext context, IAntiforgery antiforgery, IAuthorizationService authorization) =>
{
    ClaimsPrincipal user = context.User;
    string who = user.Identity?.IsAuthenticated == true
        ? $"Signed in as <strong>{WebUtility.HtmlEncode(user.Identity.Name)}</strong> with {WebUtility.HtmlEncode(string.Join(", ", user.FindAll(SamplePolicies.ScopeClaim).Select(claim => claim.Value)))}."
        : "Not signed in.";

    string studioPath = sample.Path is { Length: > 0 } mounted ? mounted : "/marten";
    string mode = sample.Anonymous ? "anonymous (no policy on the mapping)" : "behind the MartenStudio policy";
    string capabilities = sample.ReadOnly ? "read-only (ReadOnly = true)" : "all capabilities enabled";

    // The same warning the studio's own layout draws, on the page that links to it: somebody who lands
    // here in --anonymous mode has to see it before they follow the link, not only after.
    string anonymousBanner = sample.Anonymous
        ? """
          <p style="background:#dc2626;color:#fff;padding:.75rem 1rem;border-radius:4px">
            <strong>This studio is served to anyone (AllowAnonymous).</strong>
            Every document, event stream and schema in this process is readable by anyone who can reach
            this URL. This is what <code>--anonymous</code> demonstrates; it is not a way to run one.
          </p>
          """
        : string.Empty;

    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);

    // The same policy the endpoints require, evaluated here so the page can draw the panel for the one
    // visitor who could use it and say why for everybody else. The gate that matters is still on the
    // endpoint: this only decides what is rendered.
    bool isAdmin = (await authorization.AuthorizeAsync(user, SamplePolicies.StudioAdmin)).Succeeded;

    string demoData = DemoDataPanel.Render(
        dataGeneration,
        isAdmin,
        tokens.RequestToken ?? string.Empty,
        context.Request.Query[DemoDataEndpoints.MessageQueryKey]);

    context.Response.ContentType = "text/html; charset=utf-8";
    await context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Marten Studio sample</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 48rem; line-height: 1.5; padding: 0 1rem; }
            code { background: #f1f5f9; padding: .1rem .3rem; border-radius: 3px; }
        {{DemoDataPanel.Styles}}
          </style>
        </head>
        <body>
          <h1>Marten Studio sample</h1>
          {{anonymousBanner}}
          <p>{{who}}</p>
          <ul>
            <li><a href="{{WebUtility.HtmlEncode(studioPath)}}">Open Marten Studio</a> &mdash; mounted at <code>{{WebUtility.HtmlEncode(studioPath)}}</code>, {{mode}}, {{capabilities}}</li>
            <li><a href="/login">Sign in</a> as <code>admin</code>, <code>ops</code> or <code>viewer</code> (the password is the user name)</li>
          </ul>
          <form method="post" action="/logout">
            <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}" />
            <button type="submit">Sign out</button>
          </form>
          <p>Switches: <code>--anonymous</code>, <code>--readonly</code>, <code>--path /ops/marten</code>,
            <code>--allow-data-generation</code>.</p>
          {{demoData}}
        </body>
        </html>
        """);
}).AllowAnonymous();

await app.RunAsync();
