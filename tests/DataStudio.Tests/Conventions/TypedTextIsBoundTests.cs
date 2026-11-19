using System.Text.RegularExpressions;

using DataStudio.Marten.Tests.Conventions;

namespace DataStudio.Tests.Conventions;

/// <summary>
/// A box whose text the server keeps as it is typed is bound (<c>@bind:get</c>, <c>@bind:set</c>, <c>@bind:event="oninput"</c>), never
/// written as <c>value="@field"</c> beside an <c>@oninput</c> handler that sets the field.
/// </summary>
/// <remarks>
/// With the hand-written pair, each keystroke's render sends the box the value the server had when it rendered, and on a slow
/// connection that arrives after later keystrokes and puts the older text back: characters are lost, and a typed confirmation no
/// longer matches its phrase (task g22x; on a GitHub runner the Rebuild dialog stayed open, 2026-11-19). A bind tells the renderer what
/// the box held when the event was raised, so a render sends the box nothing it already has. A browser test proves the behaviour for the
/// confirmation dialog (<c>A_phrase_typed_over_a_slow_connection_is_the_phrase_in_the_box</c>); this scan keeps the pattern out of
/// every other component.
/// </remarks>
public class TypedTextIsBoundTests
{
    private static readonly Regex Element = new(@"<(?:input|textarea)\b(?:""[^""]*""|'[^']*'|[^>""'])*>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// The boxes a chord commits (Ctrl+Enter, Ctrl+S): the chord raises <c>change</c> from the script before it calls the server, so each is
    /// bound on <c>change</c>, and the render after the chord has nothing to send the box (xvfv). Named by the file and what marks the box.
    /// </summary>
    private static readonly (string File, string Marker)[] CommittedByAChord =
    [
        ("src/DataStudio.Postgres/Components/Pages/Query/QueryEditor.razor", "ms-query-editor-input"),
        ("src/DataStudio.Marten/Components/Pages/Query/LinqQueryPanel.razor", "ms-query-linq-orderby"),
        ("src/DataStudio/Components/Json/JsonEditor.razor", "ms-editor-input"),
    ];

    /// <summary>
    /// A box kept as it is typed, or a date-and-time box, which a browser commits part by part while the visitor is still typing in it:
    /// neither is drawn from a hand-written <c>value</c>.
    /// </summary>
    internal static bool IsUnboundTypedText(string element) =>
        Regex.IsMatch(element, @"(?<![\w:-])value=""@")
        && (element.Contains("@oninput=", StringComparison.Ordinal) || element.Contains("datetime-local", StringComparison.OrdinalIgnoreCase) || element.Contains("DateTimeLocal", StringComparison.Ordinal));

    [Fact]
    public void No_box_in_src_pairs_a_value_with_an_oninput_handler()
    {
        string root = RepositoryRoot.Combine("src");
        List<string> offenders = [];
        int boxes = 0;

        foreach (string file in Directory.EnumerateFiles(root, "*.razor", SearchOption.AllDirectories))
        {
            string relative = "src/" + Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative.Contains("/obj/", StringComparison.Ordinal) || relative.Contains("/bin/", StringComparison.Ordinal))
            {
                continue;
            }

            foreach (Match match in Element.Matches(File.ReadAllText(file)))
            {
                boxes++;
                if (IsUnboundTypedText(match.Value))
                {
                    offenders.Add(relative);
                }
            }
        }

        boxes.Should().BeGreaterThan(20, "the scan read the components' boxes");
        offenders.Should().BeEmpty("a box kept as it is typed uses @bind:get, @bind:set and @bind:event=\"oninput\"");
    }

    [Fact]
    public void Every_box_a_chord_commits_is_bound_on_change()
    {
        foreach ((string file, string marker) in CommittedByAChord)
        {
            string text = File.ReadAllText(RepositoryRoot.Combine(file.Split('/')));
            Match[] boxes = [.. Element.Matches(text).Where(x => x.Value.Contains(marker, StringComparison.Ordinal))];

            boxes.Should().ContainSingle(file + " has the box marked " + marker);
            boxes[0].Value.Should().Contain("@bind:get=").And.Contain("@bind:event=\"onchange\"").And.NotContain("value=\"@", file);
        }
    }

    [Theory]
    [InlineData("<input value=\"@typed\" @oninput=\"OnTyped\" />", true)]
    [InlineData("<textarea class=\"a\" value=\"@predicate\"\n   @oninput=\"OnInput\"></textarea>", true)]
    [InlineData("<input @bind:get=\"typed\" @bind:event=\"oninput\" @bind:set=\"OnTyped\" />", false)]
    [InlineData("<input value=\"@typed\" @onchange=\"OnChanged\" />", false)]
    [InlineData("<input type=\"datetime-local\" value=\"@from\" @onchange=\"OnChanged\" />", true)]
    [InlineData("<input type=\"@DateTimeLocal\" @bind:get=\"from\" @bind:event=\"onchange\" @bind:set=\"Set\" />", false)]
    [InlineData("<input type=\"checkbox\" data-value=\"@x\" @oninput=\"OnInput\" />", false)]
    public void The_scanner_knows_the_pair(string element, bool expected)
    {
        Element.IsMatch(element).Should().BeTrue();
        IsUnboundTypedText(Element.Match(element).Value).Should().Be(expected);
    }
}
