using System.Reflection;
using System.Reflection.Emit;

using DataStudio.Marten.Internal;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace DataStudio.Marten.Tests.Services;

/// <summary>
/// <c>AddMarten</c> and <c>AddMartenDiagnostics</c> refuse Marten 10 and later with one sentence that names the way out (#103). The refusal is driven through
/// the guard's seam, because this test host runs the one Marten the build restored, which is 9.
/// </summary>
public class MartenVersionGuardTests
{
    private const string Own = "2.0.2";

    private static readonly Dictionary<short, OpCode> ByValue = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Select(static field => (OpCode) field.GetValue(null)!)
        .GroupBy(static code => code.Value)
        .ToDictionary(static group => group.Key, static group => group.First());

    private static readonly MethodInfo PostgresEnsure = typeof(PostgresVersionGuard).GetMethod(nameof(PostgresVersionGuard.Ensure), Type.EmptyTypes)!;

    private static readonly MethodInfo MartenEnsure = typeof(MartenVersionGuard).GetMethod(nameof(MartenVersionGuard.Ensure), Type.EmptyTypes)!;

    [Fact]
    public void The_Marten_this_build_restored_passes_and_so_do_both_registrations()
    {
        typeof(global::Marten.IDocumentStore).Assembly.GetName().Version!.Major.Should().BeLessThan(MartenVersionGuard.FirstUnsupportedMajor);

        FluentActions.Invoking(MartenVersionGuard.Ensure).Should().NotThrow();
        FluentActions.Invoking(() => new ServiceCollection().AddDataStudio().AddMarten()).Should().NotThrow();
    }

    [Theory]
    [InlineData("9.57.0.0")]
    [InlineData("9.99.9.0")]
    [InlineData("9.0.0.0")]
    [InlineData("8.0.0.0")]
    public void Marten_9_and_below_pass(string loaded)
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(Version.Parse(loaded), Own)).Should().NotThrow();
    }

    [Fact]
    public void An_assembly_with_no_version_is_not_refused()
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(null, Own)).Should().NotThrow();
    }

    [Theory]
    [InlineData("10.0.0.0", "10.0.0")]
    [InlineData("10.4.2.0", "10.4.2")]
    [InlineData("11.0.0.0", "11.0.0")]
    public void Marten_10_and_later_are_refused_with_the_sentence(string loaded, string shown)
    {
        FluentActions.Invoking(() => MartenVersionGuard.Ensure(Version.Parse(loaded), Own))
            .Should().Throw<InvalidOperationException>()
            .WithMessage($"Data Studio {Own} supports Marten 9, and this application uses Marten {shown}. Marten 10 and later need Data Studio 3.0 or later.");
    }

    [Fact]
    public void The_refusal_names_the_studios_own_release_version_without_its_commit()
    {
        string own = PostgresVersionGuard.ReleaseVersion(typeof(MartenVersionGuard).Assembly);

        own.Should().NotContain("+").And.NotBe("?");
        MartenVersionGuard.Refusal(new Version(10, 0, 0, 0), own).Should().StartWith($"Data Studio {own} supports Marten 9");
    }

    /// <summary>
    /// Both public registrations - <c>AddMarten</c> and <c>AddMartenDiagnostics</c> - call <c>MartenVersionGuard.Ensure()</c> immediately after
    /// <c>PostgresVersionGuard.Ensure()</c> and before anything else is done, read from their IL. The seam tests above prove what the guard says; only this proves a host
    /// on Marten 10 is ever told, because a registration that stopped calling it would break nothing in a test host that runs Marten 9 (#103).
    /// </summary>
    [Theory]
    [InlineData(nameof(MartenDataStudioBuilderExtensions.AddMarten))]
    [InlineData(nameof(MartenHealthChecksBuilderExtensions.AddMartenDiagnostics))]
    public void Both_registrations_call_the_Marten_guard_straight_after_the_Postgres_guard(string registration)
    {
        MethodBase method = registration == nameof(MartenDataStudioBuilderExtensions.AddMarten)
            ? typeof(MartenDataStudioBuilderExtensions).GetMethod(registration, [typeof(DataStudioBuilder), typeof(Action<MartenOptions>)])!
            : typeof(MartenHealthChecksBuilderExtensions).GetMethod(
                registration, [typeof(IHealthChecksBuilder), typeof(string), typeof(HealthStatus?), typeof(IEnumerable<string>), typeof(TimeSpan?)])!;

        List<MethodBase> calls = CallsOf(method);
        int postgres = calls.IndexOf(PostgresEnsure);

        postgres.Should().BeGreaterThanOrEqualTo(0, $"{registration} calls PostgresVersionGuard.Ensure()");
        calls.Count(static x => x == PostgresEnsure).Should().Be(1);
        calls.Count(static x => x == MartenEnsure).Should().Be(1, $"{registration} calls MartenVersionGuard.Ensure() exactly once");
        calls[postgres + 1].Should().Be(MartenEnsure, "the Marten guard is the very next call after the Postgres guard");
    }

    /// <summary>Anti-vacuity: the reader sees the calls it is meant to, and a method without the guard call fails the same assertion.</summary>
    [Fact]
    public void The_call_reader_sees_the_guard_calls_and_a_registration_without_one_would_fail_the_test_above()
    {
        MethodInfo withoutMartenGuard = typeof(MartenVersionGuardTests).GetMethod(nameof(RegistrationThatForgotTheMartenGuard), BindingFlags.NonPublic | BindingFlags.Static)!;
        MethodInfo withoutAnyGuard = typeof(MartenVersionGuardTests).GetMethod(nameof(RegistrationThatForgotBothGuards), BindingFlags.NonPublic | BindingFlags.Static)!;

        CallsOf(withoutMartenGuard).Should().Contain(PostgresEnsure).And.NotContain(MartenEnsure);
        CallsOf(withoutAnyGuard).Should().NotContain(PostgresEnsure).And.NotContain(MartenEnsure);

        List<MethodBase> calls = CallsOf(withoutMartenGuard);

        calls[calls.IndexOf(PostgresEnsure) + 1].Should().NotBe(MartenEnsure, "so the adjacency assertion refuses it");
    }

    private static void RegistrationThatForgotTheMartenGuard()
    {
        PostgresVersionGuard.Ensure();
        GC.KeepAlive(null);
    }

    private static void RegistrationThatForgotBothGuards() => GC.KeepAlive(null);

    /// <summary>The methods a method body calls (<c>call</c>, <c>callvirt</c>, <c>newobj</c>), in the order its IL does.</summary>
    private static List<MethodBase> CallsOf(MethodBase method)
    {
        byte[] il = method.GetMethodBody()!.GetILAsByteArray()!;
        List<MethodBase> calls = [];
        int position = 0;

        while (position < il.Length)
        {
            short value = il[position] == 0xFE && position + 1 < il.Length
                ? unchecked((short) (0xFE00 | il[++position]))
                : il[position];
            position++;

            OpCode code = ByValue[value];

            if (code == OpCodes.Call || code == OpCodes.Callvirt || code == OpCodes.Newobj)
            {
                calls.Add((MethodBase) method.Module.ResolveMember(BitConverter.ToInt32(il, position))!);
            }

            position += code.OperandType switch
            {
                OperandType.InlineNone => 0,
                OperandType.ShortInlineBrTarget or OperandType.ShortInlineI or OperandType.ShortInlineVar => 1,
                OperandType.InlineVar => 2,
                OperandType.InlineI8 or OperandType.InlineR => 8,
                OperandType.InlineSwitch => 4 + (4 * BitConverter.ToInt32(il, position)),
                _ => 4,
            };
        }

        return calls;
    }
}
