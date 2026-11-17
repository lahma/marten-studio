using DataStudio.Marten.Services;
using DataStudio.Postgres.Services;
using DataStudio.Services.Settings;

using Microsoft.Extensions.Options;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// A read-only sentence says only what the switch that is on turns off (REVIEW-V2-D2, finding 3): <c>DataStudioOptions.ReadOnly</c> turns every
/// module's capabilities off, <c>MartenOptions.ReadOnly</c> only the Marten pages' - a Kafka host's stay on. Held for the switch alone in each
/// place, and for both together, where the shell's is the one named.
/// </summary>
public class ReadOnlyReachTests
{
    private const string Shell = "DataStudioOptions.ReadOnly";
    private const string Module = "MartenOptions.ReadOnly";

    private static MartenStudioSettings Settings(bool shell, bool module) =>
        MartenStudioSettings.Of(new MartenOptions { ReadOnly = module }, new ShellSettings(Options.Create(new DataStudioOptions { ReadOnly = shell })));

    [Theory]
    [InlineData(true, false, Shell, "every other capability.")]
    [InlineData(false, true, Module, "every other capability of the Marten pages.")]
    [InlineData(true, true, Shell, "every other capability.")]
    public void The_four_refusals_say_how_far_the_switch_that_is_on_reaches(bool shell, bool module, string name, string reach)
    {
        MartenStudioSettings settings = Settings(shell, module);
        settings.ReadOnlyOptionName.Should().Be(name, "the switch that is on is the shell's when it is on, else the module's");

        string[] sentences =
        [
            settings.Text.BrowseReadOnlyDenial(settings.ReadOnlyOptionName),
            settings.Text.DefinitionReadOnlyDenial(settings.ReadOnlyOptionName),
            settings.Text.SessionsReadOnlyDenial(settings.ReadOnlyOptionName),
            settings.Text.StatementsReadOnlyDenial(settings.ReadOnlyOptionName),
        ];

        sentences.Should().OnlyContain(x => x.StartsWith(name + " is true, which turns ", StringComparison.Ordinal));
        sentences.Should().OnlyContain(x => x.EndsWith(" off with " + reach, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, false, "every capability")]
    [InlineData(false, true, "every capability of the Marten pages")]
    [InlineData(true, true, "every capability")]
    public void What_the_switch_turns_off_is_said_from_the_same_source_as_its_name(bool shell, bool module, string turnedOff)
    {
        MartenStudioSettings settings = Settings(shell, module);

        settings.Text.EveryCapabilityOff(settings.ReadOnlyOptionName).Should().Be(turnedOff);
        new StudioCapabilityGuard(settings).EveryCapabilityOff.Should().Be(turnedOff);
    }

    [Theory]
    [InlineData(true, false, "which turns every capability off.")]
    [InlineData(false, true, "which turns every capability of the Marten pages off.")]
    public void The_refusal_a_service_throws_says_it_too(bool shell, bool module, string said)
    {
        MartenStudioSettings settings = Settings(shell, module);

        Action act = () => new StudioCapabilityGuard(settings).Require(StudioCapability.RunSql);

        act.Should().Throw<StudioCapabilityDeniedException>().Which.Message.Should().EndWith(said);
    }
}
