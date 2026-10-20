using PostgresStudio.Services;
using PostgresStudio.Services.Health;

namespace MartenStudio.Tests.Health;

/// <summary>
/// What the Health page, the Overview's Health region and the sidebar's badge are told, said outright by a test.
/// </summary>
/// <remarks>
/// The real <c>HealthService</c> resolves a scope against a store and sweeps it; a page test is about what the
/// page draws from a report, so the report is handed over as it is. It answers an empty, healthy report by
/// default, and records every request so a test can say what the page asked for - and when.
/// </remarks>
internal sealed class FakeHealthService : IHealthService
{
    private readonly List<HealthReportRequest> requests = [];

    /// <summary>The report <see cref="GetReportAsync" /> answers.</summary>
    public HealthReport Report { get; set; } = new()
    {
        StoreKey = "default",
        DatabaseId = "localhost.marten",
        MeasuredAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero),
        History = "Watching for under a minute (one sample): rates appear after the next check.",
    };

    /// <summary>What <see cref="GlanceAsync" /> answers.</summary>
    public HealthGlance? Glance { get; set; }

    /// <summary>What <see cref="GetReportAsync" /> throws, when a test is about a page that must survive it.</summary>
    public Exception? Failure { get; set; }

    /// <summary>Every report request, in order.</summary>
    public IReadOnlyList<HealthReportRequest> Requests => requests;

    /// <summary>How many glances the sidebar asked for.</summary>
    public int Glances { get; private set; }

    public Task<HealthReport> GetReportAsync(StudioScope scope, HealthReportRequest request, CancellationToken cancellationToken = default)
    {
        requests.Add(request);

        return Failure is not null ? Task.FromException<HealthReport>(Failure) : Task.FromResult(Report);
    }

    public Task<HealthGlance?> GlanceAsync(StudioScope scope, CancellationToken cancellationToken = default)
    {
        Glances++;
        return Task.FromResult(Glance);
    }

    /// <summary>How many banner reads were asked for, and with which codes.</summary>
    public List<IReadOnlyCollection<string>> CachedReads { get; } = [];

    /// <summary>
    /// The report's findings with these codes - what a banner is shown. Like the real one, it starts no sweep:
    /// it reads <see cref="Report" /> and nothing else, and counts nothing towards <see cref="Requests" />.
    /// </summary>
    public Task<IReadOnlyList<HealthReportFinding>> CachedFindingsAsync(
        StudioScope scope,
        IReadOnlyCollection<string> codes,
        CancellationToken cancellationToken = default)
    {
        CachedReads.Add(codes);

        return Task.FromResult<IReadOnlyList<HealthReportFinding>>(
            [.. Report.Findings.Where(x => codes.Contains(x.Code, StringComparer.Ordinal))]);
    }

    /// <summary>A report with these findings, measured now-ish, the way a sweep would hand them over.</summary>
    public FakeHealthService WithFindings(params HealthFinding[] findings)
    {
        Report = Report with
        {
            Findings =
            [
                .. findings.Select(static finding => new HealthReportFinding(
                    finding,
                    finding.Evidence,
                    HealthService.MuteKeyOf("default", "localhost.marten", finding),
                    "https://example.invalid/docs/health/" + finding.Code.ToLowerInvariant(),
                    RemedyAvailable: true,
                    HostRuns: null)),
            ],
        };

        return this;
    }

    /// <summary>A finding with the fields an evaluator fills in, and the ones a sweep stamps.</summary>
    public static HealthFinding Finding(
        string code,
        HealthSeverity severity,
        string title,
        HealthArea area = HealthArea.Configuration,
        string? subject = null) => new()
    {
        Code = code,
        Severity = severity,
        Title = title,
        WhyItMatters = "Because the test says so.",
        Subject = subject,
        Area = area,
        CheckTitle = code + " check",
        MeasuredAt = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero),
        Evidence = [new HealthEvidence("Setting", "value")],
        Remedy = new HealthRemedy("opts.Something = true;", "It makes things better."),
    };
}
