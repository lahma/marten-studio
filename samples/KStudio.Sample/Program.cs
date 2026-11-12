using System.Net;

using JasperFx.Events.Daemon;

using Marten;

using KStudio;
using KStudio.Sample;
using KStudio.Sample.Auth;
using KStudio.Sample.Kafka;

using MartenStudio;
using MartenStudio.SampleDomain;

using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;

// The one switch this host understands. It is taken out of the arguments before the host sees them: .NET's command-line provider binds a bare
// `--key` to the token after it, and would swallow whatever follows.
const string AllowDataGenerationSwitch = "--allow-data-generation";
bool allowDataGeneration = args.Contains(AllowDataGenerationSwitch, StringComparer.OrdinalIgnoreCase);
string[] hostArguments = [.. args.Where(static x => !string.Equals(x, AllowDataGenerationSwitch, StringComparison.OrdinalIgnoreCase))];

WebApplicationBuilder builder = WebApplication.CreateBuilder(hostArguments);

// Started before anything that needs them, and with no silent fallback (D18): either something configured a cluster and a database, or a
// throwaway container is started for each, or the host stops and says how to fix it. Kafka first, so that the sentence a missing Docker
// produces is the Kafka one.
await using SampleKafka kafka = await SampleKafka.StartAsync(builder.Configuration);

string? postgresHostAndPort = null;
string? postgres = builder.Configuration.GetConnectionString("Postgres");
if (string.IsNullOrWhiteSpace(postgres))
{
    (postgres, postgresHostAndPort) = await EphemeralPostgres.StartAsync();
}

// A configured Postgres can hold what another build of the Marten sample seeded, in tables this store cannot migrate over their rows. The
// throwaway container never does; a database somebody configured is left alone unless --allow-data-generation names it as the demo's.
SampleDatabaseInspection inspected = await SampleDatabase.InspectAsync(postgres);
if (inspected.NeedsReseed)
{
    if (!EphemeralPostgres.IsThrowaway(postgres) && !allowDataGeneration)
    {
        await Console.Error.WriteLineAsync(
            "KStudio sample: the database in ConnectionStrings:Postgres was seeded by another build of the demo, or has rows in the demo's schemas "
            + "that the demo did not seed. Start once with --allow-data-generation to have it drop and seed "
            + string.Join(" and ", SampleDatabase.Schemas) + " again, or drop them yourself.");
        return 1;
    }

    await SampleDatabase.ReseedAsync(postgres);
}

// The judge, and the seed: the marker topic rule decides whether the cluster is the sample's own, and a configured cluster is seeded only
// when the host says outright that creating topics there is allowed.
if (await kafka.PrepareAsync(allowDataGeneration, Console.Out) is { } refusal)
{
    await Console.Error.WriteLineAsync(refusal);
    return 1;
}

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "kstudio-sample";
    });

builder.Services.AddAuthorization(SamplePolicies.Configure);
SampleKafkaPolicies.AddHandlers(builder.Services);

// The Marten half of the studio runs over the demo store: KStudio is a module of the studio it joins, and this sample hosts Marten Studio.
builder.Services.AddSingleton(new SampleSeeding(GrantConsoleRole: false));
builder.Services
    .AddMarten(options => SampleStore.Configure(options, postgres))
    .UseLightweightSessions()
    .AddAsyncDaemon(DaemonMode.Solo)
    .InitializeWith<SampleDataSeeder>();

builder.Services.AddMartenStudio(options =>
{
    options.AuthorizationPolicy = SamplePolicies.Studio;
    options.WriteAuthorizationPolicy = SamplePolicies.StudioWrite;
    options.Capabilities = MartenStudioCapabilities.All();

    foreach (string tenantId in SampleStore.TenantIds)
    {
        options.KnownTenantIds.Add(tenantId);
    }
});

// The header's "Back to application" link, to the sample's own landing page.
builder.Services.Configure<MartenStudioOptions>(static options => options.ApplicationPath = "/");

builder.Services.AddKafkaStudio(o => SampleKafkaRegistration.Configure(o, kafka.BootstrapServers));

WebApplication app = builder.Build();

// The loud banner (D18): at Warning, on start, naming whichever of the two is a container.
if (SampleKafka.Banner(
        kafka.IsThrowaway ? kafka.BootstrapServers : null,
        postgresHostAndPort,
        SampleKafka.IsReuseRequested(Environment.GetEnvironmentVariable(SampleKafka.ReuseEnvironmentVariable))) is { } banner)
{
    app.Logger.LogWarning("{Banner}", banner);
}

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

app.MapMartenStudio().RequireAuthorization(SamplePolicies.Studio);
app.MapLogin();

app.MapGet("/", (HttpContext context, IAntiforgery antiforgery) =>
{
    string who = context.User.Identity?.IsAuthenticated == true
        ? $"Signed in as <strong>{WebUtility.HtmlEncode(context.User.Identity.Name)}</strong>."
        : "Not signed in.";

    AntiforgeryTokenSet tokens = antiforgery.GetAndStoreTokens(context);
    context.Response.ContentType = "text/html; charset=utf-8";

    return context.Response.WriteAsync($$"""
        <!DOCTYPE html>
        <html lang="en">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <title>KStudio sample</title>
          <link rel="icon" href="data:," />
          <style>
            body { font-family: system-ui, sans-serif; margin: 3rem auto; max-width: 48rem; line-height: 1.5; padding: 0 1rem; }
            code { background: #f1f5f9; padding: .1rem .3rem; border-radius: 3px; }
          </style>
        </head>
        <body>
          <h1>KStudio sample</h1>
          <p>{{who}}</p>
          <ul>
            <li><a href="/marten/kafka">Open KStudio</a> &mdash; the Kafka pages of Marten Studio, mounted at <code>/marten</code></li>
            <li><a href="/marten">Open Marten Studio</a></li>
            <li><a href="/login">Sign in</a> as <code>admin</code> (every topic) or <code>support</code> (<code>orders.*</code> only); the password is the user name</li>
          </ul>
          <p>Try, as <code>support</code>, in Search over the last 6 hours of <code>orders.v1</code>: <code>header.correlation-id = 'c-0042'</code></p>
          <form method="post" action="/logout">
            <input type="hidden" name="{{LoginEndpoints.AntiforgeryFieldName}}" value="{{WebUtility.HtmlEncode(tokens.RequestToken)}}" />
            <button type="submit">Sign out</button>
          </form>
        </body>
        </html>
        """);
}).AllowAnonymous();

await app.RunAsync();

return 0;
