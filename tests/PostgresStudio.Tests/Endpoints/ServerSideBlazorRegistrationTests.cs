using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Tests.Endpoints;

/// <summary>
/// What <see cref="ServerSideBlazorRegistration.RemoveRepeatedOptionsSetups" /> removes and, as much, what it leaves
/// (issue #38): a descriptor the registration added that repeats one the container held, and nothing of the host's.
/// </summary>
public class ServerSideBlazorRegistrationTests
{
    private sealed class Knobs
    {
        public int Value { get; set; }
    }

    private sealed class KnobsSetup : IConfigureOptions<Knobs>
    {
        public void Configure(Knobs options) => options.Value = 1;
    }

    private sealed class OtherKnobsSetup : IConfigureOptions<Knobs>
    {
        public void Configure(Knobs options) => options.Value = 2;
    }

    private static readonly Action<Knobs> Shared = static o => o.Value = 3;

    private static ServiceDescriptor Setup<T>() where T : class, IConfigureOptions<Knobs> =>
        ServiceDescriptor.Singleton<IConfigureOptions<Knobs>, T>();

    private static ServiceDescriptor Configured(Action<Knobs> configure) =>
        ServiceDescriptor.Singleton<IConfigureOptions<Knobs>>(new ConfigureNamedOptions<Knobs>(Options.DefaultName, configure));

    /// <summary>Runs <paramref name="register" /> over a collection that held <paramref name="host" />, and answers what is left.</summary>
    private static List<ServiceDescriptor> Run(ServiceDescriptor[] host, params ServiceDescriptor[] register)
    {
        IServiceCollection services = new ServiceCollection();
        foreach (ServiceDescriptor descriptor in host)
        {
            services.Add(descriptor);
        }

        ServiceDescriptor[] before = [.. services];
        foreach (ServiceDescriptor descriptor in register)
        {
            services.Add(descriptor);
        }

        ServerSideBlazorRegistration.RemoveRepeatedOptionsSetups(services, before);
        return [.. services];
    }

    [Fact]
    public void A_setup_of_the_same_implementation_the_container_held_is_removed_and_the_hosts_stays()
    {
        ServiceDescriptor hosts = Setup<KnobsSetup>();
        ServiceDescriptor repeat = Setup<KnobsSetup>();

        List<ServiceDescriptor> left = Run([hosts], repeat);

        left.Should().ContainSingle().Which.Should().BeSameAs(hosts);
    }

    [Fact]
    public void A_setup_of_another_implementation_stays()
    {
        ServiceDescriptor hosts = Setup<KnobsSetup>();
        ServiceDescriptor other = Setup<OtherKnobsSetup>();

        Run([hosts], other).Should().Equal(hosts, other);
    }

    [Fact]
    public void A_configure_of_the_same_delegate_is_removed_and_one_of_another_lambda_stays()
    {
        ServiceDescriptor hosts = Configured(Shared);
        ServiceDescriptor repeat = Configured(Shared);
        ServiceDescriptor differs = Configured(static o => o.Value = 4);

        Run([hosts], repeat, differs).Should().Equal(hosts, differs);
    }

    [Fact]
    public void A_configure_under_another_name_stays()
    {
        ServiceDescriptor hosts = Configured(Shared);
        ServiceDescriptor named = ServiceDescriptor.Singleton<IConfigureOptions<Knobs>>(new ConfigureNamedOptions<Knobs>("other", Shared));

        Run([hosts], named).Should().Equal(hosts, named);
    }

    [Fact]
    public void A_post_configure_of_the_same_delegate_is_removed()
    {
        ServiceDescriptor hosts = ServiceDescriptor.Singleton<IPostConfigureOptions<Knobs>>(new PostConfigureOptions<Knobs>(Options.DefaultName, Shared));
        ServiceDescriptor repeat = ServiceDescriptor.Singleton<IPostConfigureOptions<Knobs>>(new PostConfigureOptions<Knobs>(Options.DefaultName, Shared));

        Run([hosts], repeat).Should().ContainSingle().Which.Should().BeSameAs(hosts);
    }

    [Fact]
    public void A_factory_is_never_taken_for_a_repeat()
    {
        ServiceDescriptor hosts = ServiceDescriptor.Singleton<IConfigureOptions<Knobs>>(static _ => new KnobsSetup());
        ServiceDescriptor factory = ServiceDescriptor.Singleton<IConfigureOptions<Knobs>>(static _ => new KnobsSetup());

        Run([hosts], factory).Should().Equal(hosts, factory);
    }

    [Fact]
    public void A_service_that_is_not_an_options_setup_is_left_alone_however_it_repeats()
    {
        ServiceDescriptor hosts = ServiceDescriptor.Singleton<KnobsSetup, KnobsSetup>();
        ServiceDescriptor again = ServiceDescriptor.Singleton<KnobsSetup, KnobsSetup>();

        Run([hosts], again).Should().Equal(hosts, again);
    }

    [Fact]
    public void Nothing_the_registration_adds_is_taken_for_a_repeat_of_another_it_added()
    {
        // Two in one registration: only a descriptor the container held before it counts as "already there".
        ServiceDescriptor first = Setup<KnobsSetup>();
        ServiceDescriptor second = Setup<KnobsSetup>();

        Run([], first, second).Should().Equal(first, second);
    }

    [Fact]
    public void A_host_that_registered_the_same_setup_twice_keeps_both()
    {
        ServiceDescriptor one = Setup<KnobsSetup>();
        ServiceDescriptor two = Setup<KnobsSetup>();

        Run([one, two]).Should().Equal(one, two);
    }
}
