using System.Reflection;

using KStudio.Core.Logging;

using Microsoft.Extensions.Logging;

namespace KStudio.Tests.Logging;

/// <summary>
/// The engine's log events (decision 11): ids unique and inside 9650 to 9699 (ENG-A's are 9650 to 9664, ENG-B's 9665 to 9674, ENG-D's 9675, ENG-E's 9676), each level as designed, and no
/// parameter that could carry content. Engine events carry cluster keys, topics, partitions, offsets, counts, code names and librdkafka's own
/// text, and never a predicate, a literal, a key, a header, a value, an owner or a user.
/// </summary>
public class EngineLogEventsTests
{
    private const BindingFlags Methods = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

    /// <summary>Id → level; <see langword="null" /> is "chosen by the caller" (Warning the first time within ten minutes, Debug after).</summary>
    private static readonly Dictionary<int, LogLevel?> EngineEvents = new()
    {
        [9650] = LogLevel.Debug,
        [9651] = LogLevel.Debug,
        [9652] = LogLevel.Debug,
        [9653] = LogLevel.Debug,
        [9654] = null,
        [9655] = null,
        [9656] = LogLevel.Debug,
        [9657] = LogLevel.Debug,
        [9658] = LogLevel.Error,
        [9659] = LogLevel.Debug,
        [9660] = LogLevel.Debug,
        [9661] = LogLevel.Debug,
        [9662] = LogLevel.Debug,
        [9663] = LogLevel.Error,
        [9664] = LogLevel.Debug,

        // ENG-B: the scan engine's (9665 to 9674).
        [9665] = LogLevel.Debug,
        [9666] = LogLevel.Debug,
        [9667] = LogLevel.Debug,
        [9668] = LogLevel.Warning,
        [9669] = LogLevel.Debug,
        [9670] = LogLevel.Debug,
        [9671] = LogLevel.Debug,
        [9672] = LogLevel.Debug,
        [9673] = LogLevel.Debug,
        [9674] = LogLevel.Debug,

        // ENG-D: a payload redaction rule could not be applied to a value.
        [9675] = LogLevel.Debug,

        // ENG-E: a partitioner verification could not read its sample (kind only).
        [9676] = LogLevel.Debug,
    };

    private static readonly string[] AllowedParameters =
    [
        "logger", "level", "exception", "cluster", "call", "operation", "resourceType", "resource", "api", "code", "client", "facility",
        "text", "setting", "count", "idleSeconds", "partition", "topic", "logStart",
        "scanId", "records", "bytes", "matches", "undecidable", "elapsedMilliseconds", "reason", "refusal", "lost", "position", "isolation",
        "offsets", "partitions", "topics", "exceptionKind", "decoder", "kind",
    ];

    private static readonly string[] ForbiddenPlaceholders = ["Key", "Value", "Header", "Predicate", "Literal", "Owner", "User", "Payload", "Record"];

    private static IEnumerable<(MethodInfo Method, LoggerMessageAttribute Attribute)> Events() =>
        typeof(KStudioLog).GetMethods(Methods)
            .Select(static x => (Method: x, Attribute: x.GetCustomAttribute<LoggerMessageAttribute>()))
            .Where(static x => x.Attribute is not null)
            .Select(static x => (x.Method, x.Attribute!));

    [Fact]
    public void Ids_are_unique_and_inside_the_engines_block()
    {
        int[] ids = [.. Events().Select(static x => x.Attribute.EventId)];

        ids.Should().NotBeEmpty("scanning nothing would pass for the wrong reason");
        ids.Should().OnlyHaveUniqueItems();
        ids.Should().OnlyContain(static x => x >= 9650 && x <= 9699, "KStudio's engine owns 9650 to 9699");
    }

    [Fact]
    public void The_engine_defines_exactly_ENG_As_events_9650_to_9664_ENG_Bs_9665_to_9674_ENG_Ds_9675_and_ENG_Es_9676()
    {
        Events().Select(static x => x.Attribute.EventId).Order().Should().Equal(EngineEvents.Keys.Order());
    }

    [Fact]
    public void ENG_E_leaves_9677_to_9699_for_the_later_milestones()
    {
        Events().Select(static x => x.Attribute.EventId).Should().NotContain(static x => x >= 9677, "9677 to 9699 are for the later packets");
    }

    [Fact]
    public void Each_event_logs_at_the_level_the_design_gives_it()
    {
        foreach ((MethodInfo method, LoggerMessageAttribute attribute) in Events())
        {
            LogLevel? expected = EngineEvents[attribute.EventId];

            if (expected is null)
            {
                method.GetParameters().Should().Contain(static x => x.ParameterType == typeof(LogLevel), $"{method.Name} takes its level from the caller");
            }
            else
            {
                attribute.Level.Should().Be(expected.Value, method.Name);
            }
        }
    }

    [Fact]
    public void No_event_has_a_parameter_that_could_carry_content_or_a_placeholder_named_for_it()
    {
        foreach ((MethodInfo method, LoggerMessageAttribute attribute) in Events())
        {
            method.GetParameters().Select(static x => x.Name!).Should().OnlyContain(
                static x => AllowedParameters.Contains(x), $"{method.Name}'s parameters are an allow-list: cluster keys, topics, partitions, offsets, counts, codes");

            foreach (string placeholder in ForbiddenPlaceholders)
            {
                attribute.Message.Should().NotContain("{" + placeholder + "}", $"{method.Name} must not name content");
            }
        }
    }

    [Fact]
    public void Only_the_text_parameter_carries_librdkafkas_own_words_and_it_is_the_only_unbounded_string()
    {
        MethodInfo clientLog = typeof(KStudioLog).GetMethod(nameof(KStudioLog.ClientLog), Methods)!;

        clientLog.GetParameters().Select(static x => x.Name).Should().Contain("text");
        Events().Where(static x => x.Attribute.EventId != 9656).SelectMany(static x => x.Method.GetParameters()).Select(static x => x.Name).Should().NotContain("text");
    }

    [Fact]
    public void The_loggers_use_the_engines_categories_so_one_filter_covers_the_engine()
    {
        CategoryProvider provider = new();
        using ILoggerFactory factory = LoggerFactory.Create(builder => builder.AddProvider(provider));

        KStudioLoggers loggers = new(factory);

        provider.Categories.Should().BeEquivalentTo("KStudio.Core.Clients", "KStudio.Core.Catalog", "KStudio.Core.Scans");
        loggers.Clients.Should().NotBeNull();
        loggers.Catalog.Should().NotBeNull();
        loggers.Scans.Should().NotBeNull();
    }

    private sealed class CategoryProvider : ILoggerProvider
    {
        public List<string> Categories { get; } = [];

        public ILogger CreateLogger(string categoryName)
        {
            Categories.Add(categoryName);
            return Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        }

        public void Dispose()
        {
        }
    }

    [Fact]
    public void The_two_levels_chosen_by_the_caller_start_as_warning_then_drop_to_debug_within_ten_minutes()
    {
        PostgresStudio.Tests.Support.ManualTimeProvider clock = new();
        KafkaLogOnce once = new(clock);

        KafkaLogOnce.WarningOrDebug(once, "k").Should().Be(LogLevel.Warning);
        KafkaLogOnce.WarningOrDebug(once, "k").Should().Be(LogLevel.Debug);
        KafkaLogOnce.WarningOrDebug(once, "other").Should().Be(LogLevel.Warning);

        clock.Advance(TimeSpan.FromMinutes(10));
        KafkaLogOnce.WarningOrDebug(once, "k").Should().Be(LogLevel.Warning);
        once.ShouldLog("x").Should().BeTrue();
        once.ShouldLog("x").Should().BeFalse();
    }
}
