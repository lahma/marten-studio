using KStudio.Services;
using KStudio.Tests.Support;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;


namespace KStudio.Tests.Services;

/// <summary>D35: KStudio's capabilities default to off, and the host studio's <c>ReadOnly</c> is the one master switch.</summary>
public class KafkaCapabilityGuardTests
{
    [Fact]
    public void Every_flag_defaults_to_false()
    {
        KafkaStudioCapabilities none = new();

        none.SearchRecords.Should().BeFalse();
        KafkaStudioCapabilities.None().SearchRecords.Should().BeFalse();
        new KafkaStudioOptions().Capabilities.SearchRecords.Should().BeFalse();
    }

    [Fact]
    public void All_turns_every_flag_on()
    {
        KafkaStudioCapabilities all = KafkaStudioCapabilities.All();

        typeof(KafkaStudioCapabilities).GetProperties().Where(static x => x.PropertyType == typeof(bool))
            .Should().NotBeEmpty().And.OnlyContain(property => (bool) property.GetValue(all)!, "All() sets every flag the class has");
        Enum.GetNames<KafkaCapability>().Should().BeEquivalentTo(
            typeof(KafkaStudioCapabilities).GetProperties().Where(static x => x.PropertyType == typeof(bool)).Select(static x => x.Name),
            "one enum member per flag, named identically");
    }

    [Fact]
    public void A_capability_is_effective_only_with_the_flag_set_and_ReadOnly_off()
    {
        using KafkaStudioHarness off = new();
        off.Context.Services.GetRequiredService<KafkaCapabilityGuard>().IsEnabled(KafkaCapability.SearchRecords).Should().BeFalse();

        using KafkaStudioHarness on = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        KafkaCapabilityGuard guard = on.Context.Services.GetRequiredService<KafkaCapabilityGuard>();
        guard.IsEnabled(KafkaCapability.SearchRecords).Should().BeTrue();

        on.Context.Options.ReadOnly = true;
        guard.IsEnabled(KafkaCapability.SearchRecords).Should().BeFalse("the host studio's master switch turns every capability off");
        guard.IsConfigured(KafkaCapability.SearchRecords).Should().BeTrue();
    }

    [Fact]
    public void ReadOnly_names_the_hosts_option()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities = KafkaStudioCapabilities.All());
        harness.Context.Options.ReadOnly = true;
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action require = () => guard.Require(KafkaCapability.SearchRecords);

        require.Should().Throw<KafkaCapabilityDeniedException>().WithMessage(
            "KStudio refused 'SearchRecords' because PostgresStudioOptions.ReadOnly is true, which turns every capability off.");
    }

    [Fact]
    public void A_refusal_names_the_capability_option()
    {
        using KafkaStudioHarness harness = new();
        KafkaCapabilityGuard guard = harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>();

        Action require = () => guard.Require(KafkaCapability.SearchRecords);

        KafkaCapabilityDeniedException denial = require.Should().Throw<KafkaCapabilityDeniedException>().WithMessage(
            "KStudio refused 'SearchRecords' because KafkaStudioOptions.Capabilities.SearchRecords is false.").Which;
        denial.CapabilityOption.Should().Be("KafkaStudioOptions.Capabilities.SearchRecords");
        denial.Capability.Should().Be(KafkaCapability.SearchRecords);
    }

    [Fact]
    public void A_capability_that_is_on_requires_nothing()
    {
        using KafkaStudioHarness harness = new(static o => o.Capabilities.SearchRecords = true);

        Action require = () => harness.Context.Services.GetRequiredService<KafkaCapabilityGuard>().Require(KafkaCapability.SearchRecords);

        require.Should().NotThrow();
    }

    [Fact]
    public void The_options_validation_refuses_a_null_capabilities_object()
    {
        ServiceCollection services = new();
        services.AddKafkaStudio(static o => o.Capabilities = null!);
        using ServiceProvider provider = services.BuildServiceProvider();

        Action read = () => _ = provider.GetRequiredService<IOptions<KafkaStudioOptions>>().Value;

        read.Should().Throw<OptionsValidationException>().WithMessage(
            "*KafkaStudioOptions.Capabilities cannot be null: use new KafkaStudioCapabilities() for none, or KafkaStudioCapabilities.All()*");
    }
}
