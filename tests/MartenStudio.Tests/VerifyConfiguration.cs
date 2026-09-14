using System.Runtime.CompilerServices;

namespace MartenStudio.Tests;

/// <summary>
/// How this project's Verify baselines are written.
/// </summary>
/// <remarks>
/// UTF-8 without a byte-order mark. Verify's default writes one, and a BOM in a
/// <c>.verified.txt</c> is three bytes of noise in a file whose whole job is to be read as a diff: it
/// shows up as <c>ï»¿</c> or an invisible character at the head of the first line in half the tools that
/// open it, and <c>.gitattributes</c> already pins these files to UTF-8 and LF so nothing needs it.
/// </remarks>
internal static class VerifyConfiguration
{
    [ModuleInitializer]
    internal static void Initialize() => VerifierSettings.UseUtf8NoBom();
}
