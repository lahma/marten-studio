using Marten;
using Marten.Schema;

using MartenStudio.Services.Documents;

using PostgresStudio.Services;

namespace MartenStudio.Integration.Tests.Documents;

/// <summary>
/// M5: Undelete, gated on <c>DeleteDocuments</c> - through the studio's own lossless <c>UndoDeleteWhere</c> on a
/// database-pinned session, at every Marten, and never Marten 9.52's <c>UndeleteDocumentAsync</c>, which re-stores the
/// row through its CLR type on an unpinned session.
/// </summary>
/// <remarks>
/// <para>
/// Everything goes through the studio's write service with the real guard, resolver, policy calls and audit, as the rest
/// of this folder does: restored without its JSON rewritten, audited, refused without <c>DeleteDocuments</c>, under
/// <c>ReadOnly</c> and by the write policy asked for <c>DeleteDocuments</c>, and on a conjoined type only the scoped
/// tenant's row.
/// </para>
/// <para>
/// The two types are this class's own, soft-deleted, one single-tenanted and one conjoined; the arrangements delete them
/// through Marten itself, as a host would.
/// </para>
/// </remarks>
public class DocumentUndeleteLiveTests(PostgresFixture postgres) : DocumentWriteFixture(postgres)
{
    private const string MemoAlias = "undelete_memo";
    private const string MemoTable = "mt_doc_undelete_memo";
    private const string TenantMemoAlias = "undelete_tenant_memo";
    private const string TenantMemoTable = "mt_doc_undelete_tenant_memo";

    private static readonly string[] Tenants = ["acme", "globex"];

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    protected override bool SeedSampleData => false;

    protected override void ConfigureExtras(StoreOptions opts)
    {
        opts.Schema.For<UndeleteMemo>().SoftDeleted();
        opts.Schema.For<UndeleteTenantMemo>().MultiTenanted().SoftDeleted();
    }

    [PostgresFact]
    public async Task Undelete_restores_the_row_without_rewriting_its_JSON_and_is_audited_under_DeleteDocuments()
    {
        Users.SignIn("ops");
        Guid id = await DeletedMemoAsync("restore me");
        string? before = await StoredJsonAsync(MemoTable, id);

        DeleteResult result = await CreateService().UndeleteAsync(ScopeFor(), MemoAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await IsDeletedAsync(MemoTable, id)).Should().BeFalse();
        (await ScalarAsync($"select mt_deleted_at from \"{Schema}\".{MemoTable} where id = '{id}'::uuid"))
            .Should().BeNull("UndoDeleteWhere clears the deleted-at stamp with the flag");
        (await StoredJsonAsync(MemoTable, id)).Should().Be(before, "an undelete clears a flag; it never rewrites data (D7)");

        StudioActionLogEntry entry = Ring.GetLatest().Should()
            .ContainSingle(x => x.Action == "UndeleteDocument").Subject;
        entry.Succeeded.Should().BeTrue();
        entry.Capability.Should().Be(nameof(StudioCapability.DeleteDocuments));
        entry.User.Should().Be("ops");

        Authorization.Calls.Should().Contain(
            x => x.Policy == WritePolicyName && x.Resource.Capability == nameof(StudioCapability.DeleteDocuments),
            "undelete is the other half of delete, and the write policy is asked for it as such");
    }

    /// <summary>
    /// Without <c>DeleteDocuments</c> there is no undelete, whatever else is on: the refusal names the option, is audited,
    /// asks no policy, and the row stays deleted.
    /// </summary>
    [PostgresFact]
    public async Task Undelete_is_refused_without_DeleteDocuments_even_with_EditDocuments_on()
    {
        Users.SignIn("ops");
        StudioOptions.Capabilities = new MartenStudioCapabilities { EditDocuments = true };

        try
        {
            Guid id = await DeletedMemoAsync("stays deleted");

            StudioCapabilityDeniedException denied = (await CreateService()
                    .Invoking(x => x.UndeleteAsync(ScopeFor(), MemoAlias, id.ToString(), Token))
                    .Should().ThrowAsync<StudioCapabilityDeniedException>())
                .Which;

            denied.Capability.Should().Be(StudioCapability.DeleteDocuments);
            denied.CapabilityOption.Should().Be("MartenStudioOptions.Capabilities.DeleteDocuments");

            (await IsDeletedAsync(MemoTable, id)).Should().BeTrue();
            Logs.EventIds.Should().Contain(9202);
            Ring.GetLatest().Should().Contain(x => x.Action == "UndeleteDocument" && !x.Succeeded);
            Authorization.Calls.Should().NotContain(x => x.Resource.Capability == nameof(StudioCapability.DeleteDocuments),
                "a capability that is off is answered before any policy is asked");
        }
        finally
        {
            StudioOptions.Capabilities = MartenStudioCapabilities.All();
        }
    }

    [PostgresFact]
    public async Task Undelete_is_refused_under_ReadOnly()
    {
        Users.SignIn("ops");
        StudioOptions.ReadOnly = true;

        try
        {
            Guid id = await DeletedMemoAsync("read-only");

            StudioCapabilityDeniedException denied = (await CreateService()
                    .Invoking(x => x.UndeleteAsync(ScopeFor(), MemoAlias, id.ToString(), Token))
                    .Should().ThrowAsync<StudioCapabilityDeniedException>())
                .Which;

            denied.Reason.Should().Be(CapabilityDenialReason.ReadOnly);
            (await IsDeletedAsync(MemoTable, id)).Should().BeTrue();
        }
        finally
        {
            StudioOptions.ReadOnly = false;
        }
    }

    /// <summary>
    /// A policy that lets this visitor edit but not delete stops the undelete too, audited, and changes nothing.
    /// </summary>
    [PostgresFact]
    public async Task Undelete_is_refused_by_a_write_policy_that_refuses_DeleteDocuments()
    {
        Users.SignIn("viewer");
        Authorization.Allow(static resource => resource.Capability != nameof(StudioCapability.DeleteDocuments));

        try
        {
            Guid id = await DeletedMemoAsync("policy");

            await CreateService()
                .Invoking(x => x.UndeleteAsync(ScopeFor(), MemoAlias, id.ToString(), Token))
                .Should().ThrowAsync<StudioNotAuthorizedException>();

            (await IsDeletedAsync(MemoTable, id)).Should().BeTrue();
            Logs.EventIds.Should().Contain(9203);
            Ring.GetLatest().Should().Contain(x => x.Action == "UndeleteDocument" && !x.Succeeded);
        }
        finally
        {
            Authorization.AllowEverything();
        }
    }

    /// <summary>
    /// The same id, soft-deleted in two tenants of a conjoined type: an undelete in one tenant's scope brings back that
    /// tenant's row and leaves the other's deleted, because the session is pinned to the scope's tenant and database and
    /// Marten's own <c>UndoDeleteWhere</c> filters by it.
    /// </summary>
    [PostgresFact]
    public async Task On_a_conjoined_type_undelete_restores_only_the_scoped_tenants_document()
    {
        Users.SignIn("ops");
        Guid id = Guid.NewGuid();

        foreach (string tenant in Tenants)
        {
            await using IDocumentSession session = Store.LightweightSession(tenant);
            session.Store(new UndeleteTenantMemo { Id = id, Text = "memo of " + tenant });
            await session.SaveChangesAsync(Token);
            session.Delete<UndeleteTenantMemo>(id);
            await session.SaveChangesAsync(Token);
        }

        (await TenantDeletedAsync(id, "acme")).Should().BeTrue();
        (await TenantDeletedAsync(id, "globex")).Should().BeTrue();

        DeleteResult result = await CreateService().UndeleteAsync(ScopeFor("acme"), TenantMemoAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.Undeleted);
        (await TenantDeletedAsync(id, "acme")).Should().BeFalse("the scoped tenant's row is restored");
        (await TenantDeletedAsync(id, "globex")).Should().BeTrue("another tenant's row of the same id is not");

        Authorization.Calls.Should().Contain(x =>
            x.Policy == WritePolicyName
            && x.Resource.Capability == nameof(StudioCapability.DeleteDocuments)
            && x.Resource.TenantId == "acme");
    }

    /// <summary>With no tenant in scope a conjoined undelete is refused rather than aimed at Marten's default tenant.</summary>
    [PostgresFact]
    public async Task On_a_conjoined_type_undelete_without_a_tenant_in_scope_changes_nothing()
    {
        Users.SignIn("ops");
        Guid id = Guid.NewGuid();

        await using (IDocumentSession session = Store.LightweightSession("acme"))
        {
            session.Store(new UndeleteTenantMemo { Id = id, Text = "no tenant" });
            await session.SaveChangesAsync(Token);
            session.Delete<UndeleteTenantMemo>(id);
            await session.SaveChangesAsync(Token);
        }

        DeleteResult result = await CreateService().UndeleteAsync(ScopeFor(), TenantMemoAlias, id.ToString(), Token);

        result.Outcome.Should().Be(DeleteOutcome.Refused);
        result.Reason.Should().Contain("multi-tenanted");
        result.Changed.Should().BeFalse();
        (await TenantDeletedAsync(id, "acme")).Should().BeTrue();
    }

    private async Task<Guid> DeletedMemoAsync(string text)
    {
        Guid id = Guid.NewGuid();

        await using IDocumentSession session = Store.LightweightSession();
        session.Store(new UndeleteMemo { Id = id, Text = text });
        await session.SaveChangesAsync(Token);
        session.Delete<UndeleteMemo>(id);
        await session.SaveChangesAsync(Token);

        (await IsDeletedAsync(MemoTable, id)).Should().BeTrue("the arrangement soft-deletes it through Marten");
        return id;
    }

    private async Task<bool?> TenantDeletedAsync(Guid id, string tenant) =>
        (bool?) await ScalarAsync(
            $"select mt_deleted from \"{Schema}\".{TenantMemoTable} where id = '{id}'::uuid and tenant_id = '{tenant}'");
}

/// <summary>A soft-deleted, single-tenanted document of this test class's own.</summary>
[DocumentAlias("undelete_memo")]
public sealed class UndeleteMemo
{
    /// <summary>The identity.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to keep.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>A soft-deleted, conjoined document of this test class's own.</summary>
[DocumentAlias("undelete_tenant_memo")]
public sealed class UndeleteTenantMemo
{
    /// <summary>The identity, the same in every tenant on purpose.</summary>
    public Guid Id { get; set; }

    /// <summary>Something to keep.</summary>
    public string Text { get; set; } = string.Empty;
}
