using System.Reflection;

using Marten;

using MartenStudio.Services.Documents;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// M5: an edit has to keep its document's id - the comparison itself, with no database. The live proof that a changed id
/// used to overwrite another document is <c>DocumentIdChangeLiveTests</c>.
/// </summary>
public class DocumentIdChangeTests
{
    private const string DummyConnectionString =
        "Host=marten-studio-id-change-test.invalid;Database=none;Username=none;Password=none";

    private static readonly Guid Own = Guid.Parse("0b5c8a3e-6a1f-4d2e-9c7b-1f2e3d4c5b6a");
    private static readonly Guid Other = Guid.Parse("7d9e1f20-3a4b-4c5d-8e6f-a1b2c3d4e5f6");

    private static readonly MemberInfo NoteId = typeof(Note).GetProperty(nameof(Note.Id))!;

    [Fact]
    public void An_edit_that_keeps_the_id_passes()
    {
        using DocumentStore store = Store();

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Note), new(NoteId, Own), Json(Own), new Note { Id = Own })
            .Should().BeNull();
    }

    [Fact]
    public void An_edit_that_changes_the_id_is_refused_naming_both()
    {
        using DocumentStore store = Store();

        string? refusal = DocumentWriteService.IdChange(
            store.Options.Serializer(), typeof(Note), new(NoteId, Own), Json(Own), new Note { Id = Other });

        refusal.Should().StartWith($"The edit changes the document's id from '{Own}' to '{Other}'.")
            .And.Contain("Change the id back");
    }

    /// <summary>A strong-typed id compares as the value it is, not as its text.</summary>
    [Fact]
    public void A_strong_typed_id_compares_as_a_value()
    {
        using DocumentStore store = Store();
        MemberInfo member = typeof(Order).GetProperty(nameof(Order.Id))!;

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Order), new(member, new OrderId(Own)), "{}", new Order { Id = new OrderId(Own) })
            .Should().BeNull();

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Order), new(member, new OrderId(Own)), "{}", new Order { Id = new OrderId(Other) })
            .Should().NotBeNull();
    }

    /// <summary>
    /// Where the column's id cannot be built as the member's type, the stored document's own id stands in for it - read
    /// through the same serializer, so an id type with no equality of its own still compares by what it writes.
    /// </summary>
    [Fact]
    public void Without_a_column_id_the_stored_documents_own_id_stands_in()
    {
        using DocumentStore store = Store();
        MemberInfo member = typeof(Keyed).GetProperty(nameof(Keyed.Id))!;
        string stored = store.Options.Serializer().ToJson(new Keyed { Id = new Key { Value = "orders/1" } });

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Keyed), new(member, null), stored, new Keyed { Id = new Key { Value = "orders/1" } })
            .Should().BeNull("two Key instances with one value write the same JSON, though Key has no equality of its own");

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Keyed), new(member, null), stored, new Keyed { Id = new Key { Value = "orders/2" } })
            .Should().NotBeNull();
    }

    /// <summary>With neither a column id nor a readable stored document there is nothing to tell by, and nothing is refused.</summary>
    [Fact]
    public void With_nothing_to_tell_by_nothing_is_refused()
    {
        using DocumentStore store = Store();
        MemberInfo member = typeof(Keyed).GetProperty(nameof(Keyed.Id))!;

        DocumentWriteService.IdChange(
                store.Options.Serializer(), typeof(Keyed), new(member, null), "not json", new Keyed { Id = new Key { Value = "x" } })
            .Should().BeNull();
    }

    /// <summary>The preview refuses it as an edit a person can fix, and the audit gets a sentence with no id in it.</summary>
    [Fact]
    public void The_preview_refuses_it_and_the_audit_names_no_id()
    {
        using DocumentStore store = Store();

        DocumentWriteService.PreviewResult result = DocumentWriteService.BuildPreview(
            "note",
            Own.ToString(),
            typeof(Note),
            store.Options.Serializer(),
            Json(Own),
            Json(Other),
            DocumentConcurrencyToken.ForVersion(Guid.NewGuid()),
            hasConcurrencyColumn: true,
            new DocumentWriteService.DocumentIdentity(NoteId, Own));

        result.Document.Should().BeNull("a refused edit hands nothing on to be saved");
        result.Preview.Status.Should().Be(WritePreviewStatus.Refused);
        result.Preview.TypeIsConstructible.Should().BeTrue();
        result.Preview.Reason.Should().Contain(Other.ToString());
        result.Preview.AuditReason.Should().Be(DocumentWriteService.IdChangedAuditReason);
        result.Preview.Summary().Should().NotContain(Other.ToString());
    }

    private static string Json(Guid id) => $$"""{"Id":"{{id}}","Text":"t"}""";

    private static DocumentStore Store() => DocumentStore.For(options =>
    {
        options.Connection(DummyConnectionString);
        options.Schema.For<Note>();
    });

    /// <summary>A Guid-keyed document.</summary>
    public sealed class Note
    {
        /// <summary>The identity.</summary>
        public Guid Id { get; set; }

        /// <summary>Something to edit.</summary>
        public string Text { get; set; } = string.Empty;
    }

    /// <summary>A strong-typed id.</summary>
    /// <param name="Value">The Guid inside it.</param>
    public readonly record struct OrderId(Guid Value);

    /// <summary>A document keyed by a strong-typed id.</summary>
    public sealed class Order
    {
        /// <summary>The identity.</summary>
        public OrderId Id { get; set; }
    }

    /// <summary>An id class with no equality of its own.</summary>
    public sealed class Key
    {
        /// <summary>The value inside it.</summary>
        public string Value { get; set; } = string.Empty;
    }

    /// <summary>A document keyed by <see cref="Key" />.</summary>
    public sealed class Keyed
    {
        /// <summary>The identity.</summary>
        public Key Id { get; set; } = new();
    }
}
