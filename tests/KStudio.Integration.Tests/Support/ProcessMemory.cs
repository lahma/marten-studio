using System.Diagnostics;
using System.Globalization;

namespace KStudio.Integration.Tests.Support;

/// <summary>
/// The memory the test process holds, read the way that means "held" on each OS: on Windows <c>PrivateMemorySize64</c> (the private commit);
/// on Linux <c>RssAnon</c> of <c>/proc/self/status</c>, the anonymous pages that are resident. .NET's own <c>PrivateMemorySize64</c> on Linux
/// is the <c>VmData</c> line of the same file (dotnet/runtime, <c>ProcessManager.Linux.cs</c>), the size of the data mappings whether or not a
/// page was ever touched, which counts what the allocators reserve and the process never holds.
/// </summary>
internal static class ProcessMemory
{
    private const double MiB = 1024 * 1024;

    /// <summary>The name of the figure <see cref="Reading.Private" /> is on this OS.</summary>
    public static string PrivateName => OperatingSystem.IsLinux() ? "RssAnon" : "PrivateMemorySize64";

    /// <summary>Reads the process now.</summary>
    public static Reading Read()
    {
        using Process self = Process.GetCurrentProcess();

        if (OperatingSystem.IsLinux() && ReadLinuxStatus() is { } status)
        {
            return new Reading(
                Private: Field(status, "RssAnon:"),
                ResidentSet: Field(status, "VmRSS:"),
                DotNetPrivate: self.PrivateMemorySize64);
        }

        return new Reading(Private: self.PrivateMemorySize64, ResidentSet: self.WorkingSet64, DotNetPrivate: self.PrivateMemorySize64);
    }

    private static string[]? ReadLinuxStatus()
    {
        try
        {
            return File.ReadAllLines("/proc/self/status");
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>A <c>Name:   123 kB</c> line of <c>/proc/self/status</c>, in bytes.</summary>
    private static long Field(string[] status, string name)
    {
        string line = status.First(x => x.StartsWith(name, StringComparison.Ordinal));
        string[] parts = line[name.Length..].Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        return long.Parse(parts[0], CultureInfo.InvariantCulture) * 1024;
    }

    /// <summary>One reading, in bytes.</summary>
    /// <param name="Private">The figure the ceiling is judged on (<see cref="PrivateName" />).</param>
    /// <param name="ResidentSet">The resident set (<c>VmRSS</c>, or the working set on Windows).</param>
    /// <param name="DotNetPrivate"><c>Process.PrivateMemorySize64</c> as .NET reports it.</param>
    public readonly record struct Reading(long Private, long ResidentSet, long DotNetPrivate)
    {
        /// <summary>The highest of each figure of two readings.</summary>
        public Reading Max(Reading other) =>
            new(Math.Max(Private, other.Private), Math.Max(ResidentSet, other.ResidentSet), Math.Max(DotNetPrivate, other.DotNetPrivate));

        /// <summary>This reading's growth over <paramref name="start" />, each figure in MiB.</summary>
        public string Describe(Reading start) => string.Create(
            CultureInfo.InvariantCulture,
            $"{PrivateName} +{(Private - start.Private) / MiB:F1} MiB, resident set +{(ResidentSet - start.ResidentSet) / MiB:F1} MiB, Process.PrivateMemorySize64 +{(DotNetPrivate - start.DotNetPrivate) / MiB:F1} MiB");
    }
}
