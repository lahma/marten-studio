using Microsoft.Extensions.DependencyInjection;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// Design PS8, option (a): one database module per application, with the real registration methods in both orders. This is the
/// one project that references both packages, so it is where the pair is proved; DataStudio.Postgres.Tests proves the
/// refusal against a claimed marker.
/// </summary>
public class OneStudioPerApplicationTests
{
    [Fact]
    public void AddPostgres_after_AddMarten_is_refused_and_says_why()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddMarten();

        Action add = () => services.AddDataStudio().AddPostgres();

        add.Should().Throw<InvalidOperationException>().WithMessage(
            "AddPostgres() was called in an application that already registered the Marten module with AddMarten(). "
            + "One database module per application: the Marten module and the Postgres module share their services, so a second one would answer "
            + "for the first. Add one of them.");

        // A factory since D34 (the composition with the registered modules); with no module it answers the very instance.
        using ServiceProvider provider = services.BuildServiceProvider();
        provider.GetRequiredService<StudioDescriptor>()
            .Should().BeSameAs(DataStudio.Marten.Services.MartenDescriptor.Instance, "nothing of the refused studio was registered");
    }

    [Fact]
    public void AddMarten_after_AddPostgres_is_refused_and_says_why()
    {
        ServiceCollection services = new();
        services.AddDataStudio().AddPostgres();

        Action add = () => services.AddDataStudio().AddMarten();

        add.Should().Throw<InvalidOperationException>().WithMessage(
            "AddMarten() was called in an application that already registered the Postgres module with AddPostgres(). "
            + "One database module per application: the Postgres module and the Marten module share their services, so a second one would answer "
            + "for the first. Add one of them.");
    }
}
