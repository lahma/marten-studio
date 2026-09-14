using System.Text.Json.Nodes;

using Marten;

using MartenStudio.Services.Documents;

using Weasel.Core;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// A store whose serializer is not configured the way Marten's default is.
/// </summary>
/// <remarks>
/// <para>
/// This is AGENTS.md hard rule 10 held to a database. The round trip has to go through
/// <c>store.Options.Serializer()</c>; a studio that reached for a <c>JsonSerializer</c> of its own would
/// read <c>displayName</c> out of the stored JSON, fail to bind it to <c>DisplayName</c>, and report a
/// document nobody had touched as losing every property it has and gaining a Pascal-cased copy of each.
/// The dialog would be a wall of data loss that is not happening, which is worse than no dialog: the
/// second time somebody sees it they stop reading it.
/// </para>
/// <para>
/// <c>EnumStorage.AsString</c> is in here for the same reason from the other side — an enum written as
/// <c>"Running"</c> and read back through integer storage is a deserialization failure, not a diff.
/// </para>
/// </remarks>
public class DocumentWriteSerializerLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string GadgetTable = "mt_doc_gadget";
    private const string GadgetAlias = "gadget";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    /// <inheritdoc />
    protected override bool SeedSampleData => false;

    /// <inheritdoc />
    protected override void ConfigureExtras(StoreOptions opts)
    {
        ArgumentNullException.ThrowIfNull(opts);

        opts.UseSystemTextJsonForSerialization(EnumStorage.AsString, Casing.CamelCase);
        opts.Schema.For<Gadget>();
    }

    /// <summary>
    /// The document really is stored camel-cased with a named enum, so the rest of this class is about
    /// something.
    /// </summary>
    [PostgresFact]
    public async Task The_store_writes_camel_case_and_named_enums()
    {
        var id = Guid.NewGuid();
        await StoreAsync(new Gadget
        {
            Id = id,
            DisplayName = "Left flux capacitor",
            State = GadgetState.Running,
            Part = new GadgetPart("FC-1", 2),
        });

        var stored = JsonNode.Parse((await StoredJsonAsync(GadgetTable, id))!)!.AsObject();

        stored.ContainsKey("displayName").Should().BeTrue();
        stored.ContainsKey("DisplayName").Should().BeFalse();
        stored["state"]!.GetValue<string>().Should().Be("Running");
        stored["part"]!["partName"]!.GetValue<string>().Should().Be("FC-1");
    }

    /// <summary>
    /// Previewing a document nobody edited reports no drops, no changes and no additions — which is only
    /// true if the round trip used the store's own serializer settings.
    /// </summary>
    [PostgresFact]
    public async Task A_preview_of_an_unchanged_document_reports_nothing_at_all()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Gadget
        {
            Id = id,
            DisplayName = "Right flux capacitor",
            State = GadgetState.Idle,
            Part = new GadgetPart("FC-2", 4),
        });

        var stored = await StoredJsonAsync(GadgetTable, id);

        var preview = await service.PreviewAsync(ScopeFor(), GadgetAlias, id.ToString(), stored!, Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.DocumentTypeName.Should().Be(nameof(Gadget));
        preview.HasDrops.Should().BeFalse();
        preview.RoundTripDiff.IsEmpty.Should().BeTrue("the round trip honoured the store's casing and enum storage");
        preview.EditDiff.IsEmpty.Should().BeTrue("nothing was edited");
        preview.Summary().Should().Be("Nothing will change.");
        preview.Reason.Should().BeNull("the collection has a version column and the row has a version in it");
    }

    /// <summary>An edit to a camel-cased document saves as itself, enum and nested object included.</summary>
    [PostgresFact]
    public async Task An_edit_to_a_camel_cased_document_saves_as_itself()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Gadget
        {
            Id = id,
            DisplayName = "Spare",
            State = GadgetState.Idle,
            Part = new GadgetPart("FC-3", 1),
        });

        var stored = await StoredJsonAsync(GadgetTable, id);
        var edited = JsonNode.Parse(stored!)!.AsObject();
        edited["state"] = "Running";
        edited["part"]!["count"] = 7;

        var preview = await service.PreviewAsync(
            ScopeFor(), GadgetAlias, id.ToString(), edited.ToJsonString(), Token);

        preview.Status.Should().Be(WritePreviewStatus.Ready);
        preview.HasDrops.Should().BeFalse();
        preview.EditDiff.Changed.Select(x => x.Path).Should().Contain("$.state");

        var saved = await service.SaveAsync(
            ScopeFor(), GadgetAlias, id.ToString(), edited.ToJsonString(), preview.CurrentToken,
            acknowledgeDrops: false, Token);

        saved.Status.Should().Be(WriteStatus.Saved);

        var after = JsonNode.Parse((await StoredJsonAsync(GadgetTable, id))!)!;
        after["state"]!.GetValue<string>().Should().Be("Running");
        after["part"]!["count"]!.GetValue<int>().Should().Be(7);
        after["displayName"]!.GetValue<string>().Should().Be("Spare");
    }

    /// <summary>
    /// A property this type has no member for is still reported as dropped when the store is camel-cased
    /// — the differ is comparing what the serializer produced, not what a Pascal-cased guess would.
    /// </summary>
    [PostgresFact]
    public async Task A_property_the_camel_cased_type_cannot_carry_is_still_reported_as_dropped()
    {
        var service = CreateService();

        var id = Guid.NewGuid();
        await StoreAsync(new Gadget { Id = id, DisplayName = "Odd one", State = GadgetState.Idle });

        var stored = await StoredJsonAsync(GadgetTable, id);
        var edited = JsonNode.Parse(stored!)!.AsObject();
        edited["legacyTag"] = "old";

        var preview = await service.PreviewAsync(
            ScopeFor(), GadgetAlias, id.ToString(), edited.ToJsonString(), Token);

        preview.DroppedPaths.Should().ContainSingle().Which.Should().Be("$.legacyTag");
    }
}
