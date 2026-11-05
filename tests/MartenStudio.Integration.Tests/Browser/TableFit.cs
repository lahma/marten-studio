using Microsoft.Playwright;

namespace MartenStudio.Integration.Tests.Browser;

/// <summary>
/// Measures whether a data table's columns sit inside its scroll region (D25), and says where each column's right edge is when one does not.
/// </summary>
internal static class TableFit
{
    /// <summary>
    /// A stand-in for CI's wider fonts (1.2-FINAL-FIX): letter spacing on the content area makes every text run about 5 % wider than the
    /// machine's own font draws it, which is more than the difference between this repository's Windows fonts and ubuntu-latest's.
    /// </summary>
    public const string WiderMetricsCss = ".ms-content, .ms-content * { letter-spacing: 0.05em !important; }";

    private const string Script =
        """
        (selector) => {
            const table = document.querySelector(selector);
            const region = table.closest('.ms-table-scroll');
            const box = region.getBoundingClientRect();
            return JSON.stringify({
                regionWidth: Math.round(box.width),
                scrollWidth: region.scrollWidth,
                clientWidth: region.clientWidth,
                columns: [...table.querySelectorAll('thead th')].map(th => {
                    const r = th.getBoundingClientRect();
                    return { name: th.textContent.trim(), left: Math.round(r.left - box.left), right: Math.round(r.right - box.left) };
                })
            });
        }
        """;

    private static readonly System.Text.Json.JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Measures the table <paramref name="selector" /> names, in its own scroll region.</summary>
    public static async Task<Report> MeasureAsync(IPage page, string selector) =>
        System.Text.Json.JsonSerializer.Deserialize<Report>(await page.EvaluateAsync<string>(Script, selector), Json)!;

    /// <summary>Adds <see cref="WiderMetricsCss" /> to the page.</summary>
    public static Task WidenAsync(IPage page) => page.AddStyleTagAsync(new PageAddStyleTagOptions { Content = WiderMetricsCss });

    /// <summary>What was measured. A class with settable members, because Playwright builds the result with a parameterless constructor.</summary>
    public sealed class Report
    {
        /// <summary>The region's width in pixels.</summary>
        public int RegionWidth { get; set; }

        /// <summary>The region's scroll width.</summary>
        public int ScrollWidth { get; set; }

        /// <summary>The region's client width.</summary>
        public int ClientWidth { get; set; }

        /// <summary>The header cells' boxes.</summary>
        public List<Column> Columns { get; set; } = [];

        /// <summary>The names of the columns that are not inside the region.</summary>
        public string[] Outside() =>
            [.. Columns.Where(c => c.Left < -1 || c.Right > RegionWidth + 1).Select(c => c.Name)];

        /// <summary>Whether the region has nothing to scroll to.</summary>
        public bool Fits => ScrollWidth <= ClientWidth + 1;

        /// <summary>Each column's right edge against the region's width, for an assertion's reason.</summary>
        public string Describe() =>
            "region " + RegionWidth + " px wide (scroll width " + ScrollWidth + "); right edges: "
            + string.Join(", ", Columns.Select(c => c.Name + " " + c.Right));
    }

    /// <summary>One column header's box, relative to the region's left edge.</summary>
    public sealed class Column
    {
        /// <summary>The header's text.</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>Its left edge.</summary>
        public int Left { get; set; }

        /// <summary>Its right edge.</summary>
        public int Right { get; set; }
    }
}
