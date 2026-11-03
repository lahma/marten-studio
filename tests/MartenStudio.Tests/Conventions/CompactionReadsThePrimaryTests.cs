using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// CMP-FIX 2: the fold after a compaction's save classifies the outcome (left alone, or a failure), so it must read the host the
/// write ran on - <c>ConnectionUsage.ReadWrite</c> - as the stream's own read-back does, and not a standby that may not have
/// caught up. The pre-click fold is a page read and keeps the default.
/// </summary>
public class CompactionReadsThePrimaryTests
{
    private const string Source = "src/MartenStudio/Services/Events/EventDataService.Compaction.cs";

    private static readonly Regex FoldCall = new(@"FoldThroughAsync\s*\((?<args>[^;]*?)\)\s*\.ConfigureAwait", RegexOptions.Singleline);

    [Fact]
    public void The_fold_after_the_save_asks_for_ReadWrite_and_the_one_before_the_click_does_not()
    {
        string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(Source.Split('/'))));

        List<string> calls = [.. FoldCall.Matches(code).Select(static x => Regex.Replace(x.Groups["args"].Value, @"\s+", " ").Trim())];

        calls.Should().HaveCount(2, "the dialog's fold and the one after the save are the only callers");
        calls.Count(static x => x.Contains("ConnectionUsage.ReadWrite", StringComparison.Ordinal)).Should().Be(1);

        string afterTheSave = calls.Single(static x => x.Contains("expectedVersion", StringComparison.Ordinal));
        afterTheSave.Should().Contain("ConnectionUsage.ReadWrite", "the post-save fold must classify from the host the write ran on");

        calls.Single(static x => !x.Contains("expectedVersion", StringComparison.Ordinal))
            .Should().NotContain("ConnectionUsage", "a page read follows ReadSessionPreference");
    }

    [Fact]
    public void The_stream_read_back_after_the_save_is_on_the_primary_too()
    {
        string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(Source.Split('/'))));

        Regex.Matches(code, @"ReadStreamOnPrimaryAsync\s*\(").Should().HaveCountGreaterThanOrEqualTo(2);
        code.Should().NotContain("ReadStreamAsync(", "every stream read of a compaction goes through ReadStreamOnPrimaryAsync");
    }
}
