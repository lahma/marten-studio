namespace MartenStudio.Services.Sources;

/// <summary>
/// A scope that passed every check, with the source and the database it names - what every shared service reads
/// through once <see cref="StudioSourceResolver" /> has said yes.
/// </summary>
/// <remarks>
/// <para>
/// A record rather than an interface so that a studio's own screens can take a richer subtype without a cast at every
/// use: MartenStudio's <c>ResolvedScope</c> derives from this and adds the store and its database, and its Marten-only
/// code keeps reading those. Shared code takes this type and nothing more specific, which is why the database is named
/// <see cref="Target" /> here - a subtype is free to name its own richer database <c>Database</c>.
/// </para>
/// <para>
/// <b>Holding one is not a grant to reuse it.</b> It was authorized for one visitor, one scope and one capability; a
/// service resolves again for every call (D5), and nothing caches one across visitors.
/// </para>
/// </remarks>
/// <param name="Scope">The scope that was resolved.</param>
/// <param name="Source">The source's registration.</param>
/// <param name="Target">The database the scope resolved to - for a tenant, the instance that tenant was built from.</param>
internal record ResolvedSource(StudioScope Scope, StudioSourceRegistration Source, IStudioDatabase Target)
{
    /// <summary>The source's registration.</summary>
    /// <remarks>
    /// Get-only, so that <c>with { Source = … }</c> does not compile: a subtype derives this from its own richer
    /// members (MartenStudio's <c>ResolvedScope</c> from its <c>Registration</c>), and a copy that replaced only this
    /// would show shared code one source and the subtype's own code another.
    /// </remarks>
    public StudioSourceRegistration Source { get; } = Source;

    /// <summary>The database the scope resolved to.</summary>
    /// <remarks>Get-only for the same reason as <see cref="Source" />: <c>ResolvedScope</c> derives it from its <c>Database</c>.</remarks>
    public IStudioDatabase Target { get; } = Target;

    /// <summary>The tenant in scope, or <see langword="null" /> for all of them.</summary>
    public string? TenantId => Scope.TenantId;
}
