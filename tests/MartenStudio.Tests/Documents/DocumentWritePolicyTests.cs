using AngleSharp.Dom;

using Bunit;

using MartenStudio.Internal.Sql;
using MartenStudio.Services;
using MartenStudio.Services.Documents;
using MartenStudio.Tests.Support;

using Microsoft.Extensions.Options;

using DetailModel = MartenStudio.Services.Documents.DocumentDetail;
using DetailPage = MartenStudio.Components.Pages.Documents.DocumentDetail;
using ListPage = MartenStudio.Components.Pages.Documents.Documents;
using WriteActions = MartenStudio.Components.Pages.Documents.DocumentWriteActions;

namespace MartenStudio.Tests.Documents;

/// <summary>
/// The write controls against <see cref="MartenStudioOptions.WriteAuthorizationPolicy" />, which is asked
/// about the visitor rather than about the process.
/// </summary>
/// <remarks>
/// <para>
/// A capability is a property of the application; a write policy is a property of the person. A studio
/// with <c>EditDocuments</c> on, mounted for a team where two people may write and forty may not, showed
/// all forty-two a live Edit button and told the forty only after they had pressed it — the refusal came
/// from the service, correctly, and arrived as an error toast about something they could not have known.
/// </para>
/// <para>
/// Disabled and not hidden, deliberately. "This studio can edit documents and your account may not" is a
/// different fact from "this studio cannot edit documents", and somebody who cannot tell them apart goes
/// and asks the wrong person for the wrong thing.
/// </para>
/// <para>
/// None of this is the enforcement and no test here pretends it is: <c>IDocumentWriteService</c> resolves
/// the scope with the same capability and throws (AGENTS.md hard rule 5), which
/// <c>DocumentWriteServiceTests</c> and the live suite are about.
/// </para>
/// </remarks>
public class DocumentWritePolicyTests
{
    private const string WritePolicy = "MartenStudioWriter";
    private const string Id = "8f1d5a6e-1a2b-4c3d-9e8f-000000000001";

    // ------------------------------------------------------------------------------------------------
    // The service the pages ask
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// The capability travels on the resource, spelled the way <c>DocumentWriteService</c> spells it when
    /// it resolves the scope — so the answer a control renders is the answer the write is about to get.
    /// </summary>
    [Fact]
    public async Task IsAuthorizedAsync_asks_the_write_policy_with_the_capability_on_the_resource()
    {
        TestStoreAuthorizationService policies = new();
        StudioAuthorization authorization = Authorization(policies, writePolicy: WritePolicy);

        bool allowed = await authorization.IsAuthorizedAsync(
            new StudioScope("default", "localhost.marten", "acme"),
            StudioCapability.EditDocuments,
            Xunit.TestContext.Current.CancellationToken);

        allowed.Should().BeTrue();

        (string policy, MartenStoreResource resource) = policies.Calls.Should().ContainSingle().Subject;
        policy.Should().Be(WritePolicy);
        resource.StoreName.Should().Be("default");
        resource.DatabaseIdentifier.Should().Be("localhost.marten");
        resource.TenantId.Should().Be("acme");
        resource.Capability.Should().Be(
            nameof(StudioCapability.EditDocuments),
            "the resolver and the write service name the capability exactly this way");
    }

    /// <summary>
    /// A refusal is a refusal, and it is per capability: a policy is free to allow editing and refuse
    /// deleting, so the two questions are asked separately.
    /// </summary>
    [Fact]
    public async Task IsAuthorizedAsync_answers_per_capability()
    {
        TestStoreAuthorizationService policies = new();
        policies.Allow(static resource => resource.Capability == nameof(StudioCapability.EditDocuments));
        StudioAuthorization authorization = Authorization(policies, writePolicy: WritePolicy);

        StudioScope scope = new("default", "localhost.marten", null);

        (await authorization.IsAuthorizedAsync(scope, StudioCapability.EditDocuments, Xunit.TestContext.Current.CancellationToken))
            .Should().BeTrue();
        (await authorization.IsAuthorizedAsync(scope, StudioCapability.DeleteDocuments, Xunit.TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    /// <summary>
    /// With no policy configured nothing is asked and everything passes, which is what keeps an
    /// application that never set the option exactly as it was.
    /// </summary>
    [Fact]
    public async Task With_no_write_policy_nothing_is_asked_and_the_answer_is_yes()
    {
        TestStoreAuthorizationService policies = new();
        policies.DenyEverything();
        StudioAuthorization authorization = Authorization(policies, writePolicy: null);

        bool allowed = await authorization.IsAuthorizedAsync(
            new StudioScope("default", "localhost.marten", null),
            StudioCapability.EditDocuments,
            Xunit.TestContext.Current.CancellationToken);

        allowed.Should().BeTrue("a host that configured no policy configured no refusal");
        policies.Calls.Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // The detail page
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void A_visitor_the_write_policy_refuses_sees_edit_and_delete_disabled_and_is_told_why()
    {
        using var context = RefusingContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = RenderDetail(context);

        IElement edit = page.Find(".ms-doc-edit-btn");
        edit.HasAttribute("disabled").Should().BeTrue();
        edit.GetAttribute("title").Should().Contain("may not edit documents here");

        IElement delete = page.Find(".ms-doc-delete-btn");
        delete.HasAttribute("disabled").Should().BeTrue();
        delete.GetAttribute("title").Should().Contain("may not delete documents here");

        page.Find(".ms-doc-write-actions .ms-write-refusal").TextContent
            .Should().Contain("may not edit or delete documents here");
    }

    /// <summary>
    /// The controls stay on screen. Hiding them would make a studio whose host enabled editing look
    /// identical to one whose host did not.
    /// </summary>
    [Fact]
    public void The_refused_controls_are_rendered_rather_than_removed()
    {
        using var context = RefusingContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = RenderDetail(context);

        page.FindAll(".ms-doc-write-actions").Should().ContainSingle();
        page.FindAll(".ms-doc-edit-btn").Should().ContainSingle();
        page.FindAll(".ms-doc-delete-btn").Should().ContainSingle();
    }

    /// <summary>A policy that allows one capability and refuses the other says so about each.</summary>
    [Fact]
    public void A_policy_that_allows_editing_and_refuses_deleting_disables_only_the_delete()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(static resource =>
            resource.Capability != nameof(StudioCapability.DeleteDocuments));
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = RenderDetail(context);

        page.Find(".ms-doc-edit-btn").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-doc-delete-btn").HasAttribute("disabled").Should().BeTrue();
        page.Find(".ms-write-refusal").TextContent.Should().Be(WriteActions.DeleteRefusedByPolicy);
    }

    /// <summary>
    /// A capability that is simply off still names the option, because that one is about the application
    /// and is something a host can act on. The policy's refusal is not mentioned: the guard refuses first,
    /// before a scope is ever resolved.
    /// </summary>
    [Fact]
    public void A_capability_that_is_off_still_names_the_option_rather_than_the_policy()
    {
        using var context = new DocumentsComponentContext();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.DenyEverything();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = RenderDetail(context);

        page.Find(".ms-doc-edit-btn").GetAttribute("title")
            .Should().Be("Requires MartenStudioOptions.Capabilities.EditDocuments");
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    /// <summary>An undelete is a write like any other, and answers to the same capability.</summary>
    [Fact]
    public void Undelete_is_refused_for_the_same_visitor()
    {
        using var context = RefusingContext();
        context.Data.Detail = DocumentDetailResult.Ok(Detail(deleted: true));

        var page = RenderDetail(context);

        IElement undelete = page.Find(".ms-doc-undelete-btn");
        undelete.HasAttribute("disabled").Should().BeTrue();
        undelete.GetAttribute("title").Should().Contain("may not delete documents here");
    }

    // ------------------------------------------------------------------------------------------------
    // The list page
    // ------------------------------------------------------------------------------------------------

    [Fact]
    public void Bulk_delete_is_disabled_for_a_visitor_the_write_policy_refuses()
    {
        using var context = RefusingContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Id)]);

        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-select").Change(true);

        IElement delete = page.Find(".ms-doc-bulk-delete");
        delete.HasAttribute("disabled").Should().BeTrue();
        delete.GetAttribute("title").Should().Contain("may not delete documents here");

        page.Find(".ms-doc-bulk-bar .ms-write-refusal").TextContent
            .Should().Be(WriteActions.DeleteRefusedByPolicy);
    }

    /// <summary>
    /// The selection itself stays: copying forty ids out of a list is a read, and a person who may not
    /// delete may perfectly well want them.
    /// </summary>
    [Fact]
    public void The_selection_and_its_other_actions_survive_the_refusal()
    {
        using var context = RefusingContext();
        context.Data.Rail = FakeDocuments.Rail();
        context.Data.Page = FakeDocuments.Page(rows: [FakeDocuments.Row(Id)]);

        var page = context.Render<ListPage>(parameters => parameters.Add(x => x.Alias, "customer"));
        page.Find(".ms-doc-select").Change(true);

        page.FindAll(".ms-doc-bulk-bar").Should().ContainSingle();
        page.FindAll(".ms-doc-bulk-actions .ms-btn")
            .Where(static x => !x.ClassList.Contains("ms-doc-bulk-delete"))
            .Should().AllSatisfy(static x => x.HasAttribute("disabled").Should().BeFalse());
    }

    [Fact]
    public void With_no_write_policy_every_control_is_live()
    {
        using var context = new DocumentsComponentContext();
        context.WithAllCapabilities();
        context.Data.Detail = DocumentDetailResult.Ok(Detail());

        var page = RenderDetail(context);

        page.Find(".ms-doc-edit-btn").HasAttribute("disabled").Should().BeFalse();
        page.Find(".ms-doc-delete-btn").HasAttribute("disabled").Should().BeFalse();
        page.FindAll(".ms-write-refusal").Should().BeEmpty();
    }

    // ------------------------------------------------------------------------------------------------
    // Fixtures
    // ------------------------------------------------------------------------------------------------

    /// <summary>
    /// A studio whose host enabled every capability and whose write policy refuses this visitor. Only the
    /// write policy is set: with no store policy, reads pass without anything being asked, which is the
    /// shape that isolates the write axis.
    /// </summary>
    private static DocumentsComponentContext RefusingContext()
    {
        DocumentsComponentContext context = new();
        context.WithAllCapabilities();
        context.Options.WriteAuthorizationPolicy = WritePolicy;
        context.AuthorizationService.Allow(static resource => resource.Capability is null);
        return context;
    }

    private static StudioAuthorization Authorization(TestStoreAuthorizationService policies, string? writePolicy)
    {
        MartenStudioOptions options = new() { WriteAuthorizationPolicy = writePolicy };
        TestAuthenticationStateProvider visitor = new();
        visitor.SignIn("tester");

        return new StudioAuthorization(Options.Create(options), policies, visitor);
    }

    private static IRenderedComponent<DetailPage> RenderDetail(DocumentsComponentContext context)
    {
        context.Navigate($"marten/documents/customer/doc?id={Id}");
        return context.Render<DetailPage>(parameters => parameters.Add(x => x.Alias, "customer"));
    }

    private static DetailModel Detail(bool deleted = false) => new()
    {
        Alias = "customer",
        Id = Id,
        Json = """{"Name":"Customer 01"}""",
        TextBytes = 22,
        StoredBytes = 22,
        Columns =
        [
            new PhysicalColumnValue("id", Id, PhysicalColumnRole.Identity),
            new PhysicalColumnValue("data", null, PhysicalColumnRole.Data),
            new PhysicalColumnValue("mt_deleted", deleted ? "True" : "False", PhysicalColumnRole.Metadata),
        ],
        TableName = "\"studio_sample\".\"mt_doc_customer\"",
        ClrType = typeof(DocumentWritePolicyTests),
        IsRegistered = true,
        IsDeleted = deleted,
        IdColumnType = DocumentIdColumnType.Uuid,
    };
}
