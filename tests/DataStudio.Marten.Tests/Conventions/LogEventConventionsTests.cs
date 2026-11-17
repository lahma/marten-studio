namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// The Marten module's half of the one-wording rule (v2 step 6): its events are declared once across <c>src/</c>, and no template
/// of the module, the shared studio or the engine names a module's product. The engine's and the shell's and the Kafka module's
/// halves are the same two checks in their own suites, over the same scan (<see cref="LogEventScan" />).
/// </summary>
public class LogEventConventionsTests
{
    [Fact]
    public void Every_event_id_is_declared_exactly_once_across_src()
    {
        LogEventScan.AssertEveryIdIsDeclaredOnce("src/DataStudio.Marten", "src/DataStudio.Postgres.Diagnostics");
    }

    [Fact]
    public void No_log_template_names_a_modules_product()
    {
        LogEventScan.AssertNoTemplateNamesAProduct("src/DataStudio.Marten", "src/DataStudio.Postgres", "src/DataStudio.Postgres.Diagnostics");
    }

    /// <summary>The scan's anti-vacuity half: it finds an attribute and a direct call, and ignores one in a comment.</summary>
    [Theory]
    [InlineData("[LoggerMessage(EventId = 7, Level = LogLevel.Warning, Message = \"Marten Studio user {User} did it\")]\npublic static partial void M(this ILogger l);", 7, "Marten Studio user {User} did it")]
    [InlineData("    [LoggerMessage(EventId = 8,\n        Message = \"KStudio \" + \"split\")]\nprivate static partial void M(this ILogger l);", 8, "KStudio split")]
    [InlineData("logger.LogWarning(exception, \"Postgres Studio could not {Thing}\", thing);", null, "Postgres Studio could not {Thing}")]
    [InlineData("Logger.LogDebug(\"Data Studio could not\");", null, "Data Studio could not")]
    public void The_scan_reads_an_event_and_a_direct_call(string source, int? id, string template)
    {
        LogEventScan.Site site = LogEventScan.Read("f.cs", source).Should().ContainSingle().Subject;

        site.EventId.Should().Be(id);
        site.Template.Should().Be(template);
    }

    [Theory]
    [InlineData("/// [LoggerMessage(EventId = 3, Message = \"x\")]\nclass C { }")]
    [InlineData("// logger.LogWarning(\"Marten Studio in a comment\");\nclass C { }")]
    [InlineData("class C { string s = \"logger.LogWarning(x)\"; void M() { logger.LogDebug(exception, message); } }")]
    public void The_scan_ignores_prose_and_a_call_with_no_literal(string source)
    {
        LogEventScan.Read("f.cs", source).Should().BeEmpty();
    }
}
