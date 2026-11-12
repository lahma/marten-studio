using System.Security.Claims;

using KStudio.Sample.Auth;
using KStudio.Sample.Kafka;

using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;

namespace KStudio.Integration.Tests.Sample;

/// <summary>
/// The sample's two KStudio policies (D35), asked the way KStudio asks them - about a <see cref="KafkaTopicResource" /> through the application's
/// own <see cref="IAuthorizationService" /> - for the sample's two users (D-UI decision 18, OPEN-3): admin reaches everything, support reaches the
/// cluster and <c>orders.*</c> and no other topic, and an anonymous visitor reaches nothing. No Docker.
/// </summary>
public class SamplePolicyTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private static ServiceProvider Services()
    {
        ServiceCollection services = new();
        services.AddLogging();
        services.AddAuthorization(SamplePolicies.Configure);
        SampleKafkaPolicies.AddHandlers(services);
        return services.BuildServiceProvider();
    }

    private static async Task<bool> AllowedAsync(string? user, string policy, string? topic, string? capability = null)
    {
        await using ServiceProvider services = Services();
        ClaimsPrincipal principal = user is null ? new ClaimsPrincipal(new ClaimsIdentity()) : DevUsers.Authenticate(user, user)!;

        AuthorizationResult result = await services.GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, new KafkaTopicResource("local", null, topic, capability), policy);

        return result.Succeeded;
    }

    [Theory]
    [InlineData("admin", null, true)]
    [InlineData("admin", "orders.v1", true)]
    [InlineData("admin", "payments.v1", true)]
    [InlineData("admin", "secrets.v1", true)]
    [InlineData("support", null, true)]
    [InlineData("support", "orders.v1", true)]
    [InlineData("support", "orders.v1.dlq", true)]
    [InlineData("support", "payments.v1", false)]
    [InlineData("support", "customers.v1", false)]
    [InlineData("support", "secrets.v1", false)]
    [InlineData("support", "kstudio-sample.seed-v1", false)]
    [InlineData("support", "xorders.v1", false)]
    [InlineData(null, null, false)]
    [InlineData(null, "orders.v1", false)]
    public async Task The_topics_policy_lets_admin_reach_everything_and_support_the_cluster_and_orders_only(string? user, string? topic, bool expected)
    {
        (await AllowedAsync(user, SampleKafkaPolicies.Topics, topic)).Should().Be(expected, $"{user ?? "anonymous"} asking about {topic ?? "the cluster"}");
    }

    [Theory]
    [InlineData("admin", "payments.v1", true)]
    [InlineData("support", "orders.v1", true)]
    [InlineData("support", "payments.v1", false)]
    [InlineData(null, "orders.v1", false)]
    public async Task The_search_policy_admits_admin_and_support_with_support_bounded_to_orders(string? user, string topic, bool expected)
    {
        (await AllowedAsync(user, SampleKafkaPolicies.Search, topic, "SearchRecords")).Should().Be(expected, $"{user ?? "anonymous"} searching {topic}");
    }

    [Fact]
    public void The_sample_has_exactly_the_two_users_the_readme_names()
    {
        DevUsers.All.Select(static x => x.Key).Should().BeEquivalentTo("admin", "support");
        DevUsers.Authenticate("support", "wrong").Should().BeNull();
        DevUsers.Authenticate("viewer", "viewer").Should().BeNull();
        _ = Token;
    }
}
