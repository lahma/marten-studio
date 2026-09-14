namespace MartenStudio.Sample;

/// <summary>
/// The switches the demo host understands, read from the command line it was started with.
/// </summary>
/// <param name="Anonymous">
/// <c>--anonymous</c>: map the studio with <c>AllowAnonymous()</c> instead of a policy, to show what an
/// unauthenticated studio looks like. Never do this anywhere real.
/// </param>
/// <param name="ReadOnly">
/// <c>--readonly</c>: set <see cref="MartenStudioOptions.ReadOnly" />, which turns every mutating
/// capability off however they were configured.
/// </param>
/// <param name="Path">
/// <c>--path /ops/marten</c>: mount the studio somewhere other than <c>/marten</c>, which is what
/// exercises the sub-path re-rooting.
/// </param>
internal sealed record SampleOptions(bool Anonymous, bool ReadOnly, string? Path)
{
    /// <summary>Reads the switches out of <paramref name="args" />, ignoring anything else.</summary>
    public static SampleOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool anonymous = false;
        bool readOnly = false;
        string? path = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--anonymous":
                    anonymous = true;
                    break;
                case "--readonly":
                    readOnly = true;
                    break;
                case "--path" when i + 1 < args.Length:
                    path = args[++i];
                    break;
                default:
                    break;
            }
        }

        return new SampleOptions(anonymous, readOnly, path);
    }
}
