using KStudio.Sample.Auth;

using Microsoft.AspNetCore.Authorization;

namespace KStudio.Sample.Kafka;

/// <summary>
/// KStudio's two policies for the demo (D35), asked about a <see cref="KafkaTopicResource" /> and never about the host studio's own
/// resources: <see cref="Topics" /> for which cluster and topic a visitor may reach, and <see cref="Search" /> for the use of the
/// <c>SearchRecords</c> capability, and of <c>ReadPayloads</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>The per-topic gate, shown.</b> <c>admin</c> reaches everything. <c>support</c> reaches the cluster itself (a question with no topic)
/// and the topics whose names start with <c>orders.</c> or <c>checkout.</c> (the Wolverine application's), so <c>payments.v1</c>, <c>customers.v1</c> and <c>secrets.v1</c> are absent from every
/// list and a search that names one is refused whole. Every other visitor is refused.
/// </para>
/// <para>
/// <b>Payloads: admin yes, support no</b> (D-PAYLOAD decision 8.3). M2's exit (a support user finds a record from a ticket) needs keys and
/// headers only, and the sample then shows both sides of the payload gate: support reads <c>The value of this record is not available to you.</c>
/// on the very record admin reads whole, and a value search is refused for support.
/// </para>
/// <para>
/// <b>Search admits admin and support</b> (orchestrator decision OPEN-3: M2's exit criterion is that a support user finds a record from a
/// ticket). Support's reach stays bounded to <c>orders.*</c> by <see cref="Topics" />, which KStudio always asks first; the search handler
/// applies the same bound itself, so the sample would stay correct if a host set only the capability policy.
/// </para>
/// </remarks>
internal static class SampleKafkaPolicies
{
    /// <summary>The cluster policy: which cluster and topic.</summary>
    public const string Topics = "kafka-topics";

    /// <summary>The capability policy: who may search records.</summary>
    public const string Search = "kafka-search";

    /// <summary>The topic prefix support starts with.</summary>
    public const string SupportPrefix = "orders.";

    /// <summary>The topic prefix of the Wolverine application's checkout topics, which support may also reach.</summary>
    public const string SupportCheckoutPrefix = "checkout.";

    /// <summary>Registers both policies.</summary>
    /// <param name="options">The application's authorization options.</param>
    public static void Configure(AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        options.AddPolicy(Topics, policy => policy.RequireAuthenticatedUser().AddRequirements(new KafkaTopicsRequirement()));
        options.AddPolicy(Search, policy => policy.RequireAuthenticatedUser().AddRequirements(new KafkaSearchRequirement()));
    }

    /// <summary>Registers the handlers of both requirements.</summary>
    /// <param name="services">The application's services.</param>
    public static void AddHandlers(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IAuthorizationHandler, KafkaTopicsHandler>();
        services.AddSingleton<IAuthorizationHandler, KafkaSearchHandler>();
    }

    /// <summary>Whether a user holding <paramref name="isAdmin" /> or <paramref name="isSupport" /> may reach <paramref name="topic" /> (or the cluster, for <see langword="null" />).</summary>
    internal static bool MayReach(bool isAdmin, bool isSupport, string? topic) =>
        isAdmin || (isSupport && (topic is null
            || topic.StartsWith(SupportPrefix, StringComparison.Ordinal)
            || topic.StartsWith(SupportCheckoutPrefix, StringComparison.Ordinal)));
}

/// <summary>The requirement behind <see cref="SampleKafkaPolicies.Topics" />.</summary>
internal sealed class KafkaTopicsRequirement : IAuthorizationRequirement;

/// <summary>The requirement behind <see cref="SampleKafkaPolicies.Search" />.</summary>
internal sealed class KafkaSearchRequirement : IAuthorizationRequirement;

/// <summary>Decides <see cref="KafkaTopicsRequirement" />: admin passes everything, support passes the cluster and <c>orders.*</c>.</summary>
internal sealed class KafkaTopicsHandler : AuthorizationHandler<KafkaTopicsRequirement, KafkaTopicResource>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, KafkaTopicsRequirement requirement, KafkaTopicResource resource)
    {
        if (SampleKafkaPolicies.MayReach(
                context.User.HasClaim(SamplePolicies.ScopeClaim, DevUsers.KafkaAllScope),
                context.User.HasClaim(SamplePolicies.ScopeClaim, DevUsers.KafkaOrdersScope),
                resource.Topic))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// Decides <see cref="KafkaSearchRequirement" />: <c>SearchRecords</c> for admin and support, with support bounded to <c>orders.*</c> (OPEN-3), and
/// <c>ReadPayloads</c> for admin only.
/// </summary>
internal sealed class KafkaSearchHandler : AuthorizationHandler<KafkaSearchRequirement, KafkaTopicResource>
{
    /// <inheritdoc />
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, KafkaSearchRequirement requirement, KafkaTopicResource resource)
    {
        bool admin = context.User.HasClaim(SamplePolicies.ScopeClaim, DevUsers.KafkaAllScope);
        bool support = context.User.HasClaim(SamplePolicies.ScopeClaim, DevUsers.KafkaOrdersScope);

        // A value is the payload gate's: admin only, whatever support may search.
        if (string.Equals(resource.Capability, nameof(KafkaStudioCapabilities.ReadPayloads), StringComparison.Ordinal))
        {
            if (admin)
            {
                context.Succeed(requirement);
            }

            return Task.CompletedTask;
        }

        if (SampleKafkaPolicies.MayReach(admin, support, resource.Topic))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
