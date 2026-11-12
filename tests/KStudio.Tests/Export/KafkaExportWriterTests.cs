using System.Text;
using System.Text.Json;

using KStudio.Core.Decoding;
using KStudio.Core.Export;
using KStudio.Core.Redaction;
using KStudio.Core.Scanning;
using KStudio.Core.Wire;
using KStudio.Tests.Redaction;

using static KStudio.Tests.Predicates.ValueHarness;

namespace KStudio.Tests.Export;

/// <summary>
/// The export writer (AGENTS.md D45, D-M5 decisions 2.2 to 2.4 and 2.6): pure, it writes only what a <see cref="KafkaMatch" /> holds, so a secret the
/// match factory masked is in no file, in either format; whole rows only, up to the size limit; a spreadsheet's formulas are neutralised.
/// </summary>
public class KafkaExportWriterTests
{
    private const string Secret = "SENTINEL-7f3c2a";
    private static readonly UTF8Encoding Utf8 = new(false);

    private static KafkaMatch Make(WireRecord record, KafkaRedactionRules? rules = null, KafkaReadGrant grant = Payload, Action<KafkaScanLimits>? limits = null)
    {
        KafkaScanLimits caps = new();
        limits?.Invoke(caps);
        return KafkaMatchFactory.Create(record, PayloadRedactionTests.Topic(rules), grant, caps, new DecodeCounters());
    }

    private static WireRecord At(WireRecord record, string topic, int partition, long offset) =>
        new(topic, partition, offset, record.Key, record.Value, record.Headers, record.TimestampMs, record.TimestampType, record.LeaderEpoch);

    private static KafkaRedactionRules Fixture() =>
        Rules(static r =>
        {
            r.Key();
            r.Header("x-secret");
            r.JsonPath("$.customer.email").Searchable();
            r.Pattern(PayloadRedactionTests.PhonePattern, "phone");
        });

    /// <summary>The M3 redaction fixture: a record with every kind of mask on it, and one whose value a rule cannot be applied to.</summary>
    private static KafkaMatch[] RedactionMatches()
    {
        WireRecord masked = Json(
            PayloadRedactionTests.Document,
            Secret + "-key",
            ("authorization", "Bearer " + Secret + "-token"),
            ("x-secret", Secret + "-header"),
            ("x-plain", "visible"));

        // A JSON-path rule cannot be applied to text: the engine withholds the value whole.
        WireRecord withheld = Json("plain text holding " + Secret + "-text", "other", ("x-plain", "visible"));

        return [Make(At(masked, "orders", 0, 1), Fixture()), Make(At(withheld, "orders", 0, 2), Fixture())];
    }

    private static JsonElement[] Lines(KafkaExportFile file)
    {
        file.Content.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF], "JSON Lines has no byte-order mark");
        string text = Utf8.GetString(file.Content);
        text.Should().EndWith("\n").And.NotContain("\r\n");

        return [.. text.TrimEnd('\n').Split('\n').Select(static x => JsonDocument.Parse(x).RootElement.Clone())];
    }

    /// <summary>A small RFC 4180 reader: rows of cells, quotes undoubled.</summary>
    private static List<List<string>> ParseCsv(string text)
    {
        List<List<string>> rows = [];
        List<string> row = [];
        StringBuilder cell = new();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];

            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    cell.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    cell.Append(c);
                }
            }
            else if (c == '"')
            {
                quoted = true;
            }
            else if (c == ',')
            {
                row.Add(cell.ToString());
                cell.Clear();
            }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                row.Add(cell.ToString());
                cell.Clear();
                rows.Add(row);
                row = [];
                i++;
            }
            else
            {
                cell.Append(c);
            }
        }

        quoted.Should().BeFalse("every quote the writer opened is closed");
        cell.Length.Should().Be(0, "every row ends with CRLF");
        row.Should().BeEmpty();
        return rows;
    }

    [Fact]
    public void A_json_line_holds_the_records_fields_in_order_with_absent_keys_left_out()
    {
        KafkaMatch match = Make(At(Json("""{"a":1}""", "key-1", ("h1", "v1")), "orders", 3, 42));

        KafkaExportFile file = KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1024 * 1024);

        file.Refusal.Should().BeNull();
        file.Extension.Should().Be("jsonl");
        file.ContentType.Should().Be("application/x-ndjson");
        file.Written.Should().Be(1);
        file.LeftOut.Should().Be(0);
        file.IncludesValues.Should().BeTrue();

        JsonElement row = Lines(file).Single();
        row.EnumerateObject().Select(static x => x.Name).Should().Equal(
            "topic", "partition", "offset", "timestamp", "timestampType", "key", "keyFormat", "headers", "valueFormat", "valueLength", "value");
        row.GetProperty("topic").GetString().Should().Be("orders");
        row.GetProperty("partition").GetInt32().Should().Be(3);
        row.GetProperty("offset").GetInt64().Should().Be(42);
        row.GetProperty("timestamp").GetString().Should().MatchRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$");
        row.GetProperty("timestampType").GetString().Should().Be("CreateTime");
        row.GetProperty("key").GetString().Should().Be("key-1");
        row.GetProperty("headers")[0].GetProperty("name").GetString().Should().Be("h1");
        row.GetProperty("headers")[0].GetProperty("value").GetString().Should().Be("v1");
        row.GetProperty("valueLength").GetInt32().Should().Be(7);
    }

    [Fact]
    public void A_json_value_is_embedded_as_json_and_a_cut_or_text_value_is_a_string()
    {
        string big = "{\"a\":\"" + new string('x', 5_000) + "\"}";
        KafkaMatch json = Make(Json("""{"a":[1,2],"b":null}"""));
        KafkaMatch cut = Make(Json(big), limits: static l => l.MaxMatchBytes = 2_048);
        KafkaMatch text = Make(Json("just text"));

        JsonElement[] rows = Lines(KafkaExportWriter.Write([json], KafkaExportFormat.JsonLines, 1 << 20));
        rows[0].GetProperty("value").ValueKind.Should().Be(JsonValueKind.Object);
        rows[0].GetProperty("value").GetProperty("a")[1].GetInt32().Should().Be(2);
        rows[0].TryGetProperty("valueTruncated", out _).Should().BeFalse();

        JsonElement cutRow = Lines(KafkaExportWriter.Write([cut], KafkaExportFormat.JsonLines, 1 << 20)).Single();
        cutRow.GetProperty("value").ValueKind.Should().Be(JsonValueKind.String, "a cut JSON text is not JSON");
        cutRow.GetProperty("valueTruncated").GetBoolean().Should().BeTrue();

        Lines(KafkaExportWriter.Write([text], KafkaExportFormat.JsonLines, 1 << 20)).Single().GetProperty("value").GetString().Should().Be("just text");
    }

    [Fact]
    public void A_tombstone_is_a_null_value_and_a_binary_value_is_its_hex_preview()
    {
        KafkaMatch tombstone = Make(Json(null));
        KafkaMatch binary = Make(Bytes([0x00, 0xFF, 0x10, 0x80]));

        Lines(KafkaExportWriter.Write([tombstone], KafkaExportFormat.JsonLines, 1 << 20)).Single().GetProperty("value").ValueKind.Should().Be(JsonValueKind.Null);

        JsonElement row = Lines(KafkaExportWriter.Write([binary], KafkaExportFormat.JsonLines, 1 << 20)).Single();
        row.GetProperty("value").GetString().Should().Be(binary.ValuePreview);
        row.GetProperty("value").GetString().Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void An_envelope_row_has_no_value_whatever_the_visitors_grant_is_at_export_time()
    {
        KafkaMatch envelope = Make(Json("""{"secret":"SENTINEL-envelope"}"""), grant: KafkaReadGrant.Envelope);

        envelope.ReadWithPayload.Should().BeFalse();

        KafkaExportFile jsonl = KafkaExportWriter.Write([envelope], KafkaExportFormat.JsonLines, 1 << 20);
        KafkaExportFile csv = KafkaExportWriter.Write([envelope], KafkaExportFormat.Csv, 1 << 20);

        JsonElement row = Lines(jsonl).Single();
        row.TryGetProperty("value", out _).Should().BeFalse();
        row.TryGetProperty("valueTruncated", out _).Should().BeFalse();
        row.TryGetProperty("valueWithheldBy", out _).Should().BeFalse();
        row.GetProperty("valueFormat").GetString().Should().NotBeNullOrEmpty("the envelope's own metadata stays");
        row.GetProperty("valueLength").GetInt32().Should().BeGreaterThan(0);
        jsonl.IncludesValues.Should().BeFalse();

        ParseCsv(Utf8.GetString(csv.Content)[1..])[1][10].Should().BeEmpty();
        csv.IncludesValues.Should().BeFalse();
        Utf8.GetString(jsonl.Content).Should().NotContain("SENTINEL");
        Utf8.GetString(csv.Content).Should().NotContain("SENTINEL");
    }

    [Fact]
    public void Redaction_holds_in_json_lines_the_secret_is_in_no_byte_and_each_mask_reads_as_on_the_page()
    {
        KafkaMatch[] matches = RedactionMatches();

        KafkaExportFile file = KafkaExportWriter.Write(matches, KafkaExportFormat.JsonLines, 1 << 20);
        string text = Utf8.GetString(file.Content);

        text.Should().NotContain("SENTINEL").And.NotContain("555 0199");

        JsonElement[] rows = Lines(file);
        JsonElement first = rows[0];

        first.GetProperty("key").GetString().Should().Be(matches[0].Key.Display);
        first.GetProperty("key").GetString().Should().StartWith("••• (redacted by");
        first.GetProperty("headers").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Equal("authorization", "x-secret", "x-plain");
        first.GetProperty("headers")[0].GetProperty("value").GetString().Should().StartWith("••• (redacted by");
        first.GetProperty("headers")[1].GetProperty("value").GetString().Should().StartWith("••• (redacted by");
        first.GetProperty("headers")[2].GetProperty("value").GetString().Should().Be("visible");
        first.GetProperty("value").GetProperty("customer").GetProperty("email").GetString().Should().Be("••• (redacted by $.customer.email)");
        first.GetProperty("value").GetProperty("note").GetString().Should().Contain("••• (redacted by pattern phone)");
        first.GetProperty("redacted").EnumerateArray().Select(static x => x.GetString()).Should().Equal(
            ["key", "header:authorization", "header:x-secret", .. matches[0].ValueMaskedBy]);

        JsonElement second = rows[1];
        second.TryGetProperty("value", out _).Should().BeFalse("a value a rule cannot be applied to is withheld, not written");
        second.GetProperty("valueWithheldBy").GetString().Should().Be(matches[1].ValueWithheldBy);
        second.GetProperty("redacted").EnumerateArray().Select(static x => x.GetString()).Should().Equal("key");
    }

    [Fact]
    public void Redaction_holds_in_csv_the_secret_is_in_no_byte_and_each_mask_reads_as_on_the_page()
    {
        KafkaMatch[] matches = RedactionMatches();

        KafkaExportFile file = KafkaExportWriter.Write(matches, KafkaExportFormat.Csv, 1 << 20);
        string text = Utf8.GetString(file.Content);

        text.Should().NotContain("SENTINEL").And.NotContain("555 0199");

        List<List<string>> rows = ParseCsv(text[1..]);
        rows[0].Should().Equal(
            "topic", "partition", "offset", "timestamp", "timestamp_type", "key", "key_format", "headers", "value_format", "value_length", "value", "value_truncated", "redacted");
        rows.Should().HaveCount(3);
        rows.Should().OnlyContain(static x => x.Count == 13);

        rows[1][5].Should().Be(matches[0].Key.Display);
        rows[1][7].Should().Contain("authorization").And.Contain("visible");
        rows[1][10].Should().Contain("••• (redacted by $.customer.email)");
        rows[1][12].Should().StartWith("key; header:authorization; header:x-secret");
        rows[2][10].Should().BeEmpty("a withheld value is an empty cell");
    }

    [Fact]
    public void A_csv_has_a_byte_order_mark_and_crlf_and_a_json_lines_file_has_neither()
    {
        KafkaMatch match = Make(Json("v", "k"));

        KafkaExportFile csv = KafkaExportWriter.Write([match], KafkaExportFormat.Csv, 1 << 20);
        KafkaExportFile jsonl = KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1 << 20);

        csv.Content.Take(3).Should().Equal(0xEF, 0xBB, 0xBF);
        csv.Extension.Should().Be("csv");
        csv.ContentType.Should().Be("text/csv");

        string text = Utf8.GetString(csv.Content)[1..];
        text.Split("\r\n").Should().HaveCount(3, "a header row, one record and the empty tail after the last CRLF");
        text.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n").And.NotContain("\r");

        jsonl.Content.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1", "'+1")]
    [InlineData("-1+1", "'-1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tx", "'\tx")]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("a=b", "a=b")]
    public void A_cell_is_quoted_by_rfc_4180_and_a_formula_is_neutralised(string text, string expected) =>
        KafkaExportWriter.CsvCell(text).Should().Be(expected);

    [Fact]
    public void A_key_header_or_value_that_starts_a_formula_reaches_the_csv_neutralised_and_json_lines_is_never_altered()
    {
        KafkaMatch match = Make(Json("=cmd|' /C calc'!A0", "-2+3"));

        List<List<string>> rows = ParseCsv(Utf8.GetString(KafkaExportWriter.Write([match], KafkaExportFormat.Csv, 1 << 20).Content)[1..]);

        rows[1][5].Should().Be("'-2+3");
        rows[1][10].Should().Be("'=cmd|' /C calc'!A0");

        JsonElement row = Lines(KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1 << 20)).Single();
        row.GetProperty("key").GetString().Should().Be("-2+3");
        row.GetProperty("value").GetString().Should().Be("=cmd|' /C calc'!A0");
    }

    [Fact]
    public void A_non_ascii_text_survives_both_formats()
    {
        KafkaMatch match = Make(Json("hyvää yötä ✓ 日本語", "avain-ä"));

        Lines(KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1 << 20)).Single().GetProperty("value").GetString().Should().Be("hyvää yötä ✓ 日本語");
        ParseCsv(Utf8.GetString(KafkaExportWriter.Write([match], KafkaExportFormat.Csv, 1 << 20).Content)[1..])[1][10].Should().Be("hyvää yötä ✓ 日本語");
    }

    [Fact]
    public void Rows_come_in_the_order_topic_partition_offset_whatever_order_they_arrive_in()
    {
        WireRecord record = Json("v");
        KafkaMatch[] shuffled =
        [
            Make(At(record, "b", 0, 1)),
            Make(At(record, "a", 2, 5)),
            Make(At(record, "a", 1, 9)),
            Make(At(record, "a", 2, 4)),
            Make(At(record, "B", 0, 0)),
        ];

        JsonElement[] rows = Lines(KafkaExportWriter.Write(shuffled, KafkaExportFormat.JsonLines, 1 << 20));

        rows.Select(static x => $"{x.GetProperty("topic").GetString()}/{x.GetProperty("partition").GetInt32()}/{x.GetProperty("offset").GetInt64()}")
            .Should().Equal("B/0/0", "a/1/9", "a/2/4", "a/2/5", "b/0/1");
    }

    [Fact]
    public void Only_whole_rows_are_written_up_to_the_size_limit_and_the_rest_are_counted()
    {
        foreach (KafkaExportFormat format in new[] { KafkaExportFormat.JsonLines, KafkaExportFormat.Csv })
        {
            WholeRowsOnly(format);
        }
    }

    private static void WholeRowsOnly(KafkaExportFormat format)
    {
        KafkaMatch[] matches = [.. Enumerable.Range(0, 10).Select(static i => Make(At(Json("value-" + i), "orders", 0, 100 + i)))];
        KafkaExportFile all = KafkaExportWriter.Write(matches, format, 1 << 20);
        int header = format == KafkaExportFormat.Csv ? Utf8.GetByteCount(string.Join(",", ["topic", "partition", "offset", "timestamp", "timestamp_type", "key", "key_format", "headers", "value_format", "value_length", "value", "value_truncated", "redacted"]) + "\r\n") + 3 : 0;
        int row = (all.Content.Length - header) / 10;

        all.Written.Should().Be(10);
        all.LeftOut.Should().Be(0);

        KafkaExportFile limited = KafkaExportWriter.Write(matches, format, header + (3 * row) + (row / 2));

        limited.Written.Should().Be(3);
        limited.LeftOut.Should().Be(7, "the first row that would not fit, and every row after it");
        limited.Bytes.Should().BeLessThanOrEqualTo(header + (3 * row) + (row / 2));
        limited.Content.Should().Equal(all.Content.Take(header + (3 * row)), "a prefix of the whole file, ending on a row boundary");

        // The limit is exact: one byte short of three rows writes two, and the size of three rows writes three.
        KafkaExportWriter.Write(matches, format, header + (3 * row) - 1).Written.Should().Be(2);
        KafkaExportWriter.Write(matches, format, header + (3 * row)).Written.Should().Be(3);
    }

    [Fact]
    public void An_empty_source_is_an_empty_json_lines_file_and_a_csv_with_only_its_header()
    {
        KafkaExportWriter.Write([], KafkaExportFormat.JsonLines, 1 << 20).Content.Should().BeEmpty();
        ParseCsv(Utf8.GetString(KafkaExportWriter.Write([], KafkaExportFormat.Csv, 1 << 20).Content)[1..]).Should().ContainSingle();
    }

    [Fact]
    public void A_cloud_events_record_carries_its_mode_and_attributes_and_a_masked_attribute_is_listed()
    {
        string envelope = $$$"""{"specversion":"1.0","type":"order.placed","id":"1","subject":"{{{Secret}}}","data":{"n":1}}""";
        KafkaMatch match = Make(Json(envelope, "k", ("content-type", "application/cloudevents+json")), Rules(static r => r.CloudEvent("subject")));

        KafkaExportFile file = KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1 << 20);

        Utf8.GetString(file.Content).Should().NotContain(Secret);

        JsonElement row = Lines(file).Single();
        row.GetProperty("cloudEvent").GetProperty("mode").GetString().Should().Be(match.CloudEventMode.ToString());
        row.GetProperty("cloudEvent").GetProperty("attributes").EnumerateArray().Select(static x => x.GetProperty("name").GetString()).Should().Contain("type");
        row.GetProperty("redacted").EnumerateArray().Select(static x => x.GetString()).Should().Contain("ce:subject");
        row.GetProperty("valueIsCloudEventData").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void A_trace_context_and_omitted_headers_are_written_when_present()
    {
        const string traceparent = "00-4bf92f3577b34da6a3ce929d0e0e4736-00f067aa0ba902b7-01";
        KafkaMatch withTrace = Make(Json("v", "k", ("traceparent", traceparent)));
        (string, string?)[] many = [.. Enumerable.Range(0, 70).Select(static i => ("h" + i, (string?) "v"))];
        KafkaMatch crowded = Make(Json("v", "k", many));

        JsonElement trace = Lines(KafkaExportWriter.Write([withTrace], KafkaExportFormat.JsonLines, 1 << 20)).Single();
        trace.GetProperty("trace").GetProperty("traceId").GetString().Should().Be("4bf92f3577b34da6a3ce929d0e0e4736");
        trace.GetProperty("trace").GetProperty("spanId").GetString().Should().Be("00f067aa0ba902b7");

        JsonElement omitted = Lines(KafkaExportWriter.Write([crowded], KafkaExportFormat.JsonLines, 1 << 20)).Single();
        omitted.GetProperty("headers").GetArrayLength().Should().Be(64);
        omitted.GetProperty("headersOmitted").GetInt32().Should().Be(6);
    }

    // ---- the single value

    [Fact]
    public void A_json_value_is_saved_as_it_is_shown_with_masks_in_place()
    {
        KafkaMatch match = Make(Json(PayloadRedactionTests.Document), Fixture());

        KafkaExportFile file = KafkaExportWriter.Write([match], KafkaExportFormat.Value, 1 << 20);

        file.Refusal.Should().BeNull();
        file.Extension.Should().Be("json");
        file.ContentType.Should().Be("application/json");
        Utf8.GetString(file.Content).Should().Be(match.ValueText);
        Utf8.GetString(file.Content).Should().Contain("••• (redacted by $.customer.email)").And.NotContain("SENTINEL");
        file.Content.Take(3).Should().NotEqual([0xEF, 0xBB, 0xBF]);
        file.Written.Should().Be(1);
        file.ValueTruncated.Should().BeFalse();
    }

    [Fact]
    public void A_text_value_is_saved_as_text_and_a_cut_value_says_it_was_cut()
    {
        KafkaExportFile text = KafkaExportWriter.Write([Make(Json("hello"))], KafkaExportFormat.Value, 1 << 20);
        KafkaExportFile cut = KafkaExportWriter.Write([Make(Json(new string('x', 5_000)), limits: static l => l.MaxMatchBytes = 2_048)], KafkaExportFormat.Value, 1 << 20);

        text.Extension.Should().Be("txt");
        text.ContentType.Should().Be("text/plain");
        Utf8.GetString(text.Content).Should().Be("hello");
        cut.ValueTruncated.Should().BeTrue();
    }

    [Fact]
    public void A_value_with_no_text_is_refused_not_written_as_an_empty_file()
    {
        KafkaMatch binary = Make(Bytes([0x00, 0xFF, 0x10, 0x80]));
        KafkaMatch tombstone = Make(Json(null));
        KafkaMatch envelope = Make(Json("v"), grant: KafkaReadGrant.Envelope);
        KafkaMatch withheld = RedactionMatches()[1];

        foreach (KafkaMatch match in new[] { binary, tombstone, envelope, withheld })
        {
            KafkaExportFile file = KafkaExportWriter.Write([match], KafkaExportFormat.Value, 1 << 20);

            file.Refusal.Should().Be(KafkaExportWriter.NoValueText);
            file.Content.Should().BeEmpty();
            file.Written.Should().Be(0);
        }

        KafkaExportWriter.Write([], KafkaExportFormat.Value, 1 << 20).Refusal.Should().NotBeNull();
        KafkaExportWriter.Write([Make(Json("a")), Make(Json("b"))], KafkaExportFormat.Value, 1 << 20).Refusal.Should().NotBeNull("a value is saved from one record");
    }

    [Fact]
    public void A_value_larger_than_the_limit_is_left_out_and_counted()
    {
        KafkaExportFile file = KafkaExportWriter.Write([Make(Json(new string('x', 4_000)))], KafkaExportFormat.Value, 100);

        file.Written.Should().Be(0);
        file.LeftOut.Should().Be(1);
        file.Content.Should().BeEmpty();
        file.Refusal.Should().BeNull();
    }

    [Fact]
    public void A_record_without_a_timestamp_has_no_timestamp_to_write()
    {
        WireRecord record = new("t", 0, 0, [1], [2], [], 0, WireTimestampType.NotAvailable, 0);
        KafkaMatch match = Make(record);

        Lines(KafkaExportWriter.Write([match], KafkaExportFormat.JsonLines, 1 << 20)).Single().GetProperty("timestamp").ValueKind.Should().Be(JsonValueKind.Null);
        ParseCsv(Utf8.GetString(KafkaExportWriter.Write([match], KafkaExportFormat.Csv, 1 << 20).Content)[1..])[1][3].Should().BeEmpty();
    }

    // ---- the file name

    [Fact]
    public void A_file_name_holds_the_cluster_the_topics_and_the_time_and_nothing_a_visitor_typed()
    {
        DateTimeOffset now = new(2026, 11, 13, 8, 15, 2, TimeSpan.Zero);

        KafkaExportFileName.For("prod", KafkaExportFileName.TopicsPart(["orders.v1"]), now, "jsonl").Should().Be("kstudio-prod-orders.v1-20261113-081502Z.jsonl");
        KafkaExportFileName.For("prod", KafkaExportFileName.TopicsPart(["a", "b", "c"]), now, "csv").Should().Be("kstudio-prod-3-topics-20261113-081502Z.csv");
        KafkaExportFileName.For("prod", KafkaExportFileName.RecordPart("orders.v1", 3, 1200), now, "json").Should().Be("kstudio-prod-orders.v1-3-1200-20261113-081502Z.json");
    }

    [Fact]
    public void Every_character_outside_the_safe_set_becomes_an_underscore_and_the_name_is_cut_at_120()
    {
        DateTimeOffset now = new(2026, 11, 13, 8, 15, 2, TimeSpan.FromHours(2));

        string odd = KafkaExportFileName.For("prod/tenant a", "ordërs:v1 \\ ../x", now, "csv");

        odd.Should().Be("kstudio-prod_tenant_a-ord_rs_v1___.._x-20261113-061502Z.csv", "the time is UTC");
        odd.Should().MatchRegex("^[A-Za-z0-9._-]+$");

        string longName = KafkaExportFileName.For(new string('c', 100), new string('t', 500), now, "jsonl");

        longName.Length.Should().BeLessThanOrEqualTo(KafkaExportFileName.MaxLength);
        longName.Should().EndWith("-20261113-061502Z.jsonl", "the topic part is cut, never the time or the extension");
        longName.Should().StartWith("kstudio-" + new string('c', 40) + "-t");
    }
}
