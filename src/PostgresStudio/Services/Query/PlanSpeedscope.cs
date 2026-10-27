using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PostgresStudio.Services.Query;

/// <summary>
/// A plan in <see href="https://www.speedscope.app/file-format-schema.json">speedscope's file format</see>, for the Plan page's
/// "Download for speedscope" (X3, issue #28): the plan's nodes as frames, and one sampled profile for each measure the plan has -
/// self time, self cost, the shared blocks and the temp blocks a node itself read and wrote - so a flame graph of the plan opens
/// in a viewer somebody else maintains, from a file that never left the browser.
/// </summary>
/// <remarks>
/// <para>
/// <b>One frame for each node, never shared.</b> Two identical scans are two frames, named for the node as the tree labels it
/// and for its place in the plan (<c>[#3]</c>, its index in pre-order, which is <see cref="PlanNode.Id" />), so the viewer keeps
/// them apart. A sample is the path of frame indexes from the root to a node, and its weight is that node's own value - the
/// numbers the tree and the node table already show (<see cref="PlanNode.ExclusiveMs" />, <see cref="PlanNode.ExclusiveCost" />,
/// <see cref="PlanNodeOwn" />), never a second computation of them.
/// </para>
/// <para>
/// <b>Which profiles.</b> Self time (milliseconds) only for a plan analyzed with timing; self cost always; shared blocks read
/// only when buffers were printed; temp blocks written only when printed and above zero somewhere. A profile whose weights are
/// all zero is left out. There is no link to the viewer and no endpoint: the file is saved by the browser, and the viewer is
/// opened by hand.
/// </para>
/// </remarks>
internal static class PlanSpeedscope
{
    /// <summary>The file's schema, as speedscope names it.</summary>
    public const string Schema = "https://www.speedscope.app/file-format-schema.json";

    /// <summary>The name a pasted plan, which has no statement, is exported under.</summary>
    public const string PastedName = "Pasted plan";

    /// <summary>The file name of the download.</summary>
    public const string FileName = "plan.speedscope.json";

    /// <summary>The longest a profile's name may be, in characters.</summary>
    public const int MaxNameLength = 80;

    /// <summary>The first line of <paramref name="statement" />, trimmed and cut at <see cref="MaxNameLength" />, or <see cref="PastedName" /> for none.</summary>
    public static string NameOf(string? statement)
    {
        string? line = (statement ?? string.Empty)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(static x => x.Length > 0);

        if (line is null)
        {
            return PastedName;
        }

        return line.Length <= MaxNameLength ? line : line[..MaxNameLength];
    }

    /// <summary>What the exporter field says: the studio's name and its version, <c>Postgres Studio 0.9.0</c>.</summary>
    /// <param name="productName">The studio's product name.</param>
    public static string Exporter(string productName)
    {
        ArgumentNullException.ThrowIfNull(productName);

        System.Reflection.Assembly assembly = typeof(PlanSpeedscope).Assembly;
        string? informational = assembly
            .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
            .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion;

        string version;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            int plus = informational.IndexOf('+', StringComparison.Ordinal);
            version = plus < 0 ? informational : informational[..plus];
        }
        else
        {
            version = assembly.GetName().Version?.ToString(3) ?? string.Empty;
        }

        return (productName + " " + version).Trim();
    }

    /// <summary>The plan as a speedscope file.</summary>
    /// <param name="plan">The plan.</param>
    /// <param name="name">The file's name: <see cref="NameOf" /> the statement.</param>
    /// <param name="exporter">What made the file: <see cref="Exporter" />.</param>
    public static string Build(QueryPlan plan, string name, string exporter)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(exporter);

        List<PlanNode> nodes = [.. plan.Nodes];
        List<int[]> paths = Paths(plan.Root, nodes.Count);
        PlanNodeOwn?[] own = [.. nodes.Select(static x => PlanNodeOwn.Of(x))];

        List<Profile> profiles = [];

        if (plan.Timed)
        {
            profiles.Add(new Profile("Self time", "milliseconds", [.. nodes.Select(static x => Clamp(x.ExclusiveMs))]));
        }

        profiles.Add(new Profile("Self cost", "none", [.. nodes.Select(static x => Clamp(x.ExclusiveCost))]));

        if (plan.HasBuffers)
        {
            profiles.Add(new Profile("Shared blocks read", "none", [.. own.Select(static x => Clamp(x?.SharedRead))]));
            profiles.Add(new Profile("Temp blocks written", "none", [.. own.Select(static x => Clamp(x?.TempWritten))]));
        }

        ArrayBufferWriter<byte> buffer = new();

        using (Utf8JsonWriter json = new(buffer))
        {
            json.WriteStartObject();
            json.WriteString("$schema", Schema);
            json.WriteString("name", name);
            json.WriteString("exporter", exporter);
            json.WriteNumber("activeProfileIndex", 0);

            json.WriteStartObject("shared");
            json.WriteStartArray("frames");

            foreach (PlanNode node in nodes)
            {
                json.WriteStartObject();
                json.WriteString("name", node.Description + " [#" + node.Id.ToString(CultureInfo.InvariantCulture) + "]");
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();

            json.WriteStartArray("profiles");

            foreach (Profile profile in profiles.Where(static x => x.Weights.Any(static w => w > 0)))
            {
                json.WriteStartObject();
                json.WriteString("type", "sampled");
                json.WriteString("name", profile.Name);
                json.WriteString("unit", profile.Unit);
                json.WriteNumber("startValue", 0);
                json.WriteNumber("endValue", profile.Weights.Sum());

                json.WriteStartArray("samples");

                foreach (int[] path in paths)
                {
                    json.WriteStartArray();

                    foreach (int frame in path)
                    {
                        json.WriteNumberValue(frame);
                    }

                    json.WriteEndArray();
                }

                json.WriteEndArray();

                json.WriteStartArray("weights");

                foreach (double weight in profile.Weights)
                {
                    json.WriteNumberValue(weight);
                }

                json.WriteEndArray();
                json.WriteEndObject();
            }

            json.WriteEndArray();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Each node's path of frame indexes from the root to itself, in pre-order: a node's frame index is its id.</summary>
    private static List<int[]> Paths(PlanNode root, int count)
    {
        int[][] paths = new int[count][];
        Stack<(PlanNode Node, int[] Parent)> pending = new();
        pending.Push((root, []));

        while (pending.Count > 0)
        {
            (PlanNode node, int[] parent) = pending.Pop();
            int[] path = [.. parent, node.Id];
            paths[node.Id] = path;

            for (int i = node.Children.Count - 1; i >= 0; i--)
            {
                pending.Push((node.Children[i], path));
            }
        }

        return [.. paths];
    }

    private static double Clamp(double? value) => value is { } number && double.IsFinite(number) ? Math.Max(0, number) : 0;

    private sealed record Profile(string Name, string Unit, double[] Weights);
}
