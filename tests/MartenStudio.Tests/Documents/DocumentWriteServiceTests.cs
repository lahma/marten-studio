using System.Linq.Expressions;
using System.Text.Json;

using Marten;
using Marten.Services;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Support;

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

using Npgsql;

using NpgsqlTypes;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The write service without a database: the order its three gates run in, what it writes to the audit,
/// and the round-trip preview itself — which is a pure function over JSON, a CLR type and a serializer,
/// and therefore the one part of D7 that can be pinned down exactly.
/// </summary>
/// <remarks>
/// The store here is pointed at a host that does not resolve, which is deliberate: every test in this
/// file asserts something that must be decided <em>before</em> a connection is opened. A test that
/// accidentally reached the database would hang or throw rather than quietly pass, which is what makes
/// "no database work happened" an assertion rather than a hope.
/// </remarks>
public class DocumentWriteServiceTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-write-test.invalid;Database=none;Username=none;Password=none";

    private const string StorePolicy = "store-policy";

    private const string WritePolicy = "write-policy";

    private static readonly StudioScope Scope = new("default", string.Empty, null);

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private sealed record Harness(
        DocumentWriteService Service,
        TestStoreAuthorizationService Authorization,
        TestAuthenticationStateProvider Users,
        StudioActionLogService Ring,
        CapturingLoggerProvider Captured,
        MartenStudioOptions Options,
        ServiceProvider Provider) : IDisposable
    {
        public void Dispose() => Provider.Dispose();
    }

    private static Harness CreateHarness(Action<MartenStudioOptions>? configure = null)
    {
        var options = new MartenStudioOptions
        {
            StoreAuthorizationPolicy = StorePolicy,
            WriteAuthorizationPolicy = WritePolicy,
            Capabilities = MartenStudioCapabilities.All(),
        };
        configure?.Invoke(options);

        var wrapped = Options.Create(options);

        var services = new ServiceCollection();
        services.AddMarten(x => x.Connection(DummyConnectionString));

        var registry = new MartenStoreRegistry(services);
        var provider = services.BuildServiceProvider();

        var users = new TestAuthenticationStateProvider();
        var authorizationService = new TestStoreAuthorizationService();
        var authorization = new StudioAuthorization(wrapped, authorizationService, users);
        var tenants = new TenantDiscovery(wrapped, NullLogger<TenantDiscovery>.Instance);
        var resolver = new StudioScopeResolver(wrapped, registry, authorization, tenants, provider);

        var catalog = new StudioScopeCatalog(
            wrapped,
            registry,
            authorization,
            tenants,
            provider,
            NullLogger<StudioScopeCatalog>.Instance);
        var state = new StudioState(catalog, NullLogger<StudioState>.Instance, new HttpContextAccessor());

        var ring = new StudioActionLogService();
        var captured = new CapturingLoggerProvider();
        var factory = captured.CreateFactory();
        var audit = new StudioActionLog(ring, factory.CreateLogger<StudioActionLog>(), users, state);

        var service = new DocumentWriteService(
            new StudioCapabilityGuard(wrapped),
            resolver,
            audit,
            new ColumnCatalog(),
            wrapped,
            factory.CreateLogger<DocumentWriteService>(),
            users);

        return new Harness(service, authorizationService, users, ring, captured, options, provider);
    }

    // ---------------------------------------------------------------------------------------------
    // Gating order: capability, then policy, then anything at all.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The capability is a process-wide configuration answer and is asked first, so a studio that was
    /// never granted <c>EditDocuments</c> never runs an authorization handler, never builds a store and
    /// never opens a connection.
    /// </summary>
    [Fact]
    public async Task The_capability_is_refused_before_the_policy_is_asked()
    {
        using var harness = CreateHarness(static options => options.Capabilities = new MartenStudioCapabilities());
        harness.Users.SignIn("viewer");

        var denial = await harness.Service
            .Invoking(x => x.PreviewAsync(Scope, "customer", "42", "{}", Token))
            .Should().ThrowAsync<StudioCapabilityDeniedException>();

        denial.Which.Capability.Should().Be(StudioCapability.EditDocuments);
        denial.Which.Message.Should().Contain("Capabilities.EditDocuments");

        harness.Authorization.Calls.Should().BeEmpty("a capability that is off is answered before anybody is asked who is asking");
    }

    /// <summary>
    /// <see cref="MartenStudioOptions.ReadOnly" /> turns every capability off, and the refusal names the
    /// master switch rather than the individual property — that is the difference between a person
    /// finding the option and a person filing a bug.
    /// </summary>
    [Fact]
    public async Task ReadOnly_refuses_every_write_and_names_the_master_switch()
    {
        using var harness = CreateHarness(static options => options.ReadOnly = true);

        var denial = await harness.Service
            .Invoking(x => x.DeleteAsync(Scope, "customer", "42", Token))
            .Should().ThrowAsync<StudioCapabilityDeniedException>();

        denial.Which.Reason.Should().Be(CapabilityDenialReason.ReadOnly);
        denial.Which.Message.Should().Contain("ReadOnly");
    }

    /// <summary>
    /// With the capability on, the write policy decides — and it decides before the alias is looked up,
    /// so a visitor cannot learn which collections exist by watching which refusal comes back.
    /// </summary>
    [Fact]
    public async Task The_policy_is_asked_with_the_capability_named_and_refuses_before_the_lookup()
    {
        using var harness = CreateHarness();
        harness.Authorization.Allow(static resource => resource.Capability is null);

        await harness.Service
            .Invoking(x => x.PreviewAsync(Scope, "no-such-collection", "42", "{}", Token))
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        harness.Authorization.Calls.Should().HaveCount(2);
        harness.Authorization.Calls[0].Policy.Should().Be(StorePolicy);
        harness.Authorization.Calls[0].Resource.Capability.Should().BeNull();
        harness.Authorization.Calls[1].Policy.Should().Be(WritePolicy);
        harness.Authorization.Calls[1].Resource.Capability.Should().Be(nameof(StudioCapability.EditDocuments));
    }

    /// <summary>
    /// Both gates passed, so the third step runs: the alias is looked up, is not there, and the answer
    /// is a refusal a screen can render rather than an exception. Nothing was written and no connection
    /// was opened — this store's host does not resolve, so a test that reached the database could not
    /// have got here.
    /// </summary>
    [Fact]
    public async Task An_unknown_alias_is_refused_only_after_both_gates_pass()
    {
        using var harness = CreateHarness();

        var preview = await harness.Service.PreviewAsync(Scope, "no-such-collection", "42", "{}", Token);

        preview.Status.Should().Be(WritePreviewStatus.Refused);
        preview.Reason.Should().Contain("no-such-collection").And.Contain("read-only");
        harness.Authorization.Calls.Should().HaveCount(2, "both gates ran before the lookup did");
    }

    // ---------------------------------------------------------------------------------------------
    // Audit: every call, success or failure, with the pinned event ids.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_capability_refusal_is_audited_as_9202_and_lands_in_the_ring()
    {
        using var harness = CreateHarness(static options => options.Capabilities = new MartenStudioCapabilities());
        harness.Users.SignIn("viewer");

        await harness.Service
            .Invoking(x => x.DeleteAsync(Scope, "customer", "42", Token))
            .Should().ThrowAsync<StudioCapabilityDeniedException>();

        var entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.Action.Should().Be("DeleteDocument");
        entry.Target.Should().Be("customer/42");
        entry.Succeeded.Should().BeFalse();
        entry.Capability.Should().Be(nameof(StudioCapability.DeleteDocuments));

        harness.Captured.Entries.Select(x => x.EventId.Id).Should().Contain(9202);
    }

    [Fact]
    public async Task A_policy_refusal_is_audited_as_9203_and_names_the_scope_only_in_the_log()
    {
        using var harness = CreateHarness();
        harness.Authorization.DenyEverything();
        harness.Users.SignIn("viewer");

        var refusal = await harness.Service
            .Invoking(x => x.SaveAsync(Scope, "customer", "42", "{}", DocumentConcurrencyToken.None, false, Token))
            .Should().ThrowAsync<StudioNotAuthorizedException>();

        refusal.Which.Message.Should().NotContain("customer", "a refusal must not describe what it refused");

        harness.Ring.GetLatest().Should().ContainSingle()
            .Which.Action.Should().Be("EditDocument");

        var logged = harness.Captured.Entries.Where(x => x.EventId.Id == 9203).Should().ContainSingle().Which;
        logged.Level.Should().Be(LogLevel.Warning);
        logged.Message.Should().Contain(WritePolicy);
    }

    /// <summary>
    /// A refusal that is a value rather than an exception is still an attempt somebody made, so it is
    /// still audited — as 9201, the failure id.
    /// </summary>
    [Fact]
    public async Task A_refused_preview_is_audited_as_9201()
    {
        using var harness = CreateHarness();
        harness.Users.SignIn("ops");

        await harness.Service.PreviewAsync(Scope, "no-such-collection", "42", "{}", Token);

        var entry = harness.Ring.GetLatest().Should().ContainSingle().Which;
        entry.User.Should().Be("ops");
        entry.Action.Should().Be("PreviewDocumentEdit");
        entry.Succeeded.Should().BeFalse();

        harness.Captured.Entries.Select(x => x.EventId.Id).Should().Contain(9201);
    }

    // ---------------------------------------------------------------------------------------------
    // Bulk delete: the cap, and the empty case.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_bulk_delete_past_the_cap_is_refused_before_anything_is_read()
    {
        using var harness = CreateHarness();

        var ids = Enumerable.Range(0, DocumentWriteService.MaxBulkDeleteIds + 1)
            .Select(static x => x.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();

        var result = await harness.Service.BulkDeleteAsync(Scope, "customer", ids, Token);

        result.Accepted.Should().BeFalse();
        result.Reason.Should().Contain("501").And.Contain("500");
        result.Results.Should().BeEmpty();

        harness.Ring.GetLatest().Should().ContainSingle()
            .Which.Action.Should().Be("BulkDeleteDocuments");
    }

    [Fact]
    public async Task A_bulk_delete_of_nothing_is_accepted_and_does_nothing()
    {
        using var harness = CreateHarness();

        var result = await harness.Service.BulkDeleteAsync(Scope, "customer", [], Token);

        result.Accepted.Should().BeTrue();
        result.Results.Should().BeEmpty();
        result.DeletedCount.Should().Be(0);
    }

    // ---------------------------------------------------------------------------------------------
    // The round trip itself: what a save would do, as a pure function.
    // ---------------------------------------------------------------------------------------------

    private static readonly ISerializer Serializer = new SystemTextJsonSerializer();

    private const string StoredCustomer =
        """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Ada","Address":{"City":"Helsinki"}}""";

    private static DocumentWriteService.PreviewResult Preview(
        string editedJson,
        Type? documentType = null,
        string? storedJson = null,
        bool hasConcurrencyColumn = true) =>
        DocumentWriteService.BuildPreview(
            "writetestcustomer",
            "11111111-1111-1111-1111-111111111111",
            documentType ?? typeof(WriteTestCustomer),
            Serializer,
            storedJson ?? StoredCustomer,
            editedJson,
            DocumentConcurrencyToken.ForVersion(Guid.Parse("22222222-2222-2222-2222-222222222222")),
            hasConcurrencyColumn);

    /// <summary>
    /// The whole of D7 in one assertion: a property the CLR type has no member for is in the
    /// <c>Dropped</c> bucket, by JSONPath, <em>before</em> anything is written.
    /// </summary>
    [Fact]
    public void A_property_the_type_does_not_have_is_reported_as_dropped()
    {
        var edited =
            """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Ada","Address":{"City":"Helsinki"},"legacyField":1}""";

        var result = Preview(edited);

        result.Preview.Status.Should().Be(WritePreviewStatus.Ready);
        result.Preview.TypeIsConstructible.Should().BeTrue();
        result.Preview.HasDrops.Should().BeTrue();
        result.Preview.DroppedPaths.Should().ContainSingle().Which.Should().Be("$.legacyField");
        result.Preview.RoundTripDiff.Dropped.Should().ContainSingle().Which.Before.Should().Be("1");
        result.Document.Should().BeOfType<WriteTestCustomer>();
        result.Preview.Summary().Should().Contain("$.legacyField");
    }

    /// <summary>A nested property is reported at its own path, not as the object that contains it.</summary>
    [Fact]
    public void A_dropped_nested_property_keeps_its_path()
    {
        var edited =
            """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Ada","Address":{"City":"Helsinki","Zip":"00100"}}""";

        var result = Preview(edited);

        result.Preview.DroppedPaths.Should().ContainSingle().Which.Should().Be("$.Address.Zip");
    }

    /// <summary>
    /// The two diffs answer two different questions, and an ordinary edit shows it: nothing is dropped,
    /// and the change the person made is in the other bucket.
    /// </summary>
    [Fact]
    public void An_edit_that_the_type_can_carry_drops_nothing_and_is_reported_as_a_change()
    {
        var edited =
            """{"Id":"11111111-1111-1111-1111-111111111111","Name":"Grace","Address":{"City":"Helsinki"}}""";

        var result = Preview(edited);

        result.Preview.Status.Should().Be(WritePreviewStatus.Ready);
        result.Preview.HasDrops.Should().BeFalse();
        result.Preview.RoundTripDiff.IsEmpty.Should().BeTrue();

        var change = result.Preview.EditDiff.Changed.Should().ContainSingle().Which;
        change.Path.Should().Be("$.Name");
        change.Before.Should().Be("\"Ada\"");
        change.After.Should().Be("\"Grace\"");
    }

    /// <summary>
    /// A member the JSON does not mention comes back with the type's default, which is an
    /// <em>addition</em> and not a loss — and the dialog has to be able to tell the two apart.
    /// </summary>
    [Fact]
    public void A_member_the_edit_omits_comes_back_from_the_type_as_an_addition()
    {
        var edited = """{"Id":"11111111-1111-1111-1111-111111111111","Address":{"City":"Helsinki"}}""";

        var result = Preview(edited);

        result.Preview.HasDrops.Should().BeFalse();
        result.Preview.RoundTripDiff.Added.Select(x => x.Path).Should().Contain("$.Name");
    }

    [Fact]
    public void Malformed_json_is_refused_with_the_line_and_the_column()
    {
        var result = Preview("{\n  \"Name\": \n}");

        result.Preview.Status.Should().Be(WritePreviewStatus.Refused);
        result.Preview.TypeIsConstructible.Should().BeTrue("the JSON never got as far as the type");
        result.Preview.Reason.Should().Contain("line").And.Contain("column");
        result.Document.Should().BeNull();
    }

    /// <summary>
    /// A type the serializer cannot construct is a refusal with a message, never an unhandled exception:
    /// the studio renders whatever types the host mapped, and some of them are abstract.
    /// </summary>
    [Fact]
    public void A_type_that_cannot_be_constructed_is_refused_rather_than_thrown()
    {
        var result = Preview("""{"Id":"11111111-1111-1111-1111-111111111111"}""", typeof(WriteTestAbstractDocument));

        result.Preview.Status.Should().Be(WritePreviewStatus.Refused);
        result.Preview.TypeIsConstructible.Should().BeFalse();
        result.Preview.Reason.Should().Contain(nameof(WriteTestAbstractDocument));
        result.Document.Should().BeNull();
    }

    /// <summary>
    /// A collection with no version column can still be edited, and the preview says out loud that the
    /// save cannot be guarded — because a check that silently did not happen is the worst of both.
    /// </summary>
    [Fact]
    public void A_collection_with_no_version_column_is_ready_with_a_caveat()
    {
        var result = Preview(StoredCustomer, hasConcurrencyColumn: false);

        result.Preview.Status.Should().Be(WritePreviewStatus.Ready);
        result.Preview.Reason.Should().Contain("no version column");
    }

    // ---------------------------------------------------------------------------------------------
    // The token.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void A_version_token_survives_the_round_trip_through_a_query_string()
    {
        var token = DocumentConcurrencyToken.ForVersion(Guid.NewGuid());

        DocumentConcurrencyToken.TryParse(token.ToTokenString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(token);
    }

    [Fact]
    public void A_revision_token_survives_the_round_trip_through_a_query_string()
    {
        var token = DocumentConcurrencyToken.ForRevision(17);

        DocumentConcurrencyToken.TryParse(token.ToTokenString(), out var parsed).Should().BeTrue();
        parsed.Should().Be(token);
        parsed.Should().NotBe(DocumentConcurrencyToken.ForRevision(18));
    }

    [Fact]
    public void An_unknown_token_is_None_rather_than_a_guess()
    {
        DocumentConcurrencyToken.TryParse("not-a-version", out var parsed).Should().BeFalse();
        parsed.IsKnown.Should().BeFalse();
        parsed.Describe().Should().Be("(none)");

        DocumentConcurrencyToken.FromColumnValue("something").Should().Be(DocumentConcurrencyToken.None);
        DocumentConcurrencyToken.FromColumnValue(7).Should().Be(DocumentConcurrencyToken.ForRevision(7));
    }

    // ---------------------------------------------------------------------------------------------
    // What reaches a log line. Every string in a write message came from a URL or from a document.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// A property name with a newline in it would end the log line and start one that looks like a
    /// second event — the cheapest way there is to make an audit trail lie.
    /// </summary>
    [Fact]
    public void A_dropped_property_whose_name_contains_a_newline_cannot_break_the_log_line()
    {
        var edited =
            """
            {"Id":"11111111-1111-1111-1111-111111111111","Name":"Ada","Address":{"City":"Helsinki"},"a\nb":1}
            """;

        var result = Preview(edited);

        result.Preview.HasDrops.Should().BeTrue();
        result.Preview.DroppedPaths.Should().ContainSingle()
            .Which.Should().Contain("\n", "the path itself keeps the name the document really has");

        var summary = result.Preview.DroppedPathsSummary();

        summary.Should().NotContain("\n").And.NotContain("\r");
        summary.Should().Contain("a b", "the newline is folded to a space rather than dropped silently");

        result.Preview.Summary().Should().NotContain("\n").And.NotContain("\r");
    }

    /// <summary>
    /// A serializer's own message can be any length and can quote a fragment of somebody's document, so
    /// it stays on the screen. The audit gets the exception type and the JSON path instead.
    /// </summary>
    [Fact]
    public void A_deserializer_failure_keeps_its_message_on_screen_and_out_of_the_audit()
    {
        var result = Preview("[]");

        result.Preview.Status.Should().Be(WritePreviewStatus.Refused);
        result.Preview.TypeIsConstructible.Should().BeFalse();

        var reason = result.Preview.Reason.Should().NotBeNull().And.Subject!;
        var audit = result.Preview.AuditReason.Should().NotBeNull().And.Subject!;

        audit.Should().Contain(nameof(WriteTestCustomer), "the audit still says which type could not be built")
            .And.Contain(nameof(JsonException), "and what went wrong, by type");

        // Everything after the last colon is the serializer's own words about the document. They are what
        // a person fixing the edit needs, and they are exactly what must not reach a log file.
        var serializerWords = reason[(reason.LastIndexOf(": ", StringComparison.Ordinal) + 2)..];

        serializerWords.Should().NotBeEmpty();
        audit.Should().NotContain(serializerWords);
        result.Preview.Summary().Should().Be(audit);

        WriteResult.Refused(reason, result.Preview).AuditMessage().Should()
            .NotContain(serializerWords, "the refusal the save returns carries the same split");
    }

    [Theory]
    [InlineData("a\nb", "a b")]
    [InlineData("a\r\nb", "a b")]
    [InlineData("a\tb", "a b")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("  ", "")]
    public void Sanitize_folds_every_control_character_into_one_space(string value, string expected) =>
        WriteAuditText.Sanitize(value).Should().Be(expected);

    [Fact]
    public void Sanitize_folds_the_invisible_characters_a_log_reader_cannot_see()
    {
        // U+2028 LINE SEPARATOR, U+200E LEFT-TO-RIGHT MARK, U+202E RIGHT-TO-LEFT OVERRIDE: one ends the
        // line for several log shippers and the other two change how the rest of it reads.
        WriteAuditText.Sanitize("a" + (char) 0x2028 + "b").Should().Be("a b");
        WriteAuditText.Sanitize("a" + (char) 0x200E + "b").Should().Be("a b");
        WriteAuditText.Sanitize("a" + (char) 0x202E + "b").Should().Be("a b");
    }

    [Fact]
    public void Sanitize_caps_the_line_and_says_that_it_did()
    {
        var sanitized = WriteAuditText.Sanitize(new string('x', 5_000));

        sanitized.Should().HaveLength(WriteAuditText.MaxLength + 1);
        sanitized.Should().EndWith("…");
    }

    /// <summary>A cut must never land between the halves of a surrogate pair.</summary>
    [Fact]
    public void Sanitize_never_cuts_a_character_in_half()
    {
        var sanitized = WriteAuditText.Sanitize(string.Concat(Enumerable.Repeat("🙂", 40)), 11);

        sanitized.Should().NotBeEmpty();
        char.IsHighSurrogate(sanitized[^2]).Should().BeFalse("the last kept character is a whole one");
        sanitized.EnumerateRunes().Should().NotContain(static rune => rune.Value == 0xFFFD);
    }

    /// <summary>
    /// The audit's target is built from the alias and the id, both of which arrive from a URL.
    /// </summary>
    [Fact]
    public async Task An_id_with_a_newline_in_it_cannot_write_a_second_audit_line()
    {
        using var harness = CreateHarness();

        await harness.Service.PreviewAsync(Scope, "no-such-collection", "42\nDELETED everything", "{}", Token);

        var entry = harness.Ring.GetLatest().Should().ContainSingle().Which;

        entry.Target.Should().Be("no-such-collection/42 DELETED everything");
        entry.Message.Should().NotContain("\n");
    }

    // ---------------------------------------------------------------------------------------------
    // The Marten calls the write path makes, pinned here because they are this file's subject.
    // MartenApiSurfaceTest owns the rest of the contract.
    // ---------------------------------------------------------------------------------------------

    /// <summary>
    /// The four generic writes the studio reaches through a compiled expression, and the one overload
    /// resolution that is genuinely ambiguous to a reflection lookup.
    /// </summary>
    /// <remarks>
    /// <c>Delete&lt;T&gt;(object id)</c> and <c>Delete&lt;T&gt;(T entity)</c> are both one-argument
    /// generic methods called <c>Delete</c>; only the first has a parameter whose type really is
    /// <see cref="object" />, and picking the wrong one would delete nothing and throw an invalid cast at
    /// the first id. <c>UndoDeleteWhere&lt;T&gt;</c> is what makes an undelete a two-column update
    /// instead of a full rewrite of the document.
    /// </remarks>
    [Fact]
    public void IDocumentOperations_has_the_generic_writes_the_studio_compiles_against()
    {
        var operations = typeof(IDocumentOperations);

        var deletes = operations.GetMethods()
            .Where(static x => x.Name == nameof(IDocumentOperations.Delete) && x.IsGenericMethodDefinition)
            .ToArray();

        deletes.Should().Contain(static x => x.GetParameters().Length == 1 &&
            x.GetParameters()[0].ParameterType == typeof(object));
        deletes.Count(static x => x.GetParameters().Length == 1 &&
            x.GetParameters()[0].ParameterType == typeof(object)).Should().Be(1);

        var undoDelete = operations.GetMethod(nameof(IDocumentOperations.UndoDeleteWhere));
        undoDelete.Should().NotBeNull();
        undoDelete!.IsGenericMethodDefinition.Should().BeTrue();
        undoDelete.GetParameters().Should().ContainSingle().Which.ParameterType.GetGenericTypeDefinition()
            .Should().Be(typeof(Expression<>));

        operations.GetMethod(nameof(IDocumentOperations.UpdateExpectedVersion)).Should().NotBeNull();
        operations.GetMethod(nameof(IDocumentOperations.UpdateRevision)).Should().NotBeNull();
        typeof(IQuerySession).GetProperty(nameof(IQuerySession.Connection))!.PropertyType
            .Should().Be<NpgsqlConnection>();
    }

    // ---------------------------------------------------------------------------------------------
    // The two statements the bulk delete and the row lock rest on.
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public void The_many_read_is_one_statement_with_one_typed_array_parameter()
    {
        var table = CustomerTable();
        var ids = new[] { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() };

        DocumentQueryBuilder.TryBuildMany(table, ids, null, out var command, out var errors).Should().BeTrue();

        using (command)
        {
            errors.Should().OnlyContain(static x => x == null);

            command!.CommandText.Should().Contain("= any(@ids)");
            command.CommandText.Should().NotContain("\"data\"", "a bulk delete does not need the documents");

            var parameter = command.Parameters.Should().ContainSingle().Which;
            parameter.ParameterName.Should().Be("ids");
            parameter.NpgsqlDbType.Should().Be(NpgsqlDbType.Array | NpgsqlDbType.Uuid);
            parameter.Value.Should().BeOfType<Guid[]>().Which.Should().HaveCount(2);
        }
    }

    [Fact]
    public void The_many_read_reports_the_ids_it_could_not_use_and_still_reads_the_rest()
    {
        var table = CustomerTable();
        var good = Guid.NewGuid().ToString();

        DocumentQueryBuilder.TryBuildMany(table, ["not-a-guid", good], null, out var command, out var errors)
            .Should().BeTrue();

        using (command)
        {
            errors[0].Should().Contain("uuid");
            errors[1].Should().BeNull();
            command!.Parameters["ids"].Value.Should().BeOfType<Guid[]>().Which.Should().ContainSingle();
        }
    }

    [Fact]
    public void The_many_read_is_not_built_at_all_when_no_id_is_usable()
    {
        DocumentQueryBuilder.TryBuildMany(CustomerTable(), ["nope", "also-nope"], null, out var command, out var errors)
            .Should().BeFalse();

        command.Should().BeNull();
        errors.Should().HaveCount(2).And.OnlyContain(static x => x != null);
    }

    /// <summary>
    /// The lock timeout is a parameter and not a literal, which is the whole rule this file's SQL lives
    /// by (AGENTS.md hard rule 4) — <c>SET LOCAL</c> cannot take one, which is why it is
    /// <c>set_config(…, true)</c> instead.
    /// </summary>
    [Fact]
    public void The_lock_timeout_is_transaction_local_and_parameterised()
    {
        using var command = DocumentQueryBuilder.BuildLockTimeout(TimeSpan.FromSeconds(2.5));

        command.CommandText.Should().Be("select set_config('lock_timeout', @lockTimeout, true)");
        command.Parameters.Should().ContainSingle().Which.Value.Should().Be("2500");
    }

    private static DocumentTableInfo CustomerTable() => new()
    {
        Schema = "public",
        Table = "mt_doc_customer",
        Alias = "customer",
        IdColumnType = DocumentIdColumnType.Uuid,
    };

}

/// <summary>A document with one nested value object, which is all the round-trip tests need.</summary>
public sealed class WriteTestCustomer
{
    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public WriteTestAddress? Address { get; set; }
}

/// <summary>The nested value object.</summary>
/// <param name="City">Its one member, so a sibling in the JSON is a dropped nested property.</param>
public sealed record WriteTestAddress(string City);

/// <summary>A type no serializer can construct, which the studio has to refuse rather than throw about.</summary>
public abstract class WriteTestAbstractDocument
{
    public Guid Id { get; set; }
}
