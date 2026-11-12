using System.Text;

using KStudio.Core.Decoding;
using KStudio.Core.Predicates;
using KStudio.Core.Redaction;
using KStudio.Core.Wire;

namespace KStudio.Tests.Predicates;

/// <summary>What the value-side tests share: compile a search under the payload grant for a topic with some rules, and judge one record.</summary>
internal static class ValueHarness
{
    internal const KafkaReadGrant Payload = KafkaReadGrant.Envelope | KafkaReadGrant.Payload;

    /// <summary>The rules a topic would declare.</summary>
    /// <param name="configure">Declares them.</param>
    internal static KafkaRedactionRules Rules(Action<KafkaRedactionBuilder> configure)
    {
        KafkaRedactionRules rules = new();
        configure(new KafkaRedactionBuilder(rules));
        return rules;
    }

    internal static PredicateCompilation Compile(
        string text,
        KafkaRedactionRules? rules = null,
        KafkaReadGrant grant = Payload,
        bool declaresCloudEvents = false)
    {
        PredicateParseResult parsed = PredicateParser.Parse(text);
        parsed.Errors.Should().BeEmpty("'" + text + "' parses");
        return PredicateCompiler.Compile(
            parsed.Tree,
            new PredicateCompileContext("t", KafkaKeyFormat.Utf8String, false, new KafkaRedactor(rules), grant, declaresCloudEvents));
    }

    internal sealed record Outcome(TriState Verdict, DecodeCounters Counters, KafkaUnknownReason? Reason, CompiledPredicate Predicate, RecordEvaluationContext Context);

    internal static Outcome Run(
        string text,
        WireRecord record,
        KafkaRedactionRules? rules = null,
        IKafkaRecordDecoder? decoder = null,
        bool declaresCloudEvents = false,
        KafkaReadGrant grant = Payload,
        int maxDecodeBytes = 1024 * 1024,
        Action<RedactedValue>? onRedactionFailed = null)
    {
        declaresCloudEvents |= KafkaDecoders.BuiltInKind(decoder) == KafkaBuiltInDecoder.CloudEvents;
        PredicateCompilation compiled = Compile(text, rules, grant, declaresCloudEvents);
        compiled.Errors.Should().BeEmpty();
        DecodeCounters counters = new();
        RecordEvaluationContext context = new(
            record, KafkaKeyFormat.Utf8String, counters, maxDecodeBytes, declaresCloudEvents, grant, decoder, new KafkaRedactor(rules), null, onRedactionFailed);
        TriState verdict = compiled.Predicate!.Evaluate(context);
        return new Outcome(verdict, counters, context.UnknownReason, compiled.Predicate, context);
    }

    internal static TriState Verdict(string text, WireRecord record, KafkaRedactionRules? rules = null, IKafkaRecordDecoder? decoder = null, bool declaresCloudEvents = false) =>
        Run(text, record, rules, decoder, declaresCloudEvents).Verdict;

    internal static WireRecord Json(string? value, string? key = "k", params (string Name, string? Value)[] headers) =>
        new(
            "t",
            0,
            5,
            key is null ? null : Encoding.UTF8.GetBytes(key),
            value is null ? null : Encoding.UTF8.GetBytes(value),
            [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
            1_000,
            WireTimestampType.CreateTime,
            0);

    internal static WireRecord Bytes(byte[]? value, params (string Name, string? Value)[] headers) =>
        new(
            "t",
            0,
            5,
            Encoding.UTF8.GetBytes("k"),
            value,
            [.. headers.Select(static x => new WireHeader(x.Name, x.Value is null ? null : Encoding.UTF8.GetBytes(x.Value)))],
            1_000,
            WireTimestampType.CreateTime,
            0);
}
