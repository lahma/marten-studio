using System.Text.Json;

namespace MartenStudio.Services.Shapes;

/// <summary>
/// Counts what a check found, per concrete type: per (kind, path) a count and the first example in key order, and failures
/// grouped by (kind, exception type, path) (D-SHAPE S6).
/// </summary>
/// <remarks>
/// Holds no value of any item: its inputs are <see cref="ShapeMember" /> and <see cref="ShapeFailure" />, which have no field that
/// can carry one. Not thread-safe; a check owns one.
/// </remarks>
internal sealed class ShapeAggregator
{
    private readonly Dictionary<string, TypeBucket> types = new(StringComparer.Ordinal);

    /// <summary>Records one examined item.</summary>
    /// <param name="typeName">The concrete type's short name.</param>
    /// <param name="fullTypeName">The concrete type's full name.</param>
    /// <param name="example">Where the item is.</param>
    /// <param name="failure">Why it could not be checked, or <see langword="null" />.</param>
    /// <param name="members">What it disagrees with its round trip about.</param>
    public void Add(
        string typeName,
        string? fullTypeName,
        ShapeItemKey example,
        ShapeFailure? failure,
        IReadOnlyList<ShapeMember> members)
    {
        ArgumentNullException.ThrowIfNull(typeName);
        ArgumentNullException.ThrowIfNull(example);
        ArgumentNullException.ThrowIfNull(members);

        TypeBucket bucket = Bucket(typeName, fullTypeName);
        bucket.Examined++;

        if (failure is not null)
        {
            (ShapeFailureKind, string, string?, string?) key = (failure.Kind, failure.ExceptionType, failure.Path, failure.Detail);

            if (bucket.Failures.TryGetValue(key, out FailureBucket? existing))
            {
                existing.Count++;
            }
            else
            {
                bucket.Failures[key] = new FailureBucket(failure, example);
            }

            return;
        }

        foreach (ShapeMember member in members)
        {
            (ShapeMemberKind, string, string?) key = (member.Kind, member.Path, member.PairedPath);

            if (bucket.Members.TryGetValue(key, out MemberBucket? existing))
            {
                existing.Count++;
            }
            else
            {
                bucket.Members[key] = new MemberBucket(member, example);
            }
        }
    }

    /// <summary>Records that one examined event of <paramref name="typeName" /> was written as another type and read as this one.</summary>
    /// <param name="typeName">The type it was read as.</param>
    /// <param name="fullTypeName">That type's full name.</param>
    public void NoteReadAsOtherType(string typeName, string? fullTypeName) => Bucket(typeName, fullTypeName).ReadAsOtherType++;

    /// <summary>Records that one examined event of <paramref name="typeName" /> has a binary body: read, not compared.</summary>
    /// <param name="typeName">The type.</param>
    /// <param name="fullTypeName">That type's full name.</param>
    public void NoteBinary(string typeName, string? fullTypeName) => Bucket(typeName, fullTypeName).Binary++;

    /// <summary>The per-type results, types with a problem first, then by name; findings capped.</summary>
    public IReadOnlyList<ShapeTypeResult> Build()
    {
        List<ShapeTypeResult> results = [];

        // The short name, unless two types share it: then the full name tells them apart.
        HashSet<string> shared = [.. types.Values.GroupBy(static x => x.TypeName, StringComparer.Ordinal).Where(static x => x.Count() > 1).Select(static x => x.Key)];

        foreach (TypeBucket bucket in types.Values)
        {
            List<ShapeFailureGroup> failures = [.. bucket.Failures.Values
                .OrderByDescending(static x => x.Count)
                .ThenBy(static x => x.Failure.Path, StringComparer.Ordinal)
                .ThenBy(static x => x.Failure.ExceptionType, StringComparer.Ordinal)
                .Select(static x => new ShapeFailureGroup(
                    x.Failure.Kind,
                    x.Failure.ExceptionType,
                    x.Failure.Path,
                    x.Failure.StoredKind,
                    x.Failure.Detail,
                    x.Count,
                    x.Example,
                    x.Example.Sequence))];

            List<ShapeMemberFinding> members = [.. bucket.Members.Values
                .OrderBy(static x => KindOrder(x.Member.Kind))
                .ThenByDescending(static x => x.Count)
                .ThenBy(static x => x.Member.Path, StringComparer.Ordinal)
                .Select(static x => new ShapeMemberFinding(
                    x.Member.Kind,
                    x.Member.Path,
                    x.Member.PairedPath,
                    x.Count,
                    x.Example,
                    x.Member.StoredKind,
                    x.Member.ReadKind))];

            int moreFailures = Math.Max(0, failures.Count - ShapeCheckLimits.MaxFailureGroups);
            int moreMembers = Math.Max(0, members.Count - ShapeCheckLimits.MaxMemberFindings);

            results.Add(new ShapeTypeResult(
                shared.Contains(bucket.TypeName) ? bucket.FullTypeName ?? bucket.TypeName : bucket.TypeName,
                bucket.FullTypeName,
                bucket.Examined,
                bucket.ReadAsOtherType,
                bucket.Binary,
                [.. failures.Take(ShapeCheckLimits.MaxFailureGroups)],
                [.. members.Take(ShapeCheckLimits.MaxMemberFindings)],
                moreFailures,
                moreMembers));
        }

        return [.. results
            .OrderByDescending(static x => x.Failures.Count + x.Members.Count + x.MoreFailures + x.MoreMembers > 0)
            .ThenBy(static x => x.TypeName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static x => x.TypeName, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The pairs of a Dropped member and a Defaulted sibling of one type whose counts are within ten percent of each other - the
    /// rename question the table asks under both. A question, never a statement: it is a guess about intent.
    /// </summary>
    /// <param name="type">One type's findings.</param>
    public static IReadOnlyList<(string Dropped, string Defaulted)> RenameQuestions(ShapeTypeResult type)
    {
        ArgumentNullException.ThrowIfNull(type);

        List<ShapeMemberFinding> defaulted = [.. type.Members.Where(static x => x.Kind == ShapeMemberKind.Defaulted)];
        HashSet<ShapeMemberFinding> used = [];
        List<(string, string)> pairs = [];

        foreach (ShapeMemberFinding drop in type.Members.Where(static x => x.Kind == ShapeMemberKind.Dropped))
        {
            ShapeMemberFinding? best = null;

            foreach (ShapeMemberFinding candidate in defaulted)
            {
                if (used.Contains(candidate)
                    || !string.Equals(Parent(drop.Path), Parent(candidate.Path), StringComparison.Ordinal)
                    || !WithinTenPercent(drop.Count, candidate.Count))
                {
                    continue;
                }

                if (best is null || Math.Abs(candidate.Count - drop.Count) < Math.Abs(best.Count - drop.Count))
                {
                    best = candidate;
                }
            }

            if (best is not null)
            {
                used.Add(best);
                pairs.Add((drop.Path, best.Path));
            }
        }

        return pairs;
    }

    /// <summary>The JSON kind named for a sentence: "a string", "an object".</summary>
    /// <param name="kind">The kind.</param>
    internal static string KindPhrase(JsonValueKind? kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "another kind of value",
    };

    private static bool WithinTenPercent(int left, int right) => Math.Abs(left - right) * 10 <= Math.Max(left, right);

    private static string Parent(string path)
    {
        int cut = path.LastIndexOf('.');
        return cut < 0 ? path : path[..cut];
    }

    private static int KindOrder(ShapeMemberKind kind) => kind switch
    {
        ShapeMemberKind.Dropped => 0,
        ShapeMemberKind.Nulled => 1,
        ShapeMemberKind.Retyped => 2,
        ShapeMemberKind.Rewritten => 3,
        ShapeMemberKind.Respelled => 4,
        _ => 5,
    };

    private TypeBucket Bucket(string typeName, string? fullTypeName)
    {
        // Keyed by the full name: two subclasses of one name in two namespaces are two types, and merging them would blame one for the other.
        string key = fullTypeName ?? typeName;

        if (!types.TryGetValue(key, out TypeBucket? bucket))
        {
            bucket = new TypeBucket(typeName, fullTypeName);
            types[key] = bucket;
        }

        return bucket;
    }

    private sealed class TypeBucket(string typeName, string? fullTypeName)
    {
        public string TypeName { get; } = typeName;

        public string? FullTypeName { get; } = fullTypeName;

        public int Examined { get; set; }

        public int ReadAsOtherType { get; set; }

        public int Binary { get; set; }

        public Dictionary<(ShapeFailureKind, string, string?, string?), FailureBucket> Failures { get; } = [];

        public Dictionary<(ShapeMemberKind, string, string?), MemberBucket> Members { get; } = [];
    }

    private sealed class FailureBucket(ShapeFailure failure, ShapeItemKey example)
    {
        public ShapeFailure Failure { get; } = failure;

        public ShapeItemKey Example { get; } = example;

        public int Count { get; set; } = 1;
    }

    private sealed class MemberBucket(ShapeMember member, ShapeItemKey example)
    {
        public ShapeMember Member { get; } = member;

        public ShapeItemKey Example { get; } = example;

        public int Count { get; set; } = 1;
    }
}
