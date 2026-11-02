namespace PostgresStudio.Services.Diagnostics;

/// <summary>The grouping behind <see cref="PostgresDiagnosticsSnapshot.FindingsByCheck" />: the Health page's one line per check.</summary>
internal static class FindingGroups
{
    /// <summary>
    /// A snapshot's unaccepted findings, grouped by code and ordered worst severity first, then by the check's position in
    /// the snapshot's checks, then (for a code the checks do not list) by code. A snapshot that was not read has none.
    /// </summary>
    /// <param name="snapshot">The snapshot.</param>
    public static IReadOnlyList<PostgresFindingGroup> Of(PostgresDiagnosticsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        if (snapshot.Outcome != PostgresReadOutcome.Read || snapshot.Findings.Count == 0)
        {
            return [];
        }

        Dictionary<string, int> position = new(StringComparer.Ordinal);
        Dictionary<string, string> titles = new(StringComparer.Ordinal);

        for (int i = 0; i < snapshot.Checks.Count; i++)
        {
            PostgresCheck check = snapshot.Checks[i];
            position.TryAdd(check.Code, i);
            titles.TryAdd(check.Code, check.Title);
        }

        // Insertion order keeps each group's findings in the snapshot's order.
        Dictionary<string, List<PostgresFinding>> byCode = new(StringComparer.Ordinal);

        foreach (PostgresFinding finding in snapshot.Findings)
        {
            if (finding.Accepted)
            {
                continue;
            }

            if (!byCode.TryGetValue(finding.Code, out List<PostgresFinding>? list))
            {
                byCode[finding.Code] = list = [];
            }

            list.Add(finding);
        }

        return
        [
            .. byCode
                .Select(entry => Group(entry.Value, titles))
                .OrderBy(static x => x.Severity)
                .ThenBy(x => position.GetValueOrDefault(x.Code, int.MaxValue))
                .ThenBy(static x => x.Code, StringComparer.Ordinal),
        ];
    }

    private static PostgresFindingGroup Group(List<PostgresFinding> findings, Dictionary<string, string> titles)
    {
        PostgresFinding first = findings[0];
        bool one = findings.Count == 1;

        return new PostgresFindingGroup
        {
            Severity = findings.Min(static x => x.Severity),
            Code = first.Code,
            Category = first.Category,
            Title = one ? first.Title : titles.GetValueOrDefault(first.Code) is { Length: > 0 } title ? title : first.Title,
            Subject = one ? first.Subject : null,
            Detail = one ? first.Detail : null,
            Count = findings.Count,
            StudioPath = one ? first.StudioPath : HealthPage(first.StudioPath),
            Findings = findings,
        };
    }

    // A finding's path is its database's Health page, then "#" and the finding's card (DiagnosticsMapper.Finding): the page
    // is what GetStudioPath(database, Health) returns, built by the same HealthLinks.Relative from the same scope.
    private static string HealthPage(string findingPath)
    {
        int fragment = findingPath.IndexOf('#', StringComparison.Ordinal);
        return fragment < 0 ? findingPath : findingPath[..fragment];
    }
}
