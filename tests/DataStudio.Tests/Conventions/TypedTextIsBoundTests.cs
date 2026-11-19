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
    /// The two editors that are committed on <c>change</c>: their <c>@oninput</c> handler only clears an error and never sets the value
    /// the box is drawn from, so a render after a keystroke has no new value to send.
    /// </summary>
    private static readonly string[] CommittedOnChange =
    [
        "src/DataStudio.Postgres/Components/Pages/Query/QueryEditor.razor",
        "src/DataStudio.Marten/Components/Pages/Query/LinqQueryPanel.razor",
    ];

    internal static bool IsUnboundTypedText(string element) =>
        element.Contains("@oninput=", StringComparison.Ordinal) && Regex.IsMatch(element, @"(?<![\w:-])value=""@");

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
                if (IsUnboundTypedText(match.Value) && !CommittedOnChange.Contains(relative))
                {
                    offenders.Add(relative);
                }
            }
        }

        boxes.Should().BeGreaterThan(20, "the scan read the components' boxes");
        offenders.Should().BeEmpty("a box kept as it is typed uses @bind:get, @bind:set and @bind:event=\"oninput\"");
    }

    [Fact]
    public void The_exceptions_still_exist_and_still_commit_on_change()
    {
        foreach (string relative in CommittedOnChange)
        {
            string text = File.ReadAllText(RepositoryRoot.Combine(relative.Split('/')));
            Element.Matches(text).Should().Contain(x => IsUnboundTypedText(x.Value) && x.Value.Contains("@onchange=", StringComparison.Ordinal), relative + " is listed because it has such a box");
        }
    }

    [Theory]
    [InlineData("<input value=\"@typed\" @oninput=\"OnTyped\" />", true)]
    [InlineData("<textarea class=\"a\" value=\"@predicate\"\n   @oninput=\"OnInput\"></textarea>", true)]
    [InlineData("<input @bind:get=\"typed\" @bind:event=\"oninput\" @bind:set=\"OnTyped\" />", false)]
    [InlineData("<input value=\"@typed\" @onchange=\"OnChanged\" />", false)]
    [InlineData("<input type=\"checkbox\" data-value=\"@x\" @oninput=\"OnInput\" />", false)]
    public void The_scanner_knows_the_pair(string element, bool expected)
    {
        Element.IsMatch(element).Should().BeTrue();
        IsUnboundTypedText(Element.Match(element).Value).Should().Be(expected);
    }
}
