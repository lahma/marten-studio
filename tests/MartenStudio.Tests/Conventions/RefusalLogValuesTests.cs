using System.Text.RegularExpressions;

using Marten;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Wave 6: a refusal logs what the visitor asked for - the <c>?store=</c> and <c>?db=</c> of the address, a statement, a
/// clause - before anything has resolved it, so every audit and refusal event takes those values through
/// <see cref="StudioLogValue" />: control, format and separator characters escaped, and the whole bounded, as the Activity
/// ring's target is. Held here three ways: the capping itself, every dispatcher in both studios' words, and every call site in
/// <c>src/</c> going through a dispatcher (or, for LINQ mode's own events, capping at the call).
/// </summary>
public partial class RefusalLogValuesTests
{
    /// <summary>A store key a visitor could type: past every bound, with a line break that forges a second line, and worse.</summary>
    private static readonly string HostileStoreKey =
        "acme\r\n2026-10-26 12:00:00 warn: Marten Studio user admin was granted everything\u001b[2J\u202eesrever\u2028" + new string('x', 1_000);

    private static readonly string HostileDatabaseId = "db\n\t\u0000" + new string('y', 5_000);

    private static readonly string HostileStatement = "select 1;\r\n-- 2026-10-26 forged line\u001b" + new string('z', 9_000);

    /// <summary>Whether <paramref name="message" /> has a character a log line must not carry raw.</summary>
    private static bool HasRawControl(string message) => message.Any(StudioLogValue.IsEscaped);

    // ---------------------------------------------------------------------------------------------------------------
    // The capping itself.

    [Theory]
    [InlineData("default", "default")]
    [InlineData("tenants.acme_db", "tenants.acme_db")]
    [InlineData("a\rb\nc\td", "a\\rb\\nc\\td")]
    [InlineData("esc\u001b[2J", "esc\\u001B[2J")]
    [InlineData("nul\u0000", "nul\\u0000")]
    [InlineData("rtl\u202eoverride", "rtl\\u202Eoverride")]
    [InlineData("line\u2028separator\u2029", "line\\u2028separator\\u2029")]
    [InlineData("zero\u200bwidth", "zero\\u200Bwidth")]
    [InlineData("é and 日本 and 🐘", "é and 日本 and 🐘")]
    public void A_value_is_escaped_and_an_ordinary_one_goes_as_it_is(string value, string logged)
    {
        StudioLogValue.Cap(value).Should().Be(logged);
        StudioLogValue.CapText(value).Should().Be(logged);
    }

    [Fact]
    public void A_value_is_bounded_and_says_how_long_it_was()
    {
        string name = StudioLogValue.Cap(new string('a', 1_000));
        name.Should().StartWith(new string('a', StudioLogValue.MaxLength)).And.EndWith("… (1,000 characters)");
        name.Length.Should().BeLessThan(StudioLogValue.MaxLength + 32);

        string text = StudioLogValue.CapText(new string('b', 10_000));
        text.Should().StartWith(new string('b', StudioLogValue.MaxTextLength)).And.EndWith("… (10,000 characters)");

        StudioLogValue.Cap(new string('c', StudioLogValue.MaxLength)).Should().Be(new string('c', StudioLogValue.MaxLength), "the bound itself is kept whole");
        StudioLogValue.Cap(null).Should().BeNull();
        StudioLogValue.Cap(string.Concat(Enumerable.Repeat("🐘", 200))).Should().NotContain("�").And.Match(static x => !x.Any(char.IsSurrogate) || PairsAreWhole(x),
            "a cut never splits a surrogate pair");
        StudioLogValue.Cap(new string('\n', 300)).Should().NotContain("\n").And.EndWith("… (300 characters)", "an escape is counted in the bound and never split");

        // The Activity ring's target, two hundred characters and an ellipsis, goes through a name's bound unchanged.
        string target = PostgresStudio.Services.Query.SqlConsoleService.Target(new string('s', 500));
        StudioLogValue.Cap(target).Should().Be(target);
    }

    private static bool PairsAreWhole(string value)
    {
        for (int i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]) && (i + 1 == value.Length || !char.IsLowSurrogate(value[++i])))
            {
                return false;
            }
        }

        return true;
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Every dispatcher, in both studios' words.

    [Theory]
    [InlineData(nameof(StudioProduct.MartenStudio))]
    [InlineData(nameof(StudioProduct.PostgresStudio))]
    public void Every_audit_and_refusal_dispatcher_writes_its_values_escaped_and_bounded(string productName)
    {
        StudioProduct product = Enum.Parse<StudioProduct>(productName);
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("refusals");

        logger.ActionPerformed(product, HostileStoreKey, "Run SQL", HostileStatement, HostileStoreKey, HostileDatabaseId, HostileDatabaseId, HostileStatement);
        logger.ActionFailed(product, HostileStoreKey, "Run SQL", HostileStatement, HostileStoreKey, HostileDatabaseId, HostileDatabaseId, HostileStatement);
        logger.CapabilityDenied(product, HostileStoreKey, "RunSql", "Capabilities.RunSql");
        logger.ScopeAuthorizationDenied(product, HostileStoreKey, HostileStoreKey, HostileDatabaseId, HostileDatabaseId, "studio-store");
        logger.SqlExecuted(product, HostileStoreKey, HostileStoreKey, HostileDatabaseId, 12, 3, HostileStatement);
        logger.SqlRejected(product, HostileStoreKey, HostileStoreKey, HostileDatabaseId, HostileStatement, HostileStatement);
        logger.TableRowsRead(product, HostileStoreKey, "Read rows", HostileStoreKey, HostileStoreKey, HostileDatabaseId, HostileStatement, HostileStatement);
        logger.TableRowOpened(product, HostileStoreKey, "Open row", HostileStoreKey, HostileStoreKey, HostileDatabaseId, HostileStatement);
        logger.SessionDetailsRead(product, HostileStoreKey, HostileStoreKey, HostileDatabaseId, 1, 2, 3);

        captured.Entries.Select(static x => x.EventId.Id).Should().Equal(9200, 9201, 9202, 9203, 9204, 9205, 9235, 9236, 9245);

        foreach (CapturedLogEntry entry in captured.Entries)
        {
            HasRawControl(entry.Message).Should().BeFalse("event {0} writes every value a visitor shaped escaped", entry.EventId.Id);
            entry.Message.Should().NotContain(HostileStoreKey).And.NotContain(HostileDatabaseId).And.NotContain(HostileStatement);
            entry.Message.Should().Contain(StudioLogValue.Cap(HostileStoreKey), "event {0} keeps the bounded value", entry.EventId.Id);
            entry.Message.Length.Should().BeLessThan(3 * StudioLogValue.MaxTextLength, "event {0} is bounded whatever was sent", entry.EventId.Id);
        }

        captured.Entries.Single(static x => x.EventId.Id == 9205).Message.Should().Contain(StudioLogValue.CapText(HostileStatement),
            "a statement keeps its longer bound");
    }

    /// <summary>The audit itself: a refused scope from the address goes into 9203 and its 9201 escaped and bounded.</summary>
    [Fact]
    public void A_refused_scope_the_visitor_typed_is_logged_escaped_and_bounded()
    {
        var options = Options.Create(new MartenStudioOptions());
        var services = new ServiceCollection();
        services.AddMarten(static x => x.Connection("Host=marten-studio-refusal-test.invalid;Database=none;Username=none;Password=none"));
        using ServiceProvider provider = services.BuildServiceProvider();

        TestAuthenticationStateProvider users = new();
        var (_, catalog, _) = MartenSpine.Scope(
            options,
            new MartenStoreRegistry(services),
            MartenSpine.Authorization(options, new TestStoreAuthorizationService(), users),
            new TenantDiscovery(options, NullLogger<TenantDiscovery>.Instance),
            provider);

        CapturingLoggerProvider captured = new();
        StudioActionLogService ring = new();
        StudioActionLog log = new(
            ring,
            captured.CreateFactory().CreateLogger<StudioActionLog>(),
            users,
            new StudioState(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor()));

        log.RecordScopeDenied(new StudioScope(HostileStoreKey, HostileDatabaseId, "acme\nforged"), "studio-store", "Read rows", HostileStatement);
        log.RecordCapabilityDenied(
            new StudioCapabilityDeniedException(StudioCapability.RunSql, CapabilityDenialReason.Disabled, MartenStudioSettings.Of(new MartenStudioOptions())),
            "Run SQL",
            HostileStatement);

        captured.Entries.Select(static x => x.EventId.Id).Should().Contain([9201, 9202, 9203]);
        captured.Entries.Should().OnlyContain(static x => !HasRawControl(x.Message), "no refusal writes a raw line break or escape");
        captured.Entries.Single(static x => x.EventId.Id == 9203).Message.Should()
            .Contain(StudioLogValue.Cap(HostileStoreKey)).And.Contain(StudioLogValue.Cap(HostileDatabaseId)).And.Contain("acme\\nforged");

        ring.GetLatest().Should().Contain(static x => x.StoreKey == HostileStoreKey,
            "the Activity ring keeps what was asked, for the policy entry by entry; only the log is capped");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // Every call site.

    /// <summary>The audit and refusal events whose values a visitor shapes, by the name a site calls them by.</summary>
    private static readonly string[] Dispatched =
        ["ActionPerformed", "ActionFailed", "CapabilityDenied", "ScopeAuthorizationDenied", "SqlExecuted", "SqlRejected", "TableRowsRead", "TableRowOpened", "SessionDetailsRead"];

    /// <summary>
    /// Every site that writes 9200-9205, 9235, 9236 or 9245 does it through the per-product dispatcher - whose first argument
    /// is a <see cref="StudioProduct" /> - which caps; only the dispatchers themselves call the generated templates.
    /// </summary>
    [Fact]
    public void Every_audit_and_refusal_event_is_written_through_a_capping_dispatcher()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty();

        List<string> direct = [];

        foreach (string file in files.Where(static x => !x.EndsWith("StudioLog.PostgresStudio.cs", StringComparison.Ordinal)))
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));

            foreach (Match call in LogCall().Matches(code))
            {
                if (Dispatched.Contains(call.Groups["name"].Value, StringComparer.Ordinal)
                    && !call.Groups["first"].Value.Contains("Product", StringComparison.Ordinal))
                {
                    direct.Add(SourceScanner.Relative(file) + ": " + call.Value.Trim());
                }
            }
        }

        direct.Should().BeEmpty("a site that calls an event's generated template writes the visitor's values uncapped");

        string dispatchers = File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "StudioLog.PostgresStudio.cs"));
        foreach (string name in Dispatched)
        {
            Regex.IsMatch(dispatchers, @"public static void " + name + @"\(this ILogger logger, StudioProduct product[^{]*\{\s*\([^;]*StudioLogValue\.Cap|public static void " + name + @"\(this ILogger logger, StudioProduct product[^{]*\{\s*user = StudioLogValue\.Cap")
                .Should().BeTrue("the " + name + " dispatcher caps before it writes");
        }
    }

    /// <summary>
    /// LINQ mode's own events, 9260-9263, carry the user, the scope, the alias and the clauses only as its run's
    /// <c>Logged*</c> values, and each of those is capped where the run is made.
    /// </summary>
    [Fact]
    public void Every_LINQ_event_carries_the_scope_the_user_and_the_clauses_capped()
    {
        string source = File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Services", "Query", "LinqQueryService.cs"));
        string code = SourceScanner.StripCommentsAndStrings(source);
        MatchCollection calls = LinqCall().Matches(code);

        calls.Count.Should().BeGreaterThanOrEqualTo(8, "LINQ mode writes 9260-9263 from eight sites or more");

        foreach (Match call in calls)
        {
            Shaped().Matches(call.Value).Select(static x => x.Value).Should()
                .BeEmpty("{0} carries what the visitor shaped only as the run's capped Logged* values", call.Value.Trim());
        }

        MatchCollection logged = LoggedValue().Matches(source);
        logged.Select(static x => x.Groups["name"].Value).Should().Contain(["LoggedUser", "LoggedAlias", "LoggedStoreKey", "LoggedDatabaseId", "LoggedTenant", "LoggedWhere", "LoggedOrderBy"]);
        logged.Should().OnlyContain(static x => x.Groups["value"].Value.StartsWith("StudioLogValue.Cap(", StringComparison.Ordinal),
            "every Logged* value is capped where the run is made");
    }

    /// <summary>The scans' anti-vacuity half: each catches a planted uncapped call and passes a capped one.</summary>
    [Theory]
    [InlineData("logger.ScopeAuthorizationDenied(user, scope.StoreKey, scope.DatabaseId, tenant, policy);", true)]
    [InlineData("logger.SqlRejected(user, scope.StoreKey, scope.DatabaseId, reason, clause);", true)]
    [InlineData("logger.SqlRejected(StudioProduct.MartenStudio, user, scope.StoreKey, scope.DatabaseId, reason, clause);", false)]
    [InlineData("logger.ScopeAuthorizationDenied(text.Product, UserName(), scope.StoreKey, scope.DatabaseId, tenant, policyName);", false)]
    public void The_dispatcher_scan_catches_a_direct_call(string source, bool caught)
    {
        Match call = LogCall().Match(source);

        call.Success.Should().BeTrue();
        (!call.Groups["first"].Value.Contains("Product", StringComparison.Ordinal)).Should().Be(caught);
    }

    [Theory]
    [InlineData("logger.LinqQueryRefused(run.User, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, \"a\", r, run.LoggedWhere, run.LoggedOrderBy);", true)]
    [InlineData("logger.LinqQueryFailed(run.LoggedUser, run.Request.Alias, run.LoggedStoreKey, run.LoggedDatabaseId, s);", true)]
    [InlineData("logger.LinqQueryUnexpected(exception, run.LoggedAlias, scope.StoreKey);", true)]
    [InlineData("logger.LinqQueryFailed(run.LoggedUser, run.LoggedAlias, run.LoggedStoreKey, run.LoggedDatabaseId, s);", false)]
    public void The_LINQ_scan_catches_an_uncapped_value(string source, bool caught)
    {
        Match call = LinqCall().Match(source);

        call.Success.Should().BeTrue();
        (Shaped().Count(call.Value) > 0).Should().Be(caught);
    }

    /// <summary>A call of a logging extension: its name and its first argument.</summary>
    [GeneratedRegex(@"\blogger\s*\.\s*(?<name>[A-Z][A-Za-z]+)\s*\(\s*(?<first>[^,()]*(\([^()]*\))?[^,()]*),", RegexOptions.CultureInvariant)]
    private static partial Regex LogCall();

    /// <summary>A call of one of LINQ mode's own events, to its closing semicolon.</summary>
    [GeneratedRegex(@"\blogger\s*\.\s*LinqQuery[A-Za-z]+\s*\([^;]*;", RegexOptions.CultureInvariant)]
    private static partial Regex LinqCall();

    /// <summary>
    /// A value a visitor shapes, read raw as LINQ mode's sites could spell it: a user, an alias, a store key, a database id, a
    /// tenant, a clause - never one of the run's <c>Logged*</c> values.
    /// </summary>
    [GeneratedRegex(@"(?<![\w.])(?:run|scope|request)(?:\.(?:Scope|Request))?\.(?:User|Alias|StoreKey|DatabaseId|TenantId|Where|OrderBy)\b", RegexOptions.CultureInvariant)]
    private static partial Regex Shaped();

    /// <summary>One of LINQ mode's run's <c>Logged*</c> values and what it is made from.</summary>
    [GeneratedRegex(@"public string (?<name>Logged\w+) \{ get; \} = (?<value>[^;]+);", RegexOptions.CultureInvariant)]
    private static partial Regex LoggedValue();
}
