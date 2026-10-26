using System.Text.RegularExpressions;

using MartenStudio.Services;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;
using PostgresStudio.Services;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// W6-fix: the statement 9204 says ran is kept whole up to the circuit's receive bound, and by both ends past it, so no
/// padding keeps it out of the record; the Marten audit events and every Warning that carries a value from the address are
/// escaped and bounded too.
/// </summary>
public partial class RefusalLogValuesTests
{
    // ---------------------------------------------------------------------------------------------------------------
    // 9204's statement.

    /// <summary>
    /// The review's case: 4,000 characters of comment in front of the statement once pushed the statement itself past a text's
    /// bound, out of the only record that it ran. Below the circuit's receive bound, the whole statement is there.
    /// </summary>
    [Theory]
    [InlineData(nameof(StudioProduct.MartenStudio))]
    [InlineData(nameof(StudioProduct.PostgresStudio))]
    public void A_padded_statement_that_ran_is_in_9204_to_its_end(string productName)
    {
        StudioProduct product = Enum.Parse<StudioProduct>(productName);
        const string Real = "select salary from payroll where name = 'ceo'";
        string padded = "/*" + new string(' ', 6_000) + "*/\n" + Real;

        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("statements");

        logger.SqlExecuted(product, "ops", "default", "default", 3, 1, padded);
        logger.SqlRejected(product, "ops", "default", "default", "refused", padded);

        captured.Entries.Single(static x => x.EventId.Id == 9204).Message.Should().Contain(Real, "the statement that ran is in the record, however it was padded");
        captured.Entries.Single(static x => x.EventId.Id == 9205).Message.Should().NotContain(Real,
            "the anti-vacuity half: a text's bound would have cut it, which is why 9204 does not use one");
    }

    [Fact]
    public void A_statement_is_whole_up_to_the_bound_and_keeps_both_ends_past_it()
    {
        string whole = "select 1 -- " + new string('w', StudioLogValue.MaxStatementLength - 20);
        StudioLogValue.CapStatement(whole).Should().Be(whole, "a statement no longer than the bound is kept as it is");
        StudioLogValue.CapStatement(null).Should().BeNull();
        StudioLogValue.CapStatement("select 1;\nselect 2").Should().Be("select 1;\\nselect 2", "escaped, as every logged value is");

        // Padding at the front and at the back alike: the head and the tail are kept, and the cut is counted.
        string longer = "select 'head' " + new string(' ', 50_000) + " select 'tail'";
        string capped = StudioLogValue.CapStatement(longer);

        capped.Should().StartWith("select 'head' ").And.EndWith(" select 'tail'")
            .And.Contain(" of " + longer.Length.ToString("N0", System.Globalization.CultureInfo.InvariantCulture) + " characters cut) … ");
        capped.Length.Should().BeLessThan(StudioLogValue.MaxStatementLength + 64);

        // A cut splits neither a surrogate pair nor an escape.
        string elephants = string.Concat(Enumerable.Repeat("🐘\n", 20_000));
        string kept = StudioLogValue.CapStatement(elephants);
        PairsAreWhole(kept).Should().BeTrue();
        HasRawControl(kept).Should().BeFalse();
        kept.Should().StartWith("🐘\\n").And.EndWith("🐘\\n");
    }

    // ---------------------------------------------------------------------------------------------------------------
    // MartenStudio's own audit events.

    [Fact]
    public void The_Marten_audit_events_write_the_user_and_the_scope_escaped_and_bounded()
    {
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("marten");

        logger.SchemaChangeApplied(HostileStoreKey, HostileStoreKey, HostileDatabaseId, "applied");
        logger.ProjectionRebuildStarted(HostileStoreKey, "DailySales", HostileStoreKey, HostileDatabaseId);
        logger.DaemonControlRequested(HostileStoreKey, "Rewind", "DailySales", HostileStoreKey, HostileDatabaseId);

        captured.Entries.Select(static x => x.EventId.Id).Should().Equal(9206, 9207, 9209);

        foreach (CapturedLogEntry entry in captured.Entries)
        {
            HasRawControl(entry.Message).Should().BeFalse("event {0} writes the user escaped", entry.EventId.Id);
            entry.Message.Should().Contain(StudioLogValue.Cap(HostileStoreKey)).And.Contain(StudioLogValue.Cap(HostileDatabaseId));
            entry.Message.Length.Should().BeLessThan(3 * StudioLogValue.MaxTextLength);
        }
    }

    /// <summary>The generated templates behind the Marten audit events are called by their capping wrappers alone.</summary>
    [Fact]
    public void Only_the_Marten_wrappers_call_the_raw_templates()
    {
        List<string> raw = [];

        foreach (string file in SourceScanner.ShippedSourceFiles().Where(static x => Path.GetFileName(x) != "MartenLog.cs"))
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));
            raw.AddRange(RawTemplateCall().Matches(code).Select(x => SourceScanner.Relative(file) + ": " + x.Value));
        }

        raw.Should().BeEmpty("9206, 9207 and 9209 go through MartenLog's wrappers, which cap the user and the scope");

        string martenLog = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Services", "MartenLog.cs")));
        RawTemplateCall().Matches(martenLog).Should().HaveCount(3, "each wrapper calls its template once");

        foreach (string wrapper in new[] { "SchemaChangeApplied", "ProjectionRebuildStarted", "DaemonControlRequested" })
        {
            Regex.IsMatch(martenLog, @"public static void " + wrapper + @"\(this ILogger logger, string user[^{]*\{\s*\([^;]*StudioLogValue\.Cap\(user\)")
                .Should().BeTrue("the {0} wrapper caps the user before it writes", wrapper);
        }

        string write = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine("src", "MartenStudio", "Services", "Documents", "DocumentWriteService.cs")));
        Regex.IsMatch(write, @"\.DocumentWriteRoundTripDropped\(\s*StudioLogValue\.Cap\(UserName\(\)\)").Should().BeTrue(
            "9211, a Warning, writes the user capped; its type and id already go through WriteAuditText.Sanitize");
    }

    [GeneratedRegex(@"\.\s*(?:SchemaChangeApplied|ProjectionRebuildStarted|DaemonControlRequested)Raw\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex RawTemplateCall();

    // ---------------------------------------------------------------------------------------------------------------
    // Every Warning that names a value from the address.

    /// <summary>
    /// A direct <c>LogWarning</c>, <c>LogError</c> or <c>LogCritical</c> ships at the default log level, so one that names a
    /// value from the address - a document id, a collection alias, the scope's store, database or tenant - names it through
    /// <see cref="StudioLogValue.Cap" />.
    /// </summary>
    [Fact]
    public void Every_Warning_that_names_a_route_value_names_it_capped()
    {
        List<string> uncapped = [];
        int calls = 0;

        foreach (string file in SourceScanner.ShippedSourceFiles())
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));

            foreach (Match call in WarningCall().Matches(code))
            {
                calls++;
                uncapped.AddRange(UncappedRouteValues(code, call.Index + call.Length).Select(x => SourceScanner.Relative(file) + ": " + x));
            }
        }

        calls.Should().BeGreaterThan(10, "src/ logs Warnings directly, and a scan that finds none has stopped working");
        uncapped.Should().BeEmpty("a value from the address reaches a Warning only escaped and bounded");
    }

    /// <summary>The scan's anti-vacuity half.</summary>
    [Theory]
    [InlineData("logger.LogWarning(exception, \"x '{Alias}'\", alias);", "alias")]
    [InlineData("logger.LogWarning(exception, \"x '{Id}' of '{Alias}'\", id, StudioLogValue.Cap(alias));", "id")]
    [InlineData("logger.LogError(exception, \"x {StoreKey}\", scope.StoreKey);", "scope.StoreKey")]
    [InlineData("logger.LogWarning(exception, \"x '{Alias}'\", table.Alias);", "table.Alias")]
    [InlineData("logger.LogWarning(exception, \"x '{Alias}'\", StudioLogValue.Cap(table.Alias));", null)]
    [InlineData("logger.LogWarning(exception, \"x {StoreKey}\", resolved.Registration.Key, action);", null)]
    [InlineData("logger.LogWarning(exception, \"x {DatabaseId}\", resolved.Database.Id.Identity);", null)]
    public void The_route_value_scan_catches_an_uncapped_value(string source, string? caught)
    {
        string code = SourceScanner.StripCommentsAndStrings(source);
        Match call = WarningCall().Match(code);
        call.Success.Should().BeTrue();

        IReadOnlyList<string> found = UncappedRouteValues(code, call.Index + call.Length);

        if (caught is null)
        {
            found.Should().BeEmpty();
        }
        else
        {
            found.Should().Equal(caught);
        }
    }

    /// <summary>The top-level arguments of the call whose list starts at <paramref name="start" /> that name a route value bare.</summary>
    private static IReadOnlyList<string> UncappedRouteValues(string code, int start)
    {
        List<string> arguments = [];
        int depth = 0;
        int from = start;

        for (int index = start; index < code.Length; index++)
        {
            char c = code[index];

            if (c is '(' or '[' or '{')
            {
                depth++;
            }
            else if (c is ')' or ']' or '}')
            {
                if (depth == 0)
                {
                    arguments.Add(code[from..index].Trim());
                    break;
                }

                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                arguments.Add(code[from..index].Trim());
                from = index + 1;
            }
        }

        return [.. arguments.Where(static x => RouteValue().IsMatch(x))];
    }

    [GeneratedRegex(@"\.\s*Log(?:Warning|Error|Critical)\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex WarningCall();

    /// <summary>A bare document id or collection alias, or a scope's or table's store, database, tenant or alias.</summary>
    [GeneratedRegex(@"^(?:id|alias|(?:[A-Za-z_]\w*\.)+(?:Alias|StoreKey|DatabaseId|TenantId))$", RegexOptions.CultureInvariant)]
    private static partial Regex RouteValue();
}
