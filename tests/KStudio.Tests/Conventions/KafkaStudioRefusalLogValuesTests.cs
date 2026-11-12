using System.Text.RegularExpressions;

using KStudio.Services;

using MartenStudio.Tests.Conventions;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Logging;

using PostgresStudio.Services;

namespace KStudio.Tests.Conventions;

/// <summary>
/// Hard rule 5 for KStudio's events (9600 to 9611): a value a visitor could shape - the address's cluster, a topic, a user's name, a predicate -
/// reaches the log only through <see cref="StudioLogValue" />. Held three ways: every wrapper writes hostile values escaped and bounded, every
/// <c>[LoggerMessage]</c> method is private to <c>KafkaStudioLog</c>, and none is called from anywhere else in KStudio.
/// </summary>
public partial class KafkaStudioRefusalLogValuesTests
{
    private static readonly string Hostile =
        "acme\r\n2026-11-12 12:00:00 warn: KStudio user admin was granted everything\u001b[2J\u202eesrever\u2028" + new string('x', 1_000);

    private static readonly string HostileText = "select 1;\r\n-- forged\u001b" + new string('z', 9_000);

    [Fact]
    public void Every_wrapper_writes_its_values_escaped_and_bounded()
    {
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("kstudio");
        DateTimeOffset when = DateTimeOffset.UnixEpoch;

        logger.SearchRan(Hostile, Hostile, Hostile, Hostile, when, when, 1, 2, Hostile, HostileText);
        logger.SearchNotRun(Hostile, Hostile, Hostile, Hostile, HostileText, HostileText);
        logger.CapabilityRefused(Hostile, Hostile, Hostile);
        logger.ClusterRefused(Hostile, Hostile, Hostile, Hostile, Hostile);
        logger.RecordOpened(Hostile, Hostile, 3, 4, Hostile, Hostile);
        logger.ClusterAddressIgnored(Hostile, Hostile);

        captured.Entries.Select(static x => x.EventId.Id).Should().Equal(9600, 9601, 9602, 9603, 9604, 9610);

        foreach (CapturedLogEntry entry in captured.Entries)
        {
            entry.Message.Any(StudioLogValue.IsEscaped).Should().BeFalse("event {0} writes every value a visitor shaped escaped", entry.EventId.Id);
            entry.Message.Should().NotContain(Hostile).And.NotContain(HostileText);
            entry.Message.Should().Contain(StudioLogValue.Cap(Hostile), "event {0} keeps the bounded value", entry.EventId.Id);
            entry.Message.Length.Should().BeLessThan(8 * StudioLogValue.MaxTextLength, "event {0} is bounded whatever was sent", entry.EventId.Id);
        }
    }

    [Fact]
    public void A_missing_tenant_is_written_as_none_and_a_missing_topic_as_the_cluster()
    {
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("kstudio");

        logger.ClusterRefused("ann", "primary", null, null, "kafka-topics");

        captured.Entries.Single().Message.Should().Contain("tenant (none), topic (cluster)");
    }

    [Fact]
    public void The_events_keep_their_ids_and_levels()
    {
        CapturingLoggerProvider captured = new();
        ILogger logger = captured.CreateFactory().CreateLogger("kstudio");
        DateTimeOffset when = DateTimeOffset.UnixEpoch;

        logger.SearchRan("u", "t", "c", null, when, when, 0, 0, "limit", "key = 'x'");
        logger.SearchNotRun("u", "t", "c", null, "refused", "key = 'x'");
        logger.CapabilityRefused("u", "SearchRecords", "KafkaStudioOptions.Capabilities.SearchRecords");
        logger.ClusterRefused("u", "c", null, "t", "p");
        logger.RecordOpened("u", "t", 0, 0, "c", null);
        logger.ClusterAddressIgnored("a", "b");
        KafkaStudioLog.LiveUpdateHandlerFailed(logger, LogLevel.Debug, new InvalidOperationException("x"));

        captured.Entries.Select(static x => (x.EventId.Id, x.Level)).Should().Equal(
            (9600, LogLevel.Information),
            (9601, LogLevel.Information),
            (9602, LogLevel.Warning),
            (9603, LogLevel.Warning),
            (9604, LogLevel.Information),
            (9610, LogLevel.Debug),
            (9611, LogLevel.Debug));
    }

    [Fact]
    public void Every_LoggerMessage_method_is_private_and_called_only_from_the_log_class()
    {
        string root = RepositoryRoot.Combine("src", "KStudio");
        string logFile = Path.Combine(root, "Services", "KafkaStudioLog.cs");
        string[] sources = [.. Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(static x => x.EndsWith(".cs", StringComparison.Ordinal) || x.EndsWith(".razor", StringComparison.Ordinal))
            .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
        string log = SourceScanner.StripCommentsAndStrings(File.ReadAllText(logFile));

        string[] generated = [.. GeneratedMethod().Matches(File.ReadAllText(logFile)).Select(static m => m.Groups[1].Value)];

        generated.Should().HaveCountGreaterThanOrEqualTo(7, "scanning nothing would pass for the wrong reason");
        File.ReadAllText(logFile).Should().NotContain("public static partial", "a generated method is private, so a call site cannot skip the wrapper");
        log.Should().NotBeEmpty();

        foreach (string method in generated)
        {
            sources.Where(x => x != logFile && Regex.IsMatch(File.ReadAllText(x), @"\b" + method + @"\s*\(", RegexOptions.None, TimeSpan.FromSeconds(5)))
                .Should().BeEmpty($"{method} is called through its wrapper only");
        }
    }

    [GeneratedRegex(@"private static partial void (\w+)\(", RegexOptions.None, matchTimeoutMilliseconds: 5000)]
    private static partial Regex GeneratedMethod();
}
