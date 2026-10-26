using Microsoft.AspNetCore.Authorization;

namespace PostgresStudio.Sample.Auth;

/// <summary>
/// The two policies the demo hands Postgres Studio: who gets in, and who may use each of its three capabilities.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Studio" /> decides who gets in at all - it is what the mapping requires and what
/// <c>PostgresStudioOptions.AuthorizationPolicy</c> names. <see cref="StudioCapability" /> is the write policy: the
/// studio asks it before every read authorized like a write, against a <see cref="PostgresStudioResource" /> naming the
/// capability, and <see cref="StudioCapabilityHandler" /> answers from the user's scopes - <c>BrowseDatabase</c>,
/// <c>ViewDatabaseActivity</c> and <c>RunSql</c> each have their own. The third layer, <c>SourceAuthorizationPolicy</c>,
/// would decide which source and database - a tenant database's name is in the same resource - and the demo leaves it
/// unset, which is the "everything passes" case.
/// </para>
/// </remarks>
internal static class SamplePolicies
{
    /// <summary>Claim type the demo's users carry their permissions in.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Who may open the studio at all.</summary>
    public const string Studio = "PostgresStudio";

    /// <summary>Who may use which capability, asked per capability with the resource attached.</summary>
    public const string StudioCapability = "PostgresStudioCapability";

    /// <summary>The scope each of Postgres Studio's capabilities needs, by the name the studio puts on the resource.</summary>
    private static readonly Dictionary<string, string> ScopeByCapability = new(StringComparer.Ordinal)
    {
        [nameof(PostgresStudioCapabilities.BrowseDatabase)] = DevUsers.BrowseScope,
        [nameof(PostgresStudioCapabilities.ViewDatabaseActivity)] = DevUsers.ActivityScope,
        [nameof(PostgresStudioCapabilities.RunSql)] = DevUsers.SqlScope,
    };

    /// <summary>Registers both policies and the handler the capability policy needs.</summary>
    public static IServiceCollection AddSamplePolicies(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddAuthorization(static options =>
        {
            options.AddPolicy(Studio, static policy => policy
                .RequireAuthenticatedUser()
                .RequireClaim(ScopeClaim, DevUsers.ReadScope));

            options.AddPolicy(StudioCapability, static policy => policy
                .RequireAuthenticatedUser()
                .AddRequirements(new StudioCapabilityRequirement()));
        });

        services.AddSingleton<IAuthorizationHandler, StudioCapabilityHandler>();

        return services;
    }

    /// <summary>The scope <paramref name="capability" /> needs, or <see langword="null" /> for one the demo does not know.</summary>
    internal static string? ScopeFor(string? capability) =>
        capability is not null && ScopeByCapability.TryGetValue(capability, out string? scope) ? scope : null;
}

/// <summary>The capability policy's one requirement: the user holds the scope for the capability being exercised.</summary>
internal sealed class StudioCapabilityRequirement : IAuthorizationRequirement;

/// <summary>
/// Answers <see cref="StudioCapabilityRequirement" /> from the <see cref="PostgresStudioResource" /> the studio passes:
/// the capability it names, mapped to the scope the user must hold.
/// </summary>
/// <remarks>
/// It fails closed. A capability the demo does not know - one a later Postgres Studio adds - needs a scope nobody has,
/// and a call without the resource, or without a capability, is not one this policy was written for.
/// </remarks>
internal sealed class StudioCapabilityHandler : AuthorizationHandler<StudioCapabilityRequirement, PostgresStudioResource>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        StudioCapabilityRequirement requirement,
        PostgresStudioResource resource)
    {
        if (SamplePolicies.ScopeFor(resource.Capability) is { } scope && context.User.HasClaim(SamplePolicies.ScopeClaim, scope))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
