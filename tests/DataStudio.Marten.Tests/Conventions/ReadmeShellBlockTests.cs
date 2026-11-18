namespace DataStudio.Marten.Tests.Conventions;

/// <summary>
/// A host copies a command from a README into its own terminal, and each package's README is its nuget.org page. A line that starts with <c>#</c> inside a
/// shell block is a comment in bash and in PowerShell and an error in <c>cmd.exe</c> ("'#' is not recognized as an internal or external command"), printed between
/// commands that still run. The sentence belongs above the block, so no packed README has such a line in a block a person pastes from.
/// </summary>
public class ReadmeShellBlockTests
{
    private static readonly string[] CommandLanguages = ["shell", "sh", "bash", "console", "cmd", "bat", "powershell", "pwsh"];

    /// <summary>The repository's README (which is the Marten package's own) and every package's.</summary>
    private static string[] PackedReadmes() =>
    [
        RepositoryRoot.Combine("README.md"),
        .. Directory.GetFiles(RepositoryRoot.Combine("src"), "README.md", SearchOption.AllDirectories)
            .Where(static x => !x.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && !x.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)),
    ];

    [Fact]
    public void No_command_block_of_a_packed_readme_has_a_line_that_starts_with_a_hash()
    {
        string[] readmes = PackedReadmes();
        readmes.Length.Should().BeGreaterThanOrEqualTo(5, "the repository's README and the four package READMEs (the shell, the Postgres module and engine, the Kafka module), or this test proves nothing");

        List<string> offending = [];
        foreach (string readme in readmes)
        {
            bool inside = false;
            bool command = false;
            int number = 0;

            foreach (string line in File.ReadAllLines(readme))
            {
                number++;
                string trimmed = line.TrimStart();

                if (trimmed.StartsWith("```", StringComparison.Ordinal))
                {
                    command = !inside && CommandLanguages.Contains(trimmed[3..].Trim(), StringComparer.OrdinalIgnoreCase);
                    inside = !inside;
                    continue;
                }

                if (inside && command && trimmed.StartsWith('#'))
                {
                    offending.Add(Path.GetRelativePath(RepositoryRoot.FullPath, readme).Replace('\\', '/') + ":" + number + ": " + trimmed);
                }
            }
        }

        offending.Should().BeEmpty("a '#' line is an error when it is pasted into cmd.exe: say it in a sentence above the block");
    }
}
