namespace KStudio.Integration.Tests;

public class DockerAvailabilityTests
{
    [Fact]
    public void The_skip_reason_names_the_command_that_runs_this_suite()
    {
        DockerAvailability.SkipReason.Should().Contain("dotnet test tests/KStudio.Integration.Tests");
    }
}
