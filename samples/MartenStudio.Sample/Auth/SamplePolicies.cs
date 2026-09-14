using Microsoft.AspNetCore.Authorization;

namespace MartenStudio.Sample.Auth;

/// <summary>
/// The two policies the demo hands Marten Studio, one per layer of the authorization contract.
/// </summary>
/// <remarks>
/// <see cref="Studio" /> decides who gets in at all - it is what the mapping requires and what
/// <c>MartenStudioOptions.AuthorizationPolicy</c> would name. <see cref="StudioWrite" /> decides each
/// mutating call. The third layer, <c>StoreAuthorizationPolicy</c>, would decide which store, database
/// and tenant; the demo has one store and leaves it unset, which is the "everything passes" case.
/// </remarks>
internal static class SamplePolicies
{
    /// <summary>Claim type the demo's users carry their permissions in.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Who may open the studio at all.</summary>
    public const string Studio = "MartenStudio";

    /// <summary>Who may change anything through it.</summary>
    public const string StudioWrite = "MartenStudioWrite";

    /// <summary>
    /// Who may do the host's own irreversible things - generating a million documents, and truncating
    /// them again.
    /// </summary>
    /// <remarks>
    /// A third policy rather than a reuse of <see cref="StudioWrite" />, because these are not studio
    /// operations at all: creating and destroying sample data is a host concern (plan §5.5), and the
    /// studio's own capability model has nothing to say about it. It maps to
    /// <see cref="DevUsers.DestroyScope" />, which only <c>admin</c> carries.
    /// </remarks>
    public const string StudioAdmin = "MartenStudioAdmin";

    /// <summary>Registers all three policies.</summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Studio, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.ReadScope));

        options.AddPolicy(StudioWrite, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.WriteScope));

        options.AddPolicy(StudioAdmin, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.DestroyScope));
    }
}
