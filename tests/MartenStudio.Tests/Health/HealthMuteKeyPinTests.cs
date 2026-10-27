using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using Bunit;

using Microsoft.AspNetCore.DataProtection;

using PostgresStudio.Services.Health;
using PostgresStudio.Services.Health.Checks;

namespace MartenStudio.Tests.Health;

/// <summary>
/// H1, the second pass's mute leftovers: C03's variant mute keys pinned, and the reset of a pre-0.5 list proved to happen
/// exactly once - not again on the next visit, and not because a check's key moved to name a variant.
/// </summary>
public class HealthMuteKeyPinTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);

    private static CancellationToken Token => Xunit.TestContext.Current.CancellationToken;

    /// <summary>
    /// C03's findings, one per variant, and the mute key each is muted by: the key moved from <c>C03</c> to
    /// <c>C03|&lt;variant&gt;</c> when the check split (W5-API), and a key that moves again loses every browser's mute of it -
    /// so the three are pinned, as the codes table pins the variants themselves.
    /// </summary>
    [Theory]
    [InlineData(ServerConnectionHeadroomCheck.ServerVariant, "default|db|C03|server")]
    [InlineData(ServerConnectionHeadroomCheck.RoleVariant, "default|db|C03|role")]
    [InlineData(ServerConnectionHeadroomCheck.DatabaseVariant, "default|db|C03|database")]
    public void C03s_variant_mute_keys_are_pinned(string variant, string muteKey)
    {
        // 96 of 97 ordinary connections: the server is the nearest limit unless a nearer one is set.
        ServerConnectionLimits limits = new(96, 10, 100, 3, 0, 170000);

        limits = variant switch
        {
            ServerConnectionHeadroomCheck.RoleVariant => new ServerConnectionLimits(10, 5, 100, 3, 0, 170000)
            {
                LoginRole = "app",
                RoleLimit = 50,
                RoleSessions = 49,
            },
            ServerConnectionHeadroomCheck.DatabaseVariant => new ServerConnectionLimits(10, 49, 100, 3, 0, 170000) { DatabaseLimit = 50 },
            _ => limits,
        };

        HealthFinding finding = new ServerConnectionHeadroomCheck().Evaluate(limits, HealthEvaluation.At(Now)).Findings.Should().ContainSingle().Subject;

        finding.Variant.Should().Be(variant);
        finding.Key.Should().Be("C03|" + variant);
        HealthService.MuteKeyOf("default", "db", finding).Should().Be(muteKey);
        ServerConnectionHeadroomCheck.ServerVariant.Should().Be("server");
        ServerConnectionHeadroomCheck.RoleVariant.Should().Be("role");
        ServerConnectionHeadroomCheck.DatabaseVariant.Should().Be("database");
    }

    /// <summary>
    /// A browser's list from before the keyed hashes - raw keys and plain SHA-256 hashes, among them C03's pre-variant
    /// <c>default|db|C03</c> - is written over once, by the first circuit that reads it; the next circuit reads what that one
    /// wrote and writes nothing. A mute made under a key that has since moved to name a variant is an orphan hash, and an
    /// orphan is not a reason to write the list again either: it mutes nothing, stays until the bound pushes it out, and the
    /// moved key is muted afresh like any other.
    /// </summary>
    [Fact]
    public async Task A_pre_05_list_is_written_back_exactly_once_and_never_again_even_after_a_variant_moved_its_key()
    {
        HealthMuteKeys keys = new(new EphemeralDataProtectionProvider());

        const string Moved = "default|db|C03";
        const string Variant = "default|db|C03|server";

        string old = JsonSerializer.Serialize(new List<string>
        {
            Moved,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Moved))),
        });

        // 1. The first circuit over the old list: written over at once, and only once.
        (HealthMutes first, BunitContext one) = await ReadAsync(keys, old);
        string written;
        using (one)
        {
            Writes(one).Should().Be(1, "the old list is written over once, by the circuit that read it");
            written = LastWritten(one);
            first.IsMuted(Moved).Should().BeFalse("nothing is carried over");
            first.IsMuted(Variant).Should().BeFalse();
            HealthMutes.Parse(written).Muted.Should().BeEmpty();
        }

        // 2. The next circuit reads what the first wrote, and writes nothing.
        (_, BunitContext two) = await ReadAsync(keys, written);
        using (two)
        {
            Writes(two).Should().Be(0, "a list this key ring wrote is read as it is");
        }

        // 3. A mute made under the pre-variant key, by this key ring: a well-formed list whose one hash names a key no
        //    finding has any more. Read, it mutes nothing it should not and is not written back.
        (HealthMutes muting, BunitContext three) = await ReadAsync(keys, written);
        string withOrphan;
        using (three)
        {
            await muting.SetAsync(three.JSInterop.JSRuntime, Moved, muted: true, Token);
            Writes(three).Should().Be(1, "muting writes the list, once");
            withOrphan = LastWritten(three);
        }

        (HealthMutes afterMove, BunitContext four) = await ReadAsync(keys, withOrphan);
        using (four)
        {
            Writes(four).Should().Be(0, "an orphan hash - a key that has since moved to name a variant - is no reason to write the list again");
            afterMove.IsMuted(Variant).Should().BeFalse("a moved key is not muted by the hash of the key it moved from");
            afterMove.Keys.Should().ContainSingle().Which.Should().Be(afterMove.Hash(Moved), "the orphan stays until the bound pushes it out");

            // 4. The moved key muted afresh: one write, and the next circuit reads both and writes nothing.
            await afterMove.SetAsync(four.JSInterop.JSRuntime, Variant, muted: true, Token);
            Writes(four).Should().Be(1);
            withOrphan = LastWritten(four);
        }

        (HealthMutes last, BunitContext five) = await ReadAsync(keys, withOrphan);
        using (five)
        {
            Writes(five).Should().Be(0);
            last.IsMuted(Variant).Should().BeTrue();
            last.Keys.Should().HaveCount(2);
        }
    }

    private static async Task<(HealthMutes Mutes, BunitContext Browser)> ReadAsync(HealthMuteKeys keys, string? stored)
    {
        BunitContext context = new();
        context.JSInterop.Setup<string?>("martenStudio.health.getMuted").SetResult(stored);
        context.JSInterop.SetupVoid("martenStudio.health.setMuted", _ => true).SetVoidResult();

        HealthMutes mutes = new(keys);
        await mutes.LoadAsync(context.JSInterop.JSRuntime, Token);
        return (mutes, context);
    }

    private static int Writes(BunitContext context) =>
        context.JSInterop.Invocations.Count(static x => x.Identifier == "martenStudio.health.setMuted");

    private static string LastWritten(BunitContext context) =>
        (string) context.JSInterop.Invocations.Last(static x => x.Identifier == "martenStudio.health.setMuted").Arguments.Single()!;
}
