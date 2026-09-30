using MartenStudio.Services.Database;
using MartenStudio.Services.Schema;

namespace MartenStudio.Tests.Schema;

/// <summary>
/// DB-7-fix, item 1: what the Drift and DDL tabs are told when the script is withheld - each sentence names
/// the option that would change it and says why the script is sensitive - and the shape a refusal takes, which
/// carries nothing of the script at all.
/// </summary>
public class SchemaScriptGateTests
{
    [Theory]
    [InlineData(nameof(SchemaScript.Check), "The drift check")]
    [InlineData(nameof(SchemaScript.Preview), "The migration preview")]
    [InlineData(nameof(SchemaScript.Ddl), "The database script")]
    [InlineData(nameof(SchemaScript.Apply), "The migration an apply runs")]
    public void The_store_policy_refusal_says_the_script_spans_every_tenant_and_names_the_option(string script, string noun)
    {
        string sentence = SchemaScriptGate.StorePolicyDenial(Enum.Parse<SchemaScript>(script));

        sentence.Should().StartWith(noun)
            .And.Contain("spans every tenant")
            .And.Contain("MartenStudioOptions.StoreAuthorizationPolicy")
            .And.Contain("no tenant selected");
    }

    [Fact]
    public void The_capability_refusal_names_BrowseDatabase_and_says_the_script_prints_hidden_types()
    {
        string sentence = SchemaScriptGate.HiddenTypesDenial(SchemaScript.Ddl, DatabaseRefusal.CapabilityOff, null);

        sentence.Should().Contain("MartenStudioOptions.Capabilities.BrowseDatabase")
            .And.Contain("document types the host hides")
            .And.Contain("MartenStudioOptions.IsDocumentTypeVisible");
    }

    [Fact]
    public void The_write_policy_refusal_names_the_write_policy_and_the_capability_it_was_asked_about()
    {
        string sentence = SchemaScriptGate.HiddenTypesDenial(SchemaScript.Preview, DatabaseRefusal.WritePolicy, null);

        sentence.Should().Contain("MartenStudioOptions.WriteAuthorizationPolicy")
            .And.Contain("BrowseDatabase")
            .And.Contain("no tenant selected");
    }

    [Fact]
    public void ReadOnly_is_named_as_ReadOnly_rather_than_as_the_capability()
    {
        SchemaScriptGate.HiddenTypesDenial(SchemaScript.Check, DatabaseRefusal.ReadOnly, null)
            .Should().Contain("MartenStudioOptions.ReadOnly is true");
    }

    [Fact]
    public void Any_other_refusal_keeps_the_gates_own_sentence()
    {
        SchemaScriptGate.HiddenTypesDenial(SchemaScript.Ddl, DatabaseRefusal.Unavailable, "No Marten store is registered under 'x'.")
            .Should().EndWith("No Marten store is registered under 'x'.");
    }

    /// <summary>A refused check carries its own status and badge, and none of Marten's own sentences.</summary>
    [Fact]
    public void A_refused_check_is_withheld_rather_than_cannot_report()
    {
        var refusal = new SchemaScriptRefusal(DatabaseRefusal.StorePolicy, "no");

        SchemaCheck check = SchemaCheck.Refused(refusal);

        check.Status.Should().Be(SchemaCheckStatus.Withheld);
        check.BadgeText.Should().Be("Withheld");
        check.Withheld.Should().Be(refusal);
        check.Objects.Should().BeEmpty();
        check.Schemas.Should().BeEmpty();
        check.AssertionMessage.Should().BeNull("Marten's assertion message names the objects it disagrees about");
    }

    [Fact]
    public void A_refused_preview_has_no_SQL_no_deltas_and_nothing_to_apply()
    {
        MigrationPreview preview = MigrationPreview.Refused(new SchemaScriptRefusal(DatabaseRefusal.CapabilityOff, "no"));

        preview.HasSql.Should().BeFalse();
        preview.Deltas.Should().BeEmpty();
        preview.Notice.Should().BeNull();
        preview.IsDestructive.Should().BeFalse();
        preview.Withheld!.Kind.Should().Be(DatabaseRefusal.CapabilityOff);
    }

    [Fact]
    public void A_refused_script_has_no_text_and_is_not_an_error()
    {
        DdlScript script = DdlScript.Refused(new SchemaScriptRefusal(DatabaseRefusal.WritePolicy, "no"));

        script.HasText.Should().BeFalse();
        script.Reason.Should().BeNull("a refusal is drawn as a note, not as an error with a retry");
        script.Withheld!.Kind.Should().Be(DatabaseRefusal.WritePolicy);
    }

    [Fact]
    public void A_refused_apply_did_not_succeed_and_says_why()
    {
        SchemaApplyResult result = SchemaApplyResult.Refused(new SchemaScriptRefusal(DatabaseRefusal.StorePolicy, "the sentence"));

        result.Succeeded.Should().BeFalse();
        result.ObjectCount.Should().Be(0);
        result.Message.Should().Be("the sentence");
        result.Withheld!.Kind.Should().Be(DatabaseRefusal.StorePolicy);
    }
}
