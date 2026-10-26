using System.Text.RegularExpressions;

namespace MartenStudio.Tests.Conventions;

/// <summary>
/// Wave 6 (SEC2): no plain SHA-256 of a mute key remains. A mute key is hashed only as an HMAC under the browser's own key
/// (<c>HealthMuteKeys.Hash</c>), because a plain hash lets anybody who reads <c>localStorage</c> confirm a guessed key - so
/// every plain SHA-256 in the shipped source is one this test names, with what it hashes and why that is not a mute key.
/// </summary>
public partial class MuteKeyHashTests
{
    /// <summary>The plain SHA-256s the studio keeps, each with what it hashes.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        ["src/PostgresStudio/Internal/StudioEndpointMapper.cs"] = "a packaged static asset's content, for its ETag",
        ["src/PostgresStudio/Services/Health/Checks/HorizonHeldCheck.cs"] = "a horizon holder's identity, for a series name the server keeps in memory",
    };

    [Fact]
    public void No_plain_SHA256_hashes_a_mute_key()
    {
        string[] files = SourceScanner.ShippedSourceFiles();
        files.Should().NotBeEmpty("a scan of nothing passes, and proves nothing");

        List<string> offenders = [];

        foreach (string file in files)
        {
            string code = SourceScanner.StripCommentsAndStrings(File.ReadAllText(file));
            string relative = SourceScanner.Relative(file);

            if (PlainSha256().IsMatch(code) && !Allowed.ContainsKey(relative))
            {
                offenders.Add(relative + (MentionsAMuteKey(code) ? " (hashes a mute key)" : " (a plain SHA-256 nobody named)"));
            }
        }

        offenders.Should().BeEmpty("a mute key is hashed only as an HMAC under the browser's key; name any other plain SHA-256 here, with what it hashes");

        string mutes = SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine("src", "PostgresStudio", "Services", "Health", "HealthMutes.cs")));
        PlainSha256().IsMatch(mutes).Should().BeFalse("HealthMutes hashes with HMAC-SHA256 only");
        mutes.Should().Contain("HMACSHA256.HashData", "and does hash");

        foreach ((string path, string why) in Allowed)
        {
            File.Exists(RepositoryRoot.Combine(path.Split('/'))).Should().BeTrue(path + " is allowed for " + why + ", so it must exist");
            MentionsAMuteKey(SourceScanner.StripCommentsAndStrings(File.ReadAllText(RepositoryRoot.Combine(path.Split('/'))))).Should()
                .BeFalse(path + " hashes " + why + ", never a mute key");
        }
    }

    /// <summary>The scanner's anti-vacuity half: it catches a planted plain SHA-256 of a mute key and passes an HMAC.</summary>
    [Theory]
    [InlineData("string h = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(muteKey)));", true)]
    [InlineData("using var sha = System.Security.Cryptography.SHA256.Create();", true)]
    [InlineData("byte[] h = SHA256.HashData(bytes);", true)]
    [InlineData("string h = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(muteKey)));", false)]
    [InlineData("// SHA256.HashData(muteKey) is what 0.4 did", false)]
    public void The_scan_catches_a_plain_SHA256_and_passes_an_HMAC(string source, bool caught)
    {
        PlainSha256().IsMatch(SourceScanner.StripCommentsAndStrings(source)).Should().Be(caught);
    }

    private static bool MentionsAMuteKey(string code) => code.Contains("muteKey", StringComparison.OrdinalIgnoreCase);

    /// <summary>A call on <c>SHA256</c> itself - <c>HashData</c>, <c>HashDataAsync</c>, <c>Create</c> - never on <c>HMACSHA256</c>.</summary>
    [GeneratedRegex(@"(?<![A-Za-z0-9_])SHA256\s*\.\s*(HashData|HashDataAsync|Create|TryHashData)\b", RegexOptions.CultureInvariant)]
    private static partial Regex PlainSha256();
}
