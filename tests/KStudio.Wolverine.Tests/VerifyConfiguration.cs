using System.Runtime.CompilerServices;

namespace KStudioWolverine.Tests;

/// <summary>How this project's Verify baselines are written: UTF-8 without a byte-order mark, as the other suites' are.</summary>
internal static class VerifyConfiguration
{
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
