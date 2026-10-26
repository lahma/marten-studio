using Microsoft.Extensions.DependencyInjection;

using PostgresStudio;
using PostgresStudio.Services.Settings;

namespace MartenStudio.Tests.Services;

/// <summary>
/// Design PS8, option (a): one studio per application, with the real registration methods in both orders. This is the
/// one project that references both packages, so it is where the pair is proved; PostgresStudio.Tests proves the
/// refusal against a claimed marker.
/// </summary>
public class OneStudioPerApplicationTests
{
    [Fact]
    public void AddPostgresStudio_after_AddMartenStudio_is_refused_and_says_why()
    {
        ServiceCollection services = new();
        services.AddMartenStudio();

        Action add = () => services.AddPostgresStudio();

        add.Should().Throw<InvalidOperationException>().WithMessage(
            "AddPostgresStudio() was called in an application that already registered Marten Studio with AddMartenStudio(). "
            + "One studio per application: Marten Studio and Postgres Studio share their services, so a second one would answer "
            + "for the first. Register one of them.");

        services.Single(static x => x.ServiceType == typeof(StudioDescriptor)).ImplementationInstance
            .Should().BeSameAs(MartenStudio.Services.MartenStudioDescriptor.Instance, "nothing of the refused studio was registered");
    }

    [Fact]
    public void AddMartenStudio_after_AddPostgresStudio_is_refused_and_says_why()
    {
        ServiceCollection services = new();
        services.AddPostgresStudio();

        Action add = () => services.AddMartenStudio();

        add.Should().Throw<InvalidOperationException>().WithMessage(
            "AddMartenStudio() was called in an application that already registered Postgres Studio with AddPostgresStudio(). "
            + "One studio per application: Postgres Studio and Marten Studio share their services, so a second one would answer "
            + "for the first. Register one of them.");
    }
}
