using System.Net;
using System.Security.Claims;

using Marten;

using MartenStudio;
using MartenStudio.Sample;
using MartenStudio.Sample.Auth;
using MartenStudio.SampleDomain;

using Microsoft.AspNetCore.Authentication.Cookies;

SampleOptions sample = SampleOptions.Parse(args);

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

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

#region readme_register
builder.Services
    .AddMarten(options => SampleStore.Configure(options, connectionString))
    .UseLightweightSessions()
    .InitializeWith(new SampleDataSeeder());

builder.Services.AddMartenStudio(options =>
{
    // Who gets in, and who may change anything: the studio evaluates both against the host's own
    // authorization, and never authenticates anybody itself.
    options.AuthorizationPolicy = SamplePolicies.Studio;
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

app.MapGet("/", (HttpContext context) =>
{
    ClaimsPrincipal user = context.User;
    string who = user.Identity?.IsAuthenticated == true
        ? $"Signed in as <strong>{WebUtility.HtmlEncode(user.Identity.Name)}</strong> with {WebUtility.HtmlEncode(string.Join(", ", user.FindAll(SamplePolicies.ScopeClaim).Select(claim => claim.Value)))}."
        : "Not signed in.";

    string studioPath = sample.Path is { Length: > 0 } mounted ? mounted : "/marten";
    string mode = sample.Anonymous ? "anonymous (no policy on the mapping)" : "behind the MartenStudio policy";
    string capabilities = sample.ReadOnly ? "read-only (ReadOnly = true)" : "all capabilities enabled";

    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Marten Studio sample</title>
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 42rem; line-height: 1.5; }
            code { background: #f1f5f9; padding: .1rem .3rem; border-radius: 3px; }
          </style>
        </head>
        <body>
          <h1>Marten Studio sample</h1>
          <p>{{who}}</p>
          <ul>
            <li><a href="{{WebUtility.HtmlEncode(studioPath)}}">Open Marten Studio</a> &mdash; mounted at <code>{{WebUtility.HtmlEncode(studioPath)}}</code>, {{mode}}, {{capabilities}}</li>
            <li><a href="/login">Sign in</a> as <code>admin</code>, <code>ops</code> or <code>viewer</code> (the password is the user name)</li>
          </ul>
          <form method="post" action="/logout"><button type="submit">Sign out</button></form>
          <p>Switches: <code>--anonymous</code>, <code>--readonly</code>, <code>--path /ops/marten</code>.</p>
        </body>
        </html>
        """);
}).AllowAnonymous();

await app.RunAsync();
