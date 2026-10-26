namespace PostgresStudio.Sample;

/// <summary>
/// The one switch this sample reads from its command line, <c>--allow-data-generation</c>, and the command line the host
/// builder is handed.
/// </summary>
/// <remarks>
/// <para>
/// <b>What the switch allows.</b> The sample writes its demo data - three tenant databases created beside the admin database
/// (which needs <c>CREATEDB</c>), the admin database's directory of them, and the <c>quartz</c>, <c>legacy</c> and
/// <c>studio_sample</c> schemas - only on the throwaway container it started itself
/// (<see cref="EphemeralPostgres.IsThrowaway" />), or on a configured database when this switch says outright that the server is
/// the demo's to write to. Anywhere else it writes nothing and says so in one line: Postgres Studio itself never writes, so
/// the sample is then as read-only as the studio is. The same rule, and the same switch, as the Marten sample's.
/// </para>
/// <para>
/// <b>Read as the Marten sample reads its switches</b> (its <c>SampleOptions</c>): present means <see langword="true" /> and
/// consumes nothing; a literal <c>true</c> or <c>false</c> after it, or after an <c>=</c>, is its value, in any casing; any
/// other <c>=</c> value is refused rather than read as <see langword="false" />; and a leading <c>/</c> reads as <c>--</c>.
/// </para>
/// <para>
/// <b>The host builder never sees the bare switch.</b> .NET's command-line configuration provider reads a <c>--key</c> without
/// an <c>=</c> as the first half of a pair and swallows the token after it, so <c>--allow-data-generation --urls
/// http://localhost:5211</c> would bind the switch to <c>--urls</c> and drop the URL without a word. <see cref="HostArguments" />
/// is the same command line with the switch rewritten to <c>--allow-data-generation=true</c> (or <c>=false</c>), and every
/// other argument passed through untouched, in order.
/// </para>
/// </remarks>
/// <param name="AllowDataGeneration">Whether <c>--allow-data-generation</c> was given (and not given as <c>false</c>).</param>
/// <param name="HostArguments">The command line for <c>WebApplication.CreateBuilder</c>.</param>
internal sealed record SampleArguments(bool AllowDataGeneration, IReadOnlyList<string> HostArguments)
{
    /// <summary>The switch, in the spelling the command line uses.</summary>
    public const string AllowDataGenerationSwitch = "--allow-data-generation";

    /// <summary>The one line the sample prints when it starts on a configured database without the switch.</summary>
    public const string ReadOnlyNotice =
        "Postgres Studio sample: read-only - ConnectionStrings:Postgres names a database this sample did not start, so it "
        + "creates no tenant databases and no demo schemas there; start it with " + AllowDataGenerationSwitch + " to write them.";

    /// <summary>Reads the switch out of <paramref name="args" />, and rewrites it for the host builder.</summary>
    /// <param name="args">The process's command line.</param>
    /// <exception cref="ArgumentException">The switch was given an <c>=</c> value that is not <c>true</c> or <c>false</c>.</exception>
    public static SampleArguments Parse(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        bool allow = false;
        List<string> host = new(args.Length);

        for (int i = 0; i < args.Length; i++)
        {
            string token = args[i];
            int equals = token.IndexOf('=', StringComparison.Ordinal);
            string name = equals > 0 ? token[..equals] : token;

            if (!IsSwitch(name))
            {
                host.Add(token);
                continue;
            }

            if (equals > 0)
            {
                string inline = token[(equals + 1)..];
                if (!IsBoolean(inline))
                {
                    throw new ArgumentException(
                        "'" + token + "' is not a value this sample understands. " + AllowDataGenerationSwitch
                        + " takes true or false (any casing), or nothing at all, which means true.",
                        nameof(args));
                }

                allow = IsTrue(inline);
            }
            else if (i + 1 < args.Length && IsBoolean(args[i + 1]))
            {
                allow = IsTrue(args[++i]);
            }
            else
            {
                allow = true;
            }

            host.Add(AllowDataGenerationSwitch + "=" + (allow ? "true" : "false"));
        }

        return new SampleArguments(allow, [.. host]);
    }

    /// <summary>Whether <paramref name="name" /> spells the switch: any casing, and a leading <c>/</c> read as <c>--</c>.</summary>
    private static bool IsSwitch(string name) =>
        string.Equals(name.StartsWith('/') ? "--" + name[1..] : name, AllowDataGenerationSwitch, StringComparison.OrdinalIgnoreCase);

    private static bool IsBoolean(string token) =>
        token.Equals("true", StringComparison.OrdinalIgnoreCase) || token.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static bool IsTrue(string token) => token.Equals("true", StringComparison.OrdinalIgnoreCase);
}
