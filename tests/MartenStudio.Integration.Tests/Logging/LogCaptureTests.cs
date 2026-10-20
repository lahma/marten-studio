using MartenStudio.Services;

using PostgresStudio.Services;
using PostgresStudio.Services.Database;
using PostgresStudio.Services.Health;
using PostgresStudio.Services.Query;

namespace MartenStudio.Integration.Tests.Logging;

/// <summary>
/// The capture's filter, which needs no container of its own: it keeps both studios' categories and nobody else's.
/// </summary>
/// <remarks>
/// Worth a test because the failure is silence. After the 0.5 move every shared service logs under
/// <c>PostgresStudio.*</c>, and a filter that kept only <c>MartenStudio</c> handed those loggers a
/// <c>NullLogger</c>: every "no Warning from polling" assertion over them passed over nothing, and every assertion that
/// an event was written failed for a reason that had nothing to do with the code under test.
/// </remarks>
public class LogCaptureTests
{
    /// <summary>The categories of services that moved to the shared studio, and of one that stayed.</summary>
    private static readonly string[] Categories =
    [
        typeof(StudioActionLog).FullName!,
        typeof(TableRowService).FullName!,
        typeof(HealthSweeper).FullName!,
        typeof(SqlConsoleService).FullName!,
        typeof(MartenStoreRegistry).FullName!,
    ];

    public static TheoryData<string> StudioCategories() => new(Categories);

    [Theory]
    [MemberData(nameof(StudioCategories))]
    public void A_studio_category_is_captured(string category)
    {
        LogCapture.IsStudioCategory(category).Should().BeTrue();

        using LogCapture capture = new();
        capture.CreateLogger(category).Should().NotBeSameAs(
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance, "a captured category gets a capturing logger");
    }

    [Fact]
    public void Both_studios_categories_are_in_the_theory()
    {
        Categories.Should()
            .Contain(static x => x.StartsWith("PostgresStudio.", StringComparison.Ordinal))
            .And.Contain(static x => x.StartsWith("MartenStudio.", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Marten.Events.Daemon.ProjectionDaemon")]
    [InlineData("Npgsql.Command")]
    [InlineData("Microsoft.Hosting.Lifetime")]
    [InlineData("JasperFx.Events.Daemon.ShardAgent")]
    public void Nobody_else_s_category_is_captured(string category)
    {
        LogCapture.IsStudioCategory(category).Should().BeFalse();
    }
}
