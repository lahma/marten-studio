using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace MartenStudio.Tests;

/// <summary>
/// MartenStudio's options validation, every sentence and its order, as literals: since PS-3a-fix the shared bounds come
/// from one chain both studios use (<c>StudioOptionsValidation</c>), and nothing a MartenStudio host reads may have moved.
/// </summary>
public class MartenStudioValidationMessagesTests
{
    [Fact]
    public void Every_failure_reads_and_is_ordered_exactly_as_before_the_chain_was_shared()
    {
        ServiceCollection services = new();
        services.AddMartenStudio(static options =>
        {
            options.Path = "x?y";
            options.DefaultPageSize = 0;
            options.MaxPageSize = 6000;
            options.QueryTimeout = TimeSpan.Zero;
            options.MaxInlineDocumentBytes = 0;
            options.MaxSqlConsoleRows = 0;
            options.SqlConsoleRole = "bad role";
            options.ExactCountThreshold = -1;
            options.RefreshInterval = TimeSpan.Zero;
            options.HealthInterval = TimeSpan.Zero;
            options.RebuildShardTimeout = TimeSpan.Zero;
            options.Capabilities = null!;
            options.KnownTenantIds.Add(" ");
            options.BrowsableSchemas.Add("a\"b");
        });
        using ServiceProvider provider = services.BuildServiceProvider();

        Action resolve = () => _ = provider.GetRequiredService<IOptions<MartenStudioOptions>>().Value;

        resolve.Should().Throw<OptionsValidationException>().Which.Failures.Should().Equal(
            "MartenStudioOptions.Path must start with '/'",
            "MartenStudioOptions.Path must be a simple URL path: it cannot contain '{', '}', '?', '#', '.' or '..' segments, or empty segments ('//')",
            "MartenStudioOptions.DefaultPageSize must be between 1 and MaxPageSize",
            "MartenStudioOptions.MaxPageSize must be between 1 and 5000: a larger page is a sequential scan rendered into a browser",
            "MartenStudioOptions.QueryTimeout must be between one second and ten minutes",
            "MartenStudioOptions.MaxInlineDocumentBytes must be between 1 and 64 MiB",
            "MartenStudioOptions.MaxSqlConsoleRows must be between 1 and 10000",
            "MartenStudioOptions.SqlConsoleRole must be a plain Postgres identifier: it is written into SET LOCAL ROLE, where it cannot be a parameter",
            "MartenStudioOptions.ExactCountThreshold cannot be negative",
            "MartenStudioOptions.RefreshInterval must be between one second and five minutes",
            "MartenStudioOptions.HealthInterval must be between fifteen seconds and one hour: several health checks scan the server's lock table and process array, and a page polls them no faster than this",
            "MartenStudioOptions.RebuildShardTimeout must be at least one minute: it is the per-shard replay budget, and a rebuild that exceeds it stops with the projection's tables already emptied",
            "MartenStudioOptions.Capabilities cannot be null: use new MartenStudioCapabilities() for none, or MartenStudioCapabilities.All()",
            "MartenStudioOptions.KnownTenantIds cannot contain an empty or whitespace tenant id",
            "MartenStudioOptions.BrowsableSchemas entries must each be \"*\" or a Postgres schema name: not empty or whitespace, at most 63 bytes in UTF-8, and with no double quote (\") and no NUL - a name the studio could never quote is a name no schema it can read has");
    }
}
