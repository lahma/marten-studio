namespace MartenStudio.Sample;

/// <summary>
/// The switches the demo host understands, read from the command line it was started with.
/// </summary>
/// <remarks>
/// <para>
/// <b>A bare boolean switch is true and consumes nothing.</b> <c>--anonymous</c>, <c>--readonly</c> and
/// <c>--allow-data-generation</c> are switches, not <c>--key value</c> pairs: writing one means
/// <see langword="true" /> and the next token is left entirely alone, so
/// <c>dotnet run -- --anonymous --urls http://localhost:5000</c> starts an anonymous studio on port 5000
/// and the order of the two does not matter. An explicit value is still accepted where a script wants to
/// pass a variable — <c>--readonly true</c>, <c>--readonly false</c> and <c>--readonly=false</c> all
/// work — and <em>only</em> a literal <c>true</c> or <c>false</c> is taken as one. <c>--path</c> is a
/// pair and does take the token after it.
/// </para>
/// <para>
/// <b>This is not how .NET's own command-line configuration provider reads arguments</b>, which is the
/// whole reason the rule is written down. <c>WebApplication.CreateBuilder(args)</c> adds
/// <c>CommandLineConfigurationProvider</c>, and that provider treats every <c>--key</c> without an
/// <c>=</c> as the first half of a pair and swallows whatever follows it. So <c>--anonymous --urls
/// http://localhost:5000</c> bound <c>anonymous</c> to the string <c>"--urls"</c> and then dropped the
/// orphaned <c>http://localhost:5000</c> entirely — its <c>Load()</c> skips any token with no <c>-</c>,
/// <c>--</c> or <c>/</c> prefix (read off the 10.0.12 assembly; an older .NET threw a
/// <c>FormatException</c> there, and this one does not). Nothing warned, nothing threw, and Kestrel
/// listened on the default port while the person who typed the command watched the wrong URL.
/// <see cref="HostArguments" /> is what reconciles the two: it hands the builder the same command line
/// with this record's own switches rewritten to the <c>--key=value</c> form the provider parses without
/// consuming a neighbour, and every other argument — <c>--urls</c>, <c>--environment</c>, anything a host
/// understands — passed through untouched, in order.
/// </para>
/// </remarks>
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
/// <param name="AllowDataGeneration">
/// <c>--allow-data-generation</c>: map the demo-data panel and its endpoints even outside Development.
/// Generating a million documents is a write, and a long one, so outside a developer's own machine it
/// has to be asked for on the command line rather than implied by being signed in as an admin.
/// </param>
internal sealed record SampleOptions(bool Anonymous, bool ReadOnly, string? Path, bool AllowDataGeneration)
{
    /// <summary>The boolean switches, in the spelling the command line uses.</summary>
    private const string AnonymousSwitch = "--anonymous";
    private const string ReadOnlySwitch = "--readonly";
    private const string AllowDataGenerationSwitch = "--allow-data-generation";

    /// <summary>The one switch that really is a <c>--key value</c> pair.</summary>
    private const string PathSwitch = "--path";

    /// <summary>
    /// Whether the demo-data endpoints are mapped at all.
    /// </summary>
    /// <param name="environment">The host environment.</param>
    /// <remarks>
    /// Development or the explicit switch, and nothing else. A production host that never passed the
    /// switch does not have the endpoints, so the answer to a POST is 404 rather than 403 - there is
    /// nothing there to be refused.
    /// </remarks>
    public bool DataGenerationEnabled(IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(environment);

        return environment.IsDevelopment() || AllowDataGeneration;
    }

    /// <summary>Reads the switches out of <paramref name="args" />, ignoring anything else.</summary>
    public static SampleOptions Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool anonymous = false;
        bool readOnly = false;
        string? path = null;
        bool allowDataGeneration = false;

        foreach (Argument argument in Read(args))
        {
            switch (argument.Name)
            {
                case AnonymousSwitch:
                    anonymous = argument.Flag;
                    break;
                case ReadOnlySwitch:
                    readOnly = argument.Flag;
                    break;
                case AllowDataGenerationSwitch:
                    allowDataGeneration = argument.Flag;
                    break;
                case PathSwitch when argument.Value is { Length: > 0 } value:
                    path = value;
                    break;
                default:
                    break;
            }
        }

        return new SampleOptions(anonymous, readOnly, path, allowDataGeneration);
    }

    /// <summary>
    /// The command line to hand to <c>WebApplication.CreateBuilder</c>.
    /// </summary>
    /// <remarks>
    /// Every switch this record understands comes back in the <c>--key=value</c> form, which .NET's
    /// command-line configuration provider reads without consuming the token after it; everything else is
    /// passed through verbatim and in order, so <c>--urls</c>, <c>--environment</c> and any other host
    /// argument keep working wherever they are written. See the note on the type for what goes wrong
    /// without this.
    /// </remarks>
    public static string[] HostArguments(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        List<string> host = new(args.Length);

        foreach (Argument argument in Read(args))
        {
            if (argument.Name is null)
            {
                host.Add(argument.Token);
                continue;
            }

            if (argument.Name == PathSwitch)
            {
                // A --path with nothing after it is dropped rather than passed on: it is this record's
                // switch, and left in place the provider would swallow whatever came next.
                if (argument.Value is { Length: > 0 } value)
                {
                    host.Add(PathSwitch + "=" + value);
                }

                continue;
            }

            host.Add(argument.Name + "=" + (argument.Flag ? "true" : "false"));
        }

        return [.. host];
    }

    /// <summary>One token of the command line, and what this record made of it.</summary>
    /// <param name="Token">The token verbatim, which is what an unrecognised argument is passed on as.</param>
    /// <param name="Name">The switch it named, or <see langword="null" /> for anything not ours.</param>
    /// <param name="Flag">A boolean switch's value.</param>
    /// <param name="Value"><c>--path</c>'s value.</param>
    private readonly record struct Argument(string Token, string? Name, bool Flag, string? Value);

    /// <summary>
    /// Walks the command line once, recognising this record's switches and leaving everything else alone.
    /// </summary>
    /// <remarks>
    /// The single place the "present means true, and only a literal true/false is a value" rule is
    /// written, so <see cref="Parse" /> and <see cref="HostArguments" /> cannot disagree about which
    /// tokens belong to this record and which belong to the host.
    /// </remarks>
    private static IEnumerable<Argument> Read(string[] args)
    {
        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            string name = token;
            string? inline = null;

            int equals = token.IndexOf('=');
            if (equals > 0)
            {
                name = token[..equals];
                inline = token[(equals + 1)..];
            }

            switch (name)
            {
                case AnonymousSwitch:
                case ReadOnlySwitch:
                case AllowDataGenerationSwitch:
                    if (inline is not null)
                    {
                        yield return new Argument(token, name, IsTrue(inline), null);
                        break;
                    }

                    // The next token is consumed only when it is literally "true" or "false". Anything
                    // else - another switch, a URL, a path - belongs to whoever else is reading this
                    // command line.
                    if (i + 1 < args.Length && IsBoolean(args[i + 1]))
                    {
                        yield return new Argument(token, name, IsTrue(args[i + 1]), null);
                        i++;
                        break;
                    }

                    yield return new Argument(token, name, true, null);
                    break;

                case PathSwitch:
                    if (inline is not null)
                    {
                        yield return new Argument(token, name, false, inline);
                        break;
                    }

                    if (i + 1 < args.Length)
                    {
                        yield return new Argument(token, name, false, args[i + 1]);
                        i++;
                        break;
                    }

                    yield return new Argument(token, name, false, null);
                    break;

                default:
                    yield return new Argument(token, null, false, null);
                    break;
            }
        }
    }

    private static bool IsBoolean(string token) =>
        token.Equals("true", StringComparison.OrdinalIgnoreCase) ||
        token.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string token) => token.Equals("true", StringComparison.OrdinalIgnoreCase);
}
