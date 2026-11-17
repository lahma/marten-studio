using System.Runtime.CompilerServices;

using DataStudio.Marten.Services;

namespace DataStudio.Marten.Tests.Support;

/// <summary>
/// Puts the Marten module's words into the database fake this suite shares with the Postgres and Kafka suites
/// (<see cref="FakeDatabaseObjects" />), which refuse in the Postgres module's words wherever they are linked and nothing says otherwise.
/// </summary>
internal static class MartenFakeWords
{
    /// <summary>Runs once, when the suite's assembly loads.</summary>
#pragma warning disable CA2255 // A test assembly is application code: this is the one place the suite picks the words its fakes speak.
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Use() => FakeDatabaseObjects.Words = MartenText.Instance;
}
