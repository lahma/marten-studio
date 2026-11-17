using System.Globalization;

using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Services.Statements;

/// <summary>
/// The Statements page's addresses as a visitor's links: the route and the <c>queryid</c> parameter are the engine's
/// (<see cref="StatementLinks" />), the links that carry the scope are the UI's.
/// </summary>
internal static class StatementLinkBuilder
{
    extension(StatementLinks)
    {
        /// <summary>The page, with the scope carried under the studio's own source parameter.</summary>
        public static string ToStatements(StudioDescriptor descriptor, StudioScope? scope)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            return DataStudio.Postgres.Components.StudioScopeQuery.Append(DataStudio.Components.StudioLink.To(StatementLinks.Route), scope, descriptor.SourceQueryParameter);
        }

        /// <summary>
        /// The page with the index advisor open (1.2-INDEX-A, D9): for every table, or for one (a <c>schema.table</c> key). Opening it
        /// runs nothing - the panel's Run button does.
        /// </summary>
        public static string ToAdvisor(StudioDescriptor descriptor, StudioScope? scope, string? table = null)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            string url = DataStudio.Components.StudioLink.To(StatementLinks.Route) + "?" + IndexAdvisor.AdviseParameter + "=" + Uri.EscapeDataString(table ?? IndexAdvisor.AdviseAll);
            return DataStudio.Postgres.Components.StudioScopeQuery.Append(url, scope, descriptor.SourceQueryParameter);
        }

        /// <summary>One statement's drill-in: the page, its <c>queryid</c> and the scope - a link that reopens the same statement.</summary>
        public static string ToStatement(StudioDescriptor descriptor, StudioScope? scope, long queryId)
        {
            ArgumentNullException.ThrowIfNull(descriptor);

            string url = DataStudio.Components.StudioLink.To(StatementLinks.Route) + "?" + StatementLinks.QueryIdParameter + "=" + queryId.ToString(CultureInfo.InvariantCulture);
            return DataStudio.Postgres.Components.StudioScopeQuery.Append(url, scope, descriptor.SourceQueryParameter);
        }
    }
}
