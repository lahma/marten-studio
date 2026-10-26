using MartenStudio.Services.Events;

namespace MartenStudio.Tests.Events;

/// <summary>
/// M3's fingerprint: a dead letter's message with its variable parts set aside, over the messages Marten's daemon, Npgsql
/// and .NET actually write. <c>DeadLetterGroupsLiveTests</c> holds PostgreSQL's spelling of the same rule to these answers.
/// </summary>
public class DeadLetterFingerprintTests
{
    /// <summary>A message as it reaches a dead letter, and what it fingerprints to.</summary>
    public static TheoryData<string, string> Messages { get; } = new()
    {
        // Marten's daemon, both of its shapes (JasperFx 2.74.0 and 2.95.1: ApplyEventException's own message; and
        // EventDeserializationFailureException's).
        { "Failure to apply event #42 Id(0198a6b4-7c3e-7d2a-9f1b-2c3d4e5f6a7b)", "Failure to apply event #{n} Id({guid})" },
        { "Failure to apply event #1 Id(00000000-0000-0000-0000-000000000000)", "Failure to apply event #{n} Id({guid})" },
        { "Event deserialization error on sequence = 1234 for event type order_placed", "Event deserialization error on sequence = {n} for event type order_placed" },
        { "Event deserialization error on sequence = 7 for event type ", "Event deserialization error on sequence = {n} for event type" },

        // Marten's own exceptions, as a host's projection lets them through.
        { "Optimistic concurrency check failed for Shop.OrderSummary #5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f", "Optimistic concurrency check failed for Shop.OrderSummary #{guid}" },
        { "Unexpected starting version number for event stream '5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f', expected 3 but was 4", "Unexpected starting version number for event stream '{string}', expected {n} but was {n}" },

        // Npgsql's PostgresException: "SQLSTATE: text", and the detail it redacts unless asked.
        {
            "23505: duplicate key value violates unique constraint \"pk_mt_doc_order\"\n\nDETAIL: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.",
            "{n}: duplicate key value violates unique constraint \"{string}\" DETAIL: Detail redacted as it may contain sensitive data. Specify '{string}' in the connection string to include this information."
        },
        {
            "23505: duplicate key value violates unique constraint \"pk_mt_doc_order\"\n\nDETAIL: Key (id)=(8f1d5a6e-1a2b-4c3d-9e8f-000000000001) already exists.",
            "{n}: duplicate key value violates unique constraint \"{string}\" DETAIL: Key (id)=({guid}) already exists."
        },
        { "57014: canceling statement due to statement timeout", "{n}: canceling statement due to statement timeout" },
        { "55P03: canceling statement due to lock timeout", "55P03: canceling statement due to lock timeout" },
        {
            "40P01: deadlock detected\n\nDETAIL: Process 4242 waits for ShareLock on transaction 1337; blocked by process 4343.",
            "40P01: deadlock detected DETAIL: Process {n} waits for ShareLock on transaction {n}; blocked by process {n}."
        },
        { "22P02: invalid input syntax for type uuid: \"not-a-guid\"", "22P02: invalid input syntax for type uuid: \"{string}\"" },
        { "42P01: relation \"public.mt_doc_order\" does not exist", "42P01: relation \"{string}\" does not exist" },
        { "22003: value \"12345678901\" is out of range for type integer", "{n}: value \"{string}\" is out of range for type integer" },
        {
            "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' (currently 15 seconds) in your connection string.",
            "The connection pool has been exhausted, either raise '{string}' (currently {n}) or '{string}' (currently {n} seconds) in your connection string."
        },
        { "Exception while reading from stream", "Exception while reading from stream" },

        // .NET's own, and a projection's.
        { "Attempted to divide by zero.", "Attempted to divide by zero." },
        { "Order 1043 cannot ship before it is paid (customer 'acme-17')", "Order {n} cannot ship before it is paid (customer '{string}')" },
        { "Order 1,043.50 is over the limit", "Order {n} is over the limit" },
        { "Can't find 'Foo' in the catalog, and it doesn't exist", "Can't find '{string}' in the catalog, and it doesn't exist" },

        // A quote never opens a string straight after a quote: at the start of '' the two engines would part otherwise.
        { "'' and 'x' then ''", "'{string}' and '{string}' then '{string}'" },
        { "'a''b' it's", "'{string}''b' it's" },
        { "The value 2026-10-27T14:03:22.1234567+00:00 is too late", "The value {timestamp} is too late" },
        { "Due 2026-10-27, paid 2026-10-28 09:15Z", "Due {timestamp}, paid {timestamp}" },
        { "Expired at 10/27/2026 2:03:22 PM", "Expired at {timestamp}" },
        { "Läuft ab am 27.10.2026 14:03:22", "Läuft ab am {timestamp}" },
        { "Timed out after 00:00:30", "Timed out after {timestamp}" },
        { "Hash mismatch: expected 3f2a9c1b7e5d4f60 got 0x1F", "Hash mismatch: expected {hex} got {hex}" },
        { "Token 5F9B3C1E9A4F4B6E8F0C3A2B1C0D9E8F rejected", "Token {hex} rejected" },

        // What is a name, not a quantity, stays.
        { "partition_2 of V2 failed with 42P01", "partition_2 of V2 failed with 42P01" },
        { "deadbeef and facade are words", "deadbeef and facade are words" },
        { "Timeout of 30s", "Timeout of 30s" },

        // Whitespace is one space, and none at either end.
        { "  Line one\n\tline   two  ", "Line one line two" },
        { string.Empty, string.Empty },
    };

    [Theory]
    [MemberData(nameof(Messages))]
    public void A_message_fingerprints_to_its_shape(string message, string fingerprint) =>
        DeadLetterFingerprint.Of(message).Should().Be(fingerprint);

    [Fact]
    public void No_message_has_no_fingerprint() => DeadLetterFingerprint.Of(null).Should().BeNull();

    /// <summary>The point of it: two failures that differ only in what varies fall into one group, and two that differ in what they say do not.</summary>
    [Fact]
    public void Messages_that_differ_only_in_their_variable_parts_share_a_fingerprint()
    {
        DeadLetterFingerprint.Of("Order 17 for 'acme' failed at 2026-10-27T09:00:00Z (attempt 3)")
            .Should().Be(DeadLetterFingerprint.Of("Order 94012 for 'globex' failed at 2026-10-28T23:59:59.5+02:00 (attempt 1)"));

        DeadLetterFingerprint.Of("Order 17 cannot ship")
            .Should().NotBe(DeadLetterFingerprint.Of("Order 17 cannot be paid"));
    }

    /// <summary>
    /// The fingerprint reads the first thousand characters - code points, as PostgreSQL's <c>left</c> counts them, so a
    /// character outside the Basic Multilingual Plane is one and not two.
    /// </summary>
    [Fact]
    public void A_long_message_is_cut_at_a_thousand_code_points()
    {
        string emoji = "\U0001F600";
        string message = string.Concat(Enumerable.Repeat(emoji, 999)) + "ab" + new string('x', 500);

        string? fingerprint = DeadLetterFingerprint.Of(message);

        fingerprint.Should().Be(string.Concat(Enumerable.Repeat(emoji, 999)) + "a");
        DeadLetterFingerprint.Truncate(message, 1_000).Length.Should().Be((999 * 2) + 1);
        DeadLetterFingerprint.Truncate("short", 1_000).Should().Be("short");
    }

    /// <summary>
    /// The rule is written for two engines: every pattern stays inside the subset .NET and PostgreSQL's ARE read alike -
    /// no escapes, no lazy quantifier, no case-insensitive flag - and each replacement puts back what its pattern captured.
    /// </summary>
    [Fact]
    public void Every_pattern_is_in_the_subset_both_engines_read_alike()
    {
        foreach (DeadLetterFingerprintRule rule in DeadLetterFingerprint.Rules)
        {
            rule.Pattern.Should().NotContain("\\", "{0}: a backslash is an escape both engines read differently", rule);
            rule.Pattern.Should().NotContain("*?").And.NotContain("+?").And.NotContain("??", "{0}: a lazy quantifier", rule);
            rule.Pattern.Should().NotContain("(?i").And.NotContain("(?<", "{0}: an inline flag or a named group", rule);

            if (rule.KeepsLeadingCharacter)
            {
                rule.Pattern.Should().StartWith("(^|[^0-9A-Za-z_", "{0} puts its leading character back", rule);
                rule.DotNetReplacement.Should().Be("${1}" + rule.Placeholder);
                rule.PostgresReplacement.Should().Be("\\1" + rule.Placeholder);
            }
            else
            {
                rule.DotNetReplacement.Should().Be(rule.Placeholder);
                rule.PostgresReplacement.Should().Be(rule.Placeholder);
            }

            rule.Placeholder.Should().NotContain("$").And.NotContain("\\").And.NotContain("&",
                "{0}: a placeholder is literal in both engines' replacements", rule);
        }
    }

    /// <summary>No placeholder is a match for a later rule, so a message fingerprints the same however often it is fingerprinted.</summary>
    [Theory]
    [MemberData(nameof(Messages))]
    public void A_fingerprint_is_its_own_fingerprint(string message, string fingerprint)
    {
        _ = message;
        DeadLetterFingerprint.Of(fingerprint).Should().Be(fingerprint);
    }
}
