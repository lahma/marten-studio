namespace MartenStudio.Integration.Tests.Events;

/// <summary>
/// The messages the fingerprint's parity test runs through both engines: every message <c>DeadLetterFingerprintTests</c>
/// reads in the fast suite, and the edge cases where .NET's regex and PostgreSQL's ARE could part - adjacent tokens,
/// tokens at either end, a match that has to give back, a quote that never closes, text past the length the rule reads.
/// </summary>
internal static class DeadLetterFingerprintCorpus
{
    public static IReadOnlyList<string> Messages { get; } =
    [
        // Marten's daemon.
        "Failure to apply event #42 Id(0198a6b4-7c3e-7d2a-9f1b-2c3d4e5f6a7b)",
        "Failure to apply event #1 Id(00000000-0000-0000-0000-000000000000)",
        "Event deserialization error on sequence = 1234 for event type order_placed",
        "Event deserialization error on sequence = 7 for event type ",
        "Optimistic concurrency check failed for Shop.OrderSummary #5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f",
        "Unexpected starting version number for event stream '5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f', expected 3 but was 4",

        // Npgsql.
        "23505: duplicate key value violates unique constraint \"pk_mt_doc_order\"\n\nDETAIL: Detail redacted as it may contain sensitive data. Specify 'Include Error Detail' in the connection string to include this information.",
        "23505: duplicate key value violates unique constraint \"pk_mt_doc_order\"\n\nDETAIL: Key (id)=(8f1d5a6e-1a2b-4c3d-9e8f-000000000001) already exists.",
        "57014: canceling statement due to statement timeout",
        "55P03: canceling statement due to lock timeout",
        "40P01: deadlock detected\n\nDETAIL: Process 4242 waits for ShareLock on transaction 1337; blocked by process 4343.",
        "22P02: invalid input syntax for type uuid: \"not-a-guid\"",
        "42P01: relation \"public.mt_doc_order\" does not exist",
        "22003: value \"12345678901\" is out of range for type integer",
        "The connection pool has been exhausted, either raise 'Max Pool Size' (currently 100) or 'Timeout' (currently 15 seconds) in your connection string.",
        "Exception while reading from stream",

        // .NET and projections.
        "Attempted to divide by zero.",
        "Order 1043 cannot ship before it is paid (customer 'acme-17')",
        "Order 1,043.50 is over the limit",
        "Can't find 'Foo' in the catalog, and it doesn't exist",
        "The value 2026-10-27T14:03:22.1234567+00:00 is too late",
        "Due 2026-10-27, paid 2026-10-28 09:15Z",
        "Expired at 10/27/2026 2:03:22 PM",
        "Läuft ab am 27.10.2026 14:03:22",
        "Timed out after 00:00:30",
        "Hash mismatch: expected 3f2a9c1b7e5d4f60 got 0x1F",
        "Token 5F9B3C1E9A4F4B6E8F0C3A2B1C0D9E8F rejected",
        "partition_2 of V2 failed with 42P01",
        "deadbeef and facade are words",
        "Timeout of 30s",
        "  Line one\n\tline   two  ",
        string.Empty,

        // Where two engines could part.
        "1 2 3,4 5.6.7 8-9 10:11 1.5x x1 1x 1_2",
        "12345678 1234567a 123456ab abcdef12 abcdefgh12 0x 0xZ 00x1 a0x1 0X1f",
        "2026-10-27-12 2026-10-27T12:30 2026-10-27 12:30:45,5+0230 2026-10-27+02 2026-1027",
        "1/2/2026 123/4/2026 1.2.2026 10:00 9:5:00 9:05:0 12:34:56.7890 1:2:3",
        "'' \"\" 'a''b' \"a\"\"b\" 'unterminated \"also unterminated x'y' it's",
        "(5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8f)(5F9B3C1E-9A4F-4B6E-8F0C-3A2B1C0D9E8F)x5f9b3c1e-9a4f-4b6e-8f0c-3a2b1c0d9e8fx",
        "café 42 naïve 0xdead ünïcødé 3.14 \U0001F600 7",
        "#1#2#3 ##4 a#5 6#",
        "\r\n\f\v mixed \t whitespace \n",
        new string('9', 1_500),
        new string('a', 995) + " 1234 5678 9",
        string.Concat(Enumerable.Repeat("\U0001F600", 998)) + " 12 34",
    ];
}
