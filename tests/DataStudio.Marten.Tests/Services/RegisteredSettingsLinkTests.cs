using System.Text.RegularExpressions;

using DataStudio.Components;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Marten.Services.Health;
using DataStudio.Marten.Tests.Health;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Marten.Tests.Conventions;
using DataStudio.Marten.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// REVIEW-V2-A finding 5: the module's link helpers take the container's registered settings, and nothing in production builds
/// a default shell of its own, so a host's own <c>DataStudioOptions.Path</c> is the one source of truth.
/// </summary>
/// <remarks>
/// The links themselves are relative to the studio-rooted <c>&lt;base href&gt;</c> and read nothing of the path
/// (<c>StudioLink.To</c> says so), so "follows the path" is proved in the only form it can take: at a mount that is not
/// <c>/studio</c> every site still answers a link below the studio root, which names neither the default mount nor the host's own.
/// </remarks>
public class RegisteredSettingsLinkTests
{
    private const string Mount = "/ops/data";

    private static readonly StudioScope Scope = new("default", "localhost.marten", "acme");

    /// <summary>Each of the five sites that used to build a default shell, and the two link builders that fed them.</summary>
    public static TheoryData<string> Sites => new()
    {
        "StudioLink.To",
        "HealthLinks.ToPage",
        "HealthLinks.ToFinding",
        "DatabaseLinks.ToObject",
        "DatabaseLinks.ToRows",
        "DocumentLinks.ToDocument",
        "EventLinks.To",
    };

    [Theory]
    [MemberData(nameof(Sites))]
    public async Task A_link_built_through_the_registered_settings_is_below_the_studio_root_at_a_custom_mount(string site)
    {
        await using StudioComponentContext context = new();
        context.ShellOptions.Path = Mount;

        IStudioSettings settings = context.Services.GetRequiredService<IStudioSettings>();

        settings.HasCustomPath.Should().BeTrue("the settings are the registered ones, over the host's own DataStudioOptions");
        settings.TrimmedPath.Should().Be(Mount);

        string link = site switch
        {
            "StudioLink.To" => StudioLink.To("marten/documents/orders/doc"),
            "HealthLinks.ToPage" => HealthLinks.ToPage(Scope),
            "HealthLinks.ToFinding" => HealthLinks.ToFinding(Scope, Finding()),
            "DatabaseLinks.ToObject" => DatabaseLinks.ToObject(Scope, "public", "orders"),
            "DatabaseLinks.ToRows" => DatabaseLinks.ToRows(Scope, "public", "orders", "id = 1"),
            "DocumentLinks.ToDocument" => DocumentLinks.ToDocument(Scope, "orders", "7"),
            "EventLinks.To" => EventLinks.To(EventLinks.StreamsRoute, Scope),
            _ => throw new InvalidOperationException(site),
        };

        link.Should().NotBeNullOrEmpty().And.NotStartWith("/", "a studio link is relative to the studio-rooted base href")
            .And.NotContain("/studio").And.NotContain("ops/data");
    }

    [Fact]
    public async Task The_schema_data_service_names_the_registered_store_policy_in_its_refusal()
    {
        await using StudioComponentContext context = new();
        context.Options.StoreAuthorizationPolicy = "store-ops";

        DatabaseAccess.StorePolicyName(context.Services.GetRequiredService<IStudioSettings>()).Should().Be("store-ops");
    }

    /// <summary>
    /// The second half of the finding: the one-argument forms and a default shell are the tests' alone, so no shipped source can build
    /// a second source of truth again. The scan is capable of failing, see <see cref="The_scan_sees_a_forbidden_call"/>.
    /// </summary>
    [Fact]
    public void No_shipped_source_builds_a_default_shell_or_a_one_argument_settings()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        string[] offenders =
        [
            .. files
                .Where(static x => Forbidden.IsMatch(SourceScanner.StripCommentsAndStrings(File.ReadAllText(x))))
                .Select(SourceScanner.Relative)
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty("production takes the registered settings; only a test builds them by hand. Offenders: " + string.Join(", ", offenders));
    }

    [Fact]
    public void The_scan_sees_a_forbidden_call()
    {
        Forbidden.IsMatch("var s = MartenStudioSettings.Of(options);").Should().BeTrue();
        Forbidden.IsMatch("var s = new PostgresStudioSettings(Options.Create(x));").Should().BeTrue();
        Forbidden.IsMatch("var s = new ShellSettings(Options.Create(new DataStudioOptions()));").Should().BeTrue();
        Forbidden.IsMatch("static p => new MartenStudioSettings(a, b)").Should().BeFalse("the container's own two-argument registration is the one construction");
    }

    private static readonly Regex Forbidden = new(
        @"\b(?:Marten|Postgres)StudioSettings\.Of\(|new\s+(?:Marten|Postgres)StudioSettings\(\s*[^,()]*(?:\([^()]*\))?[^,()]*\)|new\s+ShellSettings\(|new\s+DataStudioOptions\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));

    private static HealthReportFinding Finding()
    {
        HealthFinding finding = FakeHealthService.Finding("K05", HealthSeverity.Warning, "A finding");
        return new HealthReportFinding(finding, finding.Evidence, "key", "https://example.invalid/docs", true, null);
    }
}
