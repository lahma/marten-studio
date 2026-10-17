using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// F8-D3: every call in <c>src/</c> that asks a Marten tenancy for its databases, or for one database or
/// tenant by name, is one of today's sites.
/// </summary>
/// <remarks>
/// <para>
/// None of these is a read on a dynamic tenancy, at any Marten through 9.50.2 (AGENTS.md D27).
/// <c>AllDatabases()</c>, <c>DescribeDatabasesAsync()</c> and 9.50.1's <c>TryDescribeDatabasesAsync()</c> all
/// run <c>Tenancy.BuildDatabases()</c>: a read of a master-table or sharded tenancy's lookup table every
/// time, and the first time in the process that table's apply under the store's
/// <c>AutoCreateSchemaObjects</c> and its seed rows' upsert - or, on a single-server tenancy, a
/// <c>CREATE DATABASE</c> for every configured database that does not exist yet. <c>FindDatabase(id)</c>
/// rebuilds on a miss. <c>FindOrCreateDatabase</c>, <c>GetTenant</c> and <c>GetTenantAsync</c> provision a
/// tenant a sharded or single-server tenancy does not have. <c>TryFindDatabase</c> and 9.50's
/// <c>FindDatabaseForTenantAsync</c> never provision, but on a master-table or sharded miss they build a data
/// source nothing caches.
/// </para>
/// <para>
/// So a new call is a decision, not a convenience: it goes on <see cref="AllowedSites" /> with what it needs,
/// or it does not go in. The list is by file, member and <em>count</em>, so a second call in a file that
/// already has one fails as surely as a call in a new file, and a call that goes away fails until its entry
/// is pruned - the list is a statement about the code that exists, never a standing exemption.
/// </para>
/// <para>
/// A call is a member access followed by an argument list - <c>.AllDatabases(</c> - read after
/// <see cref="SourceScanner" /> has blanked comments and strings, because every one of these is explained at
/// length in the doc comments of the files that make it. The dot is what keeps a studio method that happens
/// to share a name out of it (<c>ConfigurationService</c> has a private <c>DescribeDatabasesAsync</c> of its
/// own, called without a receiver); every call into Marten has a receiver.
/// <see cref="The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_a_string_or_a_studio_helper" /> is the
/// anti-vacuity half, and
/// <see cref="NoSchemaBuildingCallTests.Every_scanned_file_is_one_the_scanner_can_follow_to_the_end" /> already
/// fails on a file the stripping pass cannot follow to its end, which would hide a call here too.
/// </para>
/// </remarks>
public class TenancyEnumerationCallTests
{
    /// <summary>
    /// A call to one of the members, as a member access. <c>TryDescribeDatabasesAsync</c> and
    /// <c>DescribeDatabasesAsync</c> are told apart by the dot, which a <c>Try</c> prefix would sit between.
    /// </summary>
    private static readonly Regex TenancyCall = new(
        @"\.\s*(?<member>AllDatabases|BuildDatabases|TryDescribeDatabasesAsync|DescribeDatabasesAsync"
        + @"|FindOrCreateDatabase|GetTenantAsync|GetTenant|TryFindDatabase|FindDatabaseForTenantAsync|FindDatabase)\s*\(",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(5));

    /// <summary>
    /// Today's sites: the file, the member, how many calls, and what each one needs from the answer.
    /// </summary>
    /// <remarks>
    /// "Names" is what a list or a policy question is keyed by - <c>Id.Identity</c>, <c>Id.Name</c>,
    /// <c>Id.Server</c>; "the instances" is a site that hands a <c>IMartenDatabase</c> on to something that
    /// reads it; "tenant ids" is a site that reads <c>TenantIds</c> or a descriptor's.
    /// </remarks>
    private static readonly AllowedSite[] AllowedSites =
    [
        new("src/MartenStudio/Services/StudioScopeResolver.cs", "AllDatabases", 1,
            "the instance: every resolution matches the URL's database by identity against what the store lists, "
            + "never FindOrCreateDatabase, which would create the database a URL named"),
        new("src/MartenStudio/Services/StudioScopeCatalog.cs", "AllDatabases", 2,
            "names, for the database picker; and the instance tenant discovery reads, for the header"),
        new("src/MartenStudio/Services/StoreInfoService.cs", "AllDatabases", 1,
            "names, for the Overview's cards - one enumeration per store per load (F8-D3 C3)"),
        new("src/MartenStudio/Services/TenantDiscovery.cs", "DescribeDatabasesAsync", 1,
            "tenant ids, for every tenancy but master-table and sharded, which are read from the instance (F8-D3 C4)"),
        new("src/MartenStudio/Services/Database/DatabaseAccess.cs", "AllDatabases", 1,
            "names: another store's spelling of this database's identity, walked only when a policy will be asked "
            + "and only on a Single or StaticMultiple store (F8-D3 C1)"),
        new("src/MartenStudio/Services/Configuration/ConfigurationService.cs", "DescribeDatabasesAsync", 1,
            "the descriptors, which are what the Configuration screen's database list prints"),
        new("src/MartenStudio/Services/Configuration/ConfigurationService.cs", "AllDatabases", 1,
            "names, so that each descriptor can be put to the store policy as a URL would name its database"),
        new("src/MartenStudio/Services/DatabaseReach.cs", "AllDatabases", 2,
            "tenant ids: exclusivity reads them reconciled, and a tenant is proven present before it is asked for"),
        new("src/MartenStudio/Services/DatabaseReach.cs", "DescribeDatabasesAsync", 1,
            "tenant ids a single-server tenancy carries only on its descriptors"),
        new("src/MartenStudio/Services/DatabaseReach.cs", "GetTenantAsync", 1,
            "the tenant's database, asked only of a tenancy that never provisions, or for a tenant proven present"),
        new("src/MartenStudio/Services/DatabaseReachAuthorization.cs", "AllDatabases", 1,
            "names: every database an operation asked for without a tenant reaches, each put to the policy"),
        new("src/MartenStudio/Services/Projections/DaemonAccessor.cs", "AllDatabases", 1,
            "names: the databases a daemon hosted in this process covers - reached only once a projection "
            + "coordinator is registered here, so that daemon has enumerated before the studio does"),
        new("src/MartenStudio/Services/Health/Checks/ConnectionBudgetCheck.cs", "AllDatabases", 2,
            "the instances' data sources, for C02's pool budget: Storage.AllDatabases() only on a Single or "
            + "StaticMultiple store, and on a single-server tenancy Weasel's own SingleServerDatabaseCollection."
            + "AllDatabases(), the databases already built, from memory - never Tenancy.BuildDatabases()"),
    ];

    [Fact]
    public void Every_tenancy_enumeration_in_src_is_one_of_todays_sites()
    {
        Dictionary<(string File, string Member), int> found = FoundCalls();

        found.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        string[] offenders =
        [
            .. found
                .Where(x => x.Value > AllowedCount(x.Key))
                .Select(x => $"{x.Key.File}: {x.Key.Member} x{x.Value} (allowed {AllowedCount(x.Key)})")
                .Order(StringComparer.Ordinal),
        ];

        offenders.Should().BeEmpty(
            "AGENTS.md D27: on a master-table or sharded tenancy each of these is a read of the lookup table, and the "
            + "first one in the process applies it; on a sharded or single-server tenancy a lookup by name can "
            + "provision. Reuse an enumeration the request already made, or add the site to AllowedSites with what it "
            + "needs. Offenders: " + string.Join(", ", offenders));
    }

    /// <summary>
    /// The allow-list is a statement about code that exists, not a permanent exemption.
    /// </summary>
    [Fact]
    public void Every_allowed_site_is_still_there_so_the_list_cannot_go_stale()
    {
        Dictionary<(string File, string Member), int> found = FoundCalls();

        string[] stale =
        [
            .. AllowedSites
                .Where(x => found.GetValueOrDefault((x.File, x.Member)) != x.Count)
                .Select(x => $"{x.File}: {x.Member} allowed x{x.Count}, found x{found.GetValueOrDefault((x.File, x.Member))}")
                .Order(StringComparer.Ordinal),
        ];

        stale.Should().BeEmpty("an entry that excuses calls which are no longer there would excuse whatever is written there next - prune it: "
            + string.Join(", ", stale));

        AllowedSites.Should().OnlyContain(static x => !string.IsNullOrWhiteSpace(x.Needs), "every site says what it needs");
        AllowedSites.Select(static x => (x.File, x.Member)).Should().OnlyHaveUniqueItems();
    }

    /// <summary>The anti-vacuity check: the scanner has to be able to fail, and to tell a call from prose.</summary>
    [Theory]
    [InlineData("var all = await store.Storage.AllDatabases();", "AllDatabases")]
    [InlineData("await store.Options.Tenancy\n    .DescribeDatabasesAsync(token);", "DescribeDatabasesAsync")]
    [InlineData("var usage = await ((IEventStore) store).TryDescribeDatabasesAsync(token);", "TryDescribeDatabasesAsync")]
    [InlineData("var list = await tenancy.BuildDatabases();", "BuildDatabases")]
    [InlineData("var db = await store.Storage.FindOrCreateDatabase(id);", "FindOrCreateDatabase")]
    [InlineData("Tenant t = await tenancy.GetTenantAsync(tenantId);", "GetTenantAsync")]
    [InlineData("Tenant t = tenancy.GetTenant (tenantId);", "GetTenant")]
    [InlineData("var db = await tenancy.TryFindDatabase(id);", "TryFindDatabase")]
    [InlineData("var db = await tenancy.FindDatabase(databaseId);", "FindDatabase")]
    [InlineData("var db = await events.FindDatabaseForTenantAsync(id, token);", "FindDatabaseForTenantAsync")]
    [InlineData("var all = store?.Storage.AllDatabases();", "AllDatabases")]
    [InlineData("/// <c>Storage.AllDatabases()</c> is a query on a master-table tenancy.", null)]
    [InlineData("// tenancy.GetTenantAsync(id) provisions a sharded tenant", null)]
    [InlineData("/* store.Storage.FindOrCreateDatabase(id) */", null)]
    [InlineData("var why = \"never call Storage.AllDatabases() here\";", null)]
    [InlineData("@* the picker never calls Tenancy.BuildDatabases() *@", null)]
    [InlineData("await DescribeDatabasesAsync(resolved, cancellationToken);", null)]
    [InlineData("private async Task<int> DescribeDatabasesAsync(ResolvedScope resolved)", null)]
    [InlineData("IMartenDatabase? found = await FindDatabaseAsync(store, id);", null)]
    [InlineData("var ids = await tenancy.GetTenantIdsAsync(token);", null)]
    [InlineData("var count = databases.AllDatabasesCount();", null)]
    public void The_scanner_finds_a_real_call_and_ignores_one_in_a_comment_a_string_or_a_studio_helper(
        string source, string? member)
    {
        Match match = TenancyCall.Match(SourceScanner.StripCommentsAndStrings(source));

        if (member is null)
        {
            match.Success.Should().BeFalse("'{0}' is not a call into a tenancy", source);
        }
        else
        {
            match.Success.Should().BeTrue("'{0}' calls {1}", source, member);
            match.Groups["member"].Value.Should().Be(member);
        }
    }

    private static int AllowedCount((string File, string Member) site) =>
        AllowedSites.FirstOrDefault(x =>
            string.Equals(x.File, site.File, StringComparison.Ordinal)
            && string.Equals(x.Member, site.Member, StringComparison.Ordinal))?.Count ?? 0;

    /// <summary>Every call the shipped source makes, counted by file and member.</summary>
    private static Dictionary<(string File, string Member), int> FoundCalls()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");

        Dictionary<(string File, string Member), int> found = new();

        foreach (string path in files)
        {
            string relative = SourceScanner.Relative(path);
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(path));

            foreach (Match match in TenancyCall.Matches(code))
            {
                (string File, string Member) key = (relative, match.Groups["member"].Value);
                found[key] = found.GetValueOrDefault(key) + 1;
            }
        }

        return found;
    }

    /// <summary>One allowed site.</summary>
    /// <param name="File">The file, relative to the repository root, with forward slashes.</param>
    /// <param name="Member">The member called.</param>
    /// <param name="Count">How many calls of it the file makes.</param>
    /// <param name="Needs">What the site needs from the answer.</param>
    private sealed record AllowedSite(string File, string Member, int Count, string Needs);
}
