using System.Runtime.CompilerServices;

namespace DataStudio.Kafka.Tests;

/// <summary>How this project's Verify baselines are written: UTF-8 without a byte-order mark, as DataStudio.Marten.Tests' are.</summary>
internal static class VerifyConfiguration
{
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
