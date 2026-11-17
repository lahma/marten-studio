using System.Text.RegularExpressions;

using DataStudio.Components;
using DataStudio.Marten.Services.Documents;
using DataStudio.Marten.Services.Events;
using DataStudio.Marten.Services.Health;
using DataStudio.Postgres.Services;
using DataStudio.Postgres.Services.Database;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Settings;
using DataStudio.Marten.Tests.Conventions;
using DataStudio.Marten.Tests.Components;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// REVIEW-V2-A finding 5: nothing in production builds a default shell of its own, so a host's own <c>DataStudioOptions.Path</c> is the one
/// source of truth.
/// </summary>
/// <remarks>
/// The links themselves are relative to the studio-rooted <c>&lt;base href&gt;</c> and take no settings (v2 step 5, REVIEW-V2-B finding 2): what holds them
/// is the route tests (every rail route and route constant is a page) and the exact-address tests of the link classes, not a theory that could not fail.
/// </remarks>
public class RegisteredSettingsLinkTests
{
    private static readonly StudioScope Scope = new("default", "localhost.marten", "acme");

    /// <summary>
    /// The exact address of one link of each module, at a mount that is not <c>/studio</c>: the whole address below the mount, a module's segment first,
    /// the scope on the end, and nothing of the mount in it - the document's <c>&lt;base href&gt;</c> carries that.
    /// </summary>
    [Fact]
    public async Task A_link_is_the_exact_address_below_the_mount_with_its_modules_segment_at_a_custom_mount()
    {
        await using StudioComponentContext context = new();
        context.ShellOptions.Path = "/ops/data";
        context.Services.GetRequiredService<IStudioSettings>().TrimmedPath.Should().Be("/ops/data");

        HealthLinks.ToPage(Scope).Should().Be("postgres/health?store=default&db=localhost.marten&tenant=acme");
        DatabaseLinks.ToObject(Scope, "public", "orders").Should().Be("postgres/database/object?schema=public&name=orders&store=default&db=localhost.marten&tenant=acme");
        EventLinks.To(EventLinks.StreamsRoute, Scope).Should().Be("marten/events/streams?store=default&db=localhost.marten&tenant=acme");
        DocumentLinks.ToDocument(Scope, "orders", "7").Should().StartWith("marten/documents/orders/doc?");
        StudioLink.To("kafka/search").Should().Be("kafka/search");
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
        Forbidden.IsMatch("var s = MartenSettings.Of(options);").Should().BeTrue();
        Forbidden.IsMatch("var s = new PostgresSettings(Options.Create(x));").Should().BeTrue();
        Forbidden.IsMatch("var s = new ShellSettings(Options.Create(new DataStudioOptions()));").Should().BeTrue();
        Forbidden.IsMatch("static p => new MartenSettings(a, b)").Should().BeFalse("the container's own two-argument registration is the one construction");
    }

    private static readonly Regex Forbidden = new(
        @"\b(?:Marten|Postgres)Settings\.Of\(|new\s+(?:Marten|Postgres)Settings\(\s*[^,()]*(?:\([^()]*\))?[^,()]*\)|new\s+ShellSettings\(|new\s+DataStudioOptions\(",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(5));
}
