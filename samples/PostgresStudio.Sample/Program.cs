using System.Net;
using System.Security.Claims;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;

using Npgsql;

using PostgresStudio;
using PostgresStudio.Sample;
using PostgresStudio.Sample.Auth;
using PostgresStudio.SampleData;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// No silent localhost fallback: either something configured a database, or a throwaway container is started, or the
// host refuses (D18). The configured database is the admin database; the tenant databases are created beside it.
string? connectionString = builder.Configuration.GetConnectionString("Postgres");
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
        options.Cookie.Name = "postgres-studio-sample";
    });

builder.Services.AddSamplePolicies();

#region readme_register
// The application's own data source - what AddNpgsqlDataSource(connectionString) from Npgsql.DependencyInjection
// registers, written out here so the sample needs no other package. Postgres Studio finds the unkeyed one by itself and
// lists it as the source "default". It reads through it, with its role, its TLS and its settings, and never holds a
// connection string of its own.
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

// One database per tenant, listed by the application itself from a directory in the admin database.
builder.Services.AddSingleton(services =>
    new SampleTenantDatabases(services.GetRequiredService<NpgsqlDataSource>(), connectionString));

builder.Services.AddPostgresStudio(options =>
{
    // Who gets in, and who may use each capability: the studio evaluates both against the host's own authorization,
    // and never authenticates anybody itself. The write policy is asked about each capability by name.
    options.AuthorizationPolicy = SamplePolicies.Studio;
    options.WriteAuthorizationPolicy = SamplePolicies.StudioCapability;

    // All three are off until the host says otherwise: reading rows and definitions, other sessions' statements, and
    // the SQL console. `All()` is the one-line, greppable opt-in; `ReadOnly` overrides it whatever it says.
    options.Capabilities = PostgresStudioCapabilities.All();

    // `public` is browsable only while nothing names a schema: a list replaces that default rather than adding to it, so
    // `public` - where every tenant database keeps its tables - is named first. The others are the admin database's:
    // Quartz.NET's job store, a legacy schema of catalog edge cases, and the application's own tables. "*" would open
    // every schema the role can use - fine for a scratch database, not for a shared one.
    options.BrowsableSchemas.Add("public");
    options.BrowsableSchemas.Add(RelationalDemoSchema.QuartzSchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.LegacySchemaName);
    options.BrowsableSchemas.Add(RelationalDemoSchema.SampleSchemaName);

    // The tenant databases, as a second source beside "default": the studio lists them through the application, and
    // asks it for a tenant's data source only when somebody opens that tenant.
    options.AddDatabaseSource("tenants", static services => services.GetRequiredService<SampleTenantDatabases>());
});
#endregion

#region readme_health
// Optional, and AddPostgresStudio() does not do it for you: the health advisor's checks as one ASP.NET Core health
// check. It reports codes and counts, never a name, and runs the checks at most once per HealthInterval however often
// the endpoint is probed.
builder.Services.AddHealthChecks().AddPostgresStudio(tags: ["postgres"]);
#endregion

WebApplication app = builder.Build();

// The demo data, before the first request: the tenant databases and the admin database's directory of them, then
// Quartz.NET's job store and the legacy schema beside it. Both are idempotent, so starting again writes nothing, and
// neither writes into a database or schema it did not create.
ILogger seeding = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("PostgresStudio.Sample.Seeding");
await TenantDemo.ApplyAsync(connectionString, seeding);
await using (NpgsqlConnection adminConnection = await app.Services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
{
    await RelationalDemoSchema.ApplyAsync(adminConnection, seeding);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

#region readme_map
app.MapPostgresStudio().RequireAuthorization(SamplePolicies.Studio);
#endregion

app.MapLogin();

// The default response writer prints only the status word; the codes and counts are in the report's data, for a writer
// the host chooses. Anonymous, because an orchestrator's probe signs in as nobody.
app.MapHealthChecks("/healthz").AllowAnonymous();

// A browser asks for /favicon.ico on the first page that names no icon of its own, and the studio's pages name none.
app.MapGet("/favicon.ico", static () => Results.NoContent()).AllowAnonymous();

app.MapGet("/", (HttpContext context, IAntiforgery antiforgery) =>
{
    ClaimsPrincipal user = context.User;
    string who = user.Identity?.IsAuthenticated == true
        ? $"Signed in as <strong>{WebUtility.HtmlEncode(user.Identity.Name)}</strong> with {WebUtility.HtmlEncode(string.Join(", ", user.FindAll(SamplePolicies.ScopeClaim).Select(claim => claim.Value)))}."
        : "Not signed in.";

    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);

    context.Response.ContentType = "text/html; charset=utf-8";
    return context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>Postgres Studio sample</title>
          <link rel="icon" href="data:," />
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 48rem; line-height: 1.5; padding: 0 1rem; }
            code { background: #f1f5f9; padding: .1rem .3rem; border-radius: 3px; }
          </style>
        </head>
        <body>
          <h1>Postgres Studio sample</h1>
          <p>{{who}}</p>
          <ul>
            <li><a href="/postgres">Open Postgres Studio</a> &mdash; mounted at <code>/postgres</code>, with the admin
              database as the source <code>default</code> and <code>tenant_a</code>, <code>tenant_b</code> and
              <code>tenant_c</code> under the source <code>tenants</code></li>
            <li><a href="/login">Sign in</a> as <code>admin</code>, <code>ops</code> or <code>viewer</code> (the password is the user name)</li>
          </ul>
          <form method="post" action="/logout">
            <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}" />
            <button type="submit">Sign out</button>
          </form>
        </body>
        </html>
        """);
}).AllowAnonymous();

await app.RunAsync();
