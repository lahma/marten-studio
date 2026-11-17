using System.Globalization;

namespace DataStudio.Kafka.Tests.Support;

/// <summary>
/// Which characters a log value must never carry raw: a control, format or line or paragraph separator character. A copy of the diagnostics engine's
/// <c>StudioLogValue.IsEscaped</c>, which this suite cannot reference (a Kafka-only host has no engine in its output); the live project's parity test
/// compares the two over every <see cref="char" />.
/// </summary>
public static class TestLogEscape
{
    /// <summary>Whether <paramref name="c" /> is written as an escape.</summary>
    /// <param name="c">The character.</param>
    public static bool IsEscaped(char c) => char.GetUnicodeCategory(c) is UnicodeCategory.Control
        or UnicodeCategory.Format
        or UnicodeCategory.LineSeparator
        or UnicodeCategory.ParagraphSeparator;
}
