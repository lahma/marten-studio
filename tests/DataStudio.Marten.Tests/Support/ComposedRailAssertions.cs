using System.Reflection;

using DataStudio.Services.Settings;

using Microsoft.Extensions.DependencyInjection;

namespace DataStudio.Marten.Tests.Support;

/// <summary>
/// What v2 step 5 holds of every host, on the composed shell (linked into each suite that composes one): every route of every rail entry is a page of
/// the composed route table, every route constant a module's link classes declare is one too, and no 1.x address (<c>health</c>, <c>documents</c>,
/// <c>query</c>) is. The route table is the endpoint mapping's own input, so a rail entry that matches here is a link that opens a page there.
/// </summary>
internal static class ComposedRailAssertions
{
    /// <summary>The addresses 1.x served below the mount, and which 2.0 does not: they sit under a module's segment now.</summary>
    private static readonly string[] OneDotXAddresses =
    [
        "health", "query", "plan", "database", "database/object", "database/sessions", "relationships", "statements", "maintenance", "storage", "replication", "server",
        "documents", "documents/customer", "events/streams", "events/feed", "projections", "schema", "config", "stores", "tenants",
    ];

    /// <summary>Every rail entry's route of the composed shell matches a page, and so does the front page and Activity.</summary>
    /// <param name="provider">A provider holding the host's registrations.</param>
    public static StudioShellDescriptor EveryRailRouteMatchesAPage(IServiceProvider provider)
    {
        StudioShellDescriptor shell = provider.GetRequiredService<StudioShellDescriptor>();
        string[] routes = [.. shell.Navigation.SelectMany(static section => section.Items).Select(static item => item.Route).OfType<string>()];

        routes.Should().NotBeEmpty().And.Contain("activity");
        foreach (string route in routes)
        {
            shell.Routes.Match(route).Should().NotBeNull("the rail's '{0}' is a page of the composed route table", route);
        }

        shell.Routes.Match(string.Empty).Should().NotBeNull("the mount is the shell's front page");

        foreach (string module in shell.Modules.Select(static x => x.HomeRoute))
        {
            shell.Routes.Match(module).Should().NotBeNull("the home of a module is a page");
        }

        return shell;
    }

    /// <summary>A 1.x address, below the mount, is no page of this composition (unless the module that spells it that way is one of the host's).</summary>
    /// <param name="shell">The composed shell.</param>
    public static void NoOneDotXAddressIsAPage(StudioShellDescriptor shell)
    {
        foreach (string address in OneDotXAddresses)
        {
            shell.Routes.Match(address).Should().BeNull("'{0}' is a 1.x address: every page is under its module's segment", address);
        }
    }

    /// <summary>Every <c>const string</c> named <c>Route</c> or ending in <c>Route</c> in <paramref name="assemblies" />, with its owner.</summary>
    /// <param name="assemblies">The assemblies to walk.</param>
    public static IReadOnlyList<(string Owner, string Value)> RouteConstants(params Assembly[] assemblies) =>
    [
        .. assemblies
            .SelectMany(static x => x.GetTypes())
            .SelectMany(static type => type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(static f => f is { IsLiteral: true, IsInitOnly: false } && f.FieldType == typeof(string) && (f.Name == "Route" || f.Name.EndsWith("Route", StringComparison.Ordinal)))
                .Select(static f => (Owner: f.DeclaringType!.Name + "." + f.Name, Value: (string) f.GetRawConstantValue()!)))
            .OrderBy(static x => x.Owner, StringComparer.Ordinal),
    ];

    /// <summary>Every route constant of <paramref name="assemblies" /> is a page of <paramref name="shell" />'s route table.</summary>
    /// <param name="shell">The composed shell.</param>
    /// <param name="assemblies">The assemblies whose route constants are walked.</param>
    public static void EveryRouteConstantIsAPage(StudioShellDescriptor shell, params Assembly[] assemblies)
    {
        IReadOnlyList<(string Owner, string Value)> constants = RouteConstants(assemblies);

        constants.Should().NotBeEmpty();
        foreach ((string owner, string value) in constants)
        {
            shell.Routes.Match(value).Should().NotBeNull("{0} = '{1}' is an address below the mount that opens a page", owner, value);
        }
    }
}
