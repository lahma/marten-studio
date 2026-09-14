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

    /// <summary>Registers both policies.</summary>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Studio, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.ReadScope));

        options.AddPolicy(StudioWrite, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.WriteScope));
    }
}
