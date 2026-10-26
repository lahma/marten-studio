using System.Collections;
using System.Reflection;

using PostgresStudio;

namespace MartenStudio.Tests;

/// <summary>
/// Design §5.2: every option PostgresStudio has, MartenStudio has too, with the same type and the same default - so a
/// host that knows one studio knows the other - except the listed renames, the one type difference, the two values that
/// name the studio, and the options only one studio has. The one project that references both packages is where this
/// can be checked.
/// </summary>
public class OptionsParityTests
{
    /// <summary>PostgresStudio's name on the left, MartenStudio's on the right.</summary>
    private static readonly Dictionary<string, string> Renames = new(StringComparer.Ordinal)
    {
        ["SourceAuthorizationPolicy"] = nameof(MartenStudioOptions.StoreAuthorizationPolicy),
        ["MaxInlineValueBytes"] = nameof(MartenStudioOptions.MaxInlineDocumentBytes),
    };

    /// <summary>The same name and a studio's own type: each studio's capabilities class.</summary>
    private static readonly string[] OwnTypes = ["Capabilities"];

    /// <summary>Defaults that name the studio, and the value each studio has.</summary>
    private static readonly Dictionary<string, (object Postgres, object Marten)> StudioValues = new(StringComparer.Ordinal)
    {
        ["Path"] = ("/postgres", "/marten"),
        ["Title"] = ("Postgres Studio", "Marten Studio"),
    };

    private static readonly string[] PostgresOnly = ["IncludeDefaultDataSource"];

    private static readonly string[] MartenOnly =
    [
        nameof(MartenStudioOptions.DiscoverTenantIds),
        nameof(MartenStudioOptions.IncludeAncillaryStores),
        nameof(MartenStudioOptions.IsDocumentTypeVisible),
        nameof(MartenStudioOptions.KnownTenantIds),
        nameof(MartenStudioOptions.RebuildShardTimeout),
    ];

    private static PropertyInfo[] Public(Type type) =>
        [.. type.GetProperties(BindingFlags.Public | BindingFlags.Instance).OrderBy(static x => x.Name, StringComparer.Ordinal)];

    [Fact]
    public void Every_PostgresStudio_option_has_a_MartenStudio_twin_with_the_same_type_and_default()
    {
        PostgresStudioOptions postgres = new();
        MartenStudioOptions marten = new();
        Dictionary<string, PropertyInfo> twins = Public(typeof(MartenStudioOptions)).ToDictionary(static x => x.Name, StringComparer.Ordinal);
        List<string> problems = [];
        int compared = 0;

        foreach (PropertyInfo option in Public(typeof(PostgresStudioOptions)))
        {
            if (PostgresOnly.Contains(option.Name))
            {
                continue;
            }

            string twinName = Renames.GetValueOrDefault(option.Name, option.Name);
            if (!twins.TryGetValue(twinName, out PropertyInfo? twin))
            {
                problems.Add(option.Name + ": no MartenStudioOptions." + twinName);
                continue;
            }

            compared++;

            if (OwnTypes.Contains(option.Name))
            {
                continue;
            }

            if (option.PropertyType != twin.PropertyType)
            {
                problems.Add(option.Name + ": " + option.PropertyType.Name + " here, " + twin.PropertyType.Name + " in MartenStudio");
                continue;
            }

            object? mine = option.GetValue(postgres);
            object? theirs = twin.GetValue(marten);

            if (StudioValues.TryGetValue(option.Name, out (object Postgres, object Marten) expected))
            {
                if (!Equals(mine, expected.Postgres) || !Equals(theirs, expected.Marten))
                {
                    problems.Add(option.Name + ": '" + mine + "' and '" + theirs + "', not the studios' own values");
                }
            }
            else if (!SameDefault(mine, theirs))
            {
                problems.Add(option.Name + ": defaults '" + mine + "' and '" + theirs + "' differ");
            }
        }

        compared.Should().BeGreaterThan(15, "the comparison read the options");
        problems.Should().BeEmpty(string.Join(Environment.NewLine, problems));
    }

    [Fact]
    public void The_options_only_MartenStudio_has_are_the_listed_ones()
    {
        HashSet<string> shared =
        [
            .. Public(typeof(PostgresStudioOptions)).Select(static x => Renames.GetValueOrDefault(x.Name, x.Name)),
        ];

        Public(typeof(MartenStudioOptions)).Select(static x => x.Name).Where(x => !shared.Contains(x))
            .Should().Equal(MartenOnly, "a shared option added to one studio needs its twin in the other, or a line here saying why not");
    }

    [Fact]
    public void Both_capabilities_classes_name_the_three_shared_reads_alike()
    {
        string[] postgres = [.. Public(typeof(PostgresStudioCapabilities)).Select(static x => x.Name)];

        postgres.Should().Equal("BrowseDatabase", "RunSql", "ViewDatabaseActivity");
        Public(typeof(MartenStudioCapabilities)).Select(static x => x.Name).Should().Contain(postgres);
    }

    private static bool SameDefault(object? left, object? right) =>
        left is IEnumerable a && right is IEnumerable b && left is not string
            ? a.Cast<object?>().SequenceEqual(b.Cast<object?>())
            : Equals(left, right);
}
