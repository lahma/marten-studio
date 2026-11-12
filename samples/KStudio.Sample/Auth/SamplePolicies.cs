using Microsoft.AspNetCore.Authorization;

namespace KStudio.Sample.Auth;

/// <summary>
/// The policies the demo hands the studio it hosts: who may open it, and who may change anything in its Marten half.
/// </summary>
/// <remarks>
/// <see cref="Studio" /> decides who gets in at all; it is what the mapping requires. <see cref="StudioWrite" /> decides each
/// mutating call of the Marten screens. KStudio's two policies, which are asked about a topic, are
/// <see cref="Kafka.SampleKafkaPolicies" />'s.
/// </remarks>
internal static class SamplePolicies
{
    /// <summary>Claim type the demo's users carry their permissions in.</summary>
    public const string ScopeClaim = "scope";

    /// <summary>Who may open the studio at all.</summary>
    public const string Studio = "MartenStudio";

    /// <summary>Who may change anything through the Marten screens.</summary>
    public const string StudioWrite = "MartenStudioWrite";

    /// <summary>Registers every policy the sample uses, KStudio's two included.</summary>
    /// <param name="options">The application's authorization options.</param>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Studio, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.ReadScope));

        options.AddPolicy(StudioWrite, policy => policy
            .RequireAuthenticatedUser()
            .RequireClaim(ScopeClaim, DevUsers.WriteScope));

        Kafka.SampleKafkaPolicies.Configure(options);
    }
}
