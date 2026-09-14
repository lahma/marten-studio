namespace MartenStudio.Services;

/// <summary>
/// What a visitor <see cref="MartenStudioOptions.WriteAuthorizationPolicy" /> refuses is told, per
/// capability.
/// </summary>
/// <remarks>
/// <para>
/// A capability is a property of the application; a write policy is a property of the person. The two
/// refusals therefore read differently and must not be collapsed: a capability that is off names the
/// option a host would set, because that is something somebody can act on, while a policy refusal names
/// the account, because no amount of configuration changes it for this visitor. Somebody who cannot tell
/// the two apart goes and asks the wrong person for the wrong thing.
/// </para>
/// <para>
/// The control stays on screen, disabled, wearing this as its <c>title</c> and — because a tooltip does
/// not exist on a touch screen — with the same sentence rendered beside it. Hiding it would make a studio
/// whose host enabled the operation look identical to one whose host did not.
/// </para>
/// <para>
/// None of this is the enforcement. Every mutating service resolves the scope with the same capability
/// and throws (AGENTS.md hard rule 5); this is only the part a person reads.
/// </para>
/// <para>
/// <c>DocumentWriteActions</c> still declares its own <c>EditRefusedByPolicy</c> and
/// <c>DeleteRefusedByPolicy</c> constants, which P8b shipped before this table existed. They are
/// word-for-word the two entries below, and nothing would fail if one of the pair were edited and the
/// other were not. There should be one copy: folding that component onto this is a two-line change for
/// whichever packet next owns <c>Components/Pages/Documents</c>, and it is worth doing.
/// </para>
/// </remarks>
internal static class WritePolicyRefusal
{
    /// <summary>The one sentence a refused visitor is shown for <paramref name="capability" />.</summary>
    public static string For(StudioCapability capability) => For(capability.ToString());

    /// <summary>
    /// The same, by capability name.
    /// </summary>
    /// <remarks>
    /// The Razor compiler emits a public component class and a public <c>[Parameter]</c> cannot have an
    /// internal type, so <c>WriteRefusal</c> takes the capability as
    /// <c>nameof(StudioCapability.ArchiveStreams)</c> - checked by the compiler, unlike a literal - and
    /// this is the overload it reaches. The same trade <c>CapabilityDisabled</c> makes.
    /// </remarks>
    public static string For(string capability) => capability switch
    {
        nameof(StudioCapability.EditDocuments) => "Your account may not edit documents here.",
        nameof(StudioCapability.DeleteDocuments) => "Your account may not delete documents here.",
        nameof(StudioCapability.ArchiveStreams) => "Your account may not archive streams here.",
        nameof(StudioCapability.ManageDeadLetters) => "Your account may not manage dead letters here.",
        nameof(StudioCapability.ControlDaemon) => "Your account may not control the daemon here.",
        nameof(StudioCapability.RebuildProjections) => "Your account may not rebuild projections here.",
        nameof(StudioCapability.CorrectProgression) => "Your account may not correct projection progress here.",
        nameof(StudioCapability.ApplySchemaChanges) => "Your account may not apply schema changes here.",
        nameof(StudioCapability.RunSql) => "Your account may not run SQL here.",
        _ => "Your account may not do this here.",
    };
}
