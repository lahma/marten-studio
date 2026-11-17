using Bunit;

using DataStudio.Components.Layout;
using DataStudio.Services.Settings;
using DataStudio.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Postgres.Tests.Components;

/// <summary>
/// Shell A2b (the review of A2a, F6): the chip keys each module's groups by the module's key and the group's index, never by its heading. The
/// composition proves module keys unique and says nothing of the words of their groups, so two modules whose groups share a heading must draw, not
/// throw a duplicate-key error out of the header.
/// </summary>
public class CapabilityBannerModuleGroupsTests
{
    private sealed class FakeModule(string key, string root) : IStudioModule
    {
        public string Key => key;

        public string ProductName => "Fake " + key;

        public IReadOnlyList<StudioPageAssembly> PageAssemblies => [new(typeof(FakeModule).Assembly, root)];

        public IReadOnlyList<StudioNavSection> Navigation => [];

        public Type? ScopeSelector => null;

        public Type? HeaderSearch => null;

        public Type? CircuitHost => null;

        public Type? ScopeGate => null;

        public string HomeRoute => key;

        public string Summary => "A fake.";

        public string ExposedSentence => "Fake.";

        public IReadOnlyList<StudioModuleCapabilityGroup> CapabilityGroups =>
        [
            new("Same heading", [new StudioModuleCapability(key + "One", "Fake." + key + ".One", Configured: true)]),
            new("Same heading", [new StudioModuleCapability(key + "Two", "Fake." + key + ".Two", Configured: false)]),
        ];

        public string? AllCapabilitiesOption => null;

        public IReadOnlyList<StudioShortcutGroup> Shortcuts => [];

        public string AuditEventIds => string.Empty;
    }

    [Fact]
    public async Task Two_modules_whose_groups_share_a_heading_are_drawn_each_group_once()
    {
        await using PostgresStudioComponentContext context = new(static services =>
        {
            services.AddSingleton<IStudioModule>(new FakeModule("a", "/a"));
            services.AddSingleton<IStudioModule>(new FakeModule("b", "/b"));
        });

        IRenderedComponent<CapabilityBanner> banner = context.Render<CapabilityBanner>();

        banner.FindAll(".ms-popover-title").Select(static x => x.TextContent.Trim()).Where(static x => x.EndsWith(": Same heading", StringComparison.Ordinal))
            .Should().HaveCount(4, "two groups of two modules, each under its module's name");
        banner.FindAll(".ms-capability-name").Select(static x => x.TextContent.Trim()).Should().Contain(["aOne", "aTwo", "bOne", "bTwo"]);
    }
}
