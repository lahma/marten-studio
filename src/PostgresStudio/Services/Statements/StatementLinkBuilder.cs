using System.Globalization;

using PostgresStudio.Services.Settings;

namespace PostgresStudio.Services.Statements;

/// <summary>
/// The Statements page's addresses as a visitor's links: the route and the <c>queryid</c> parameter are the engine's
/// (<see cref="StatementLinks" />), the links that carry the scope are the UI's.
/// </summary>
internal static class StatementLinkBuilder
{
    extension(StatementLinks)
    {
        /// <summary>The page, with the scope carried under the studio's own source parameter.</summary>
        public static string ToStatements(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(descriptor);
            return PostgresStudio.Components.StudioScopeQuery.Append(PostgresStudio.Components.StudioLink.To(settings, StatementLinks.Route), scope, descriptor.SourceQueryParameter);
        }

        /// <summary>One statement's drill-in: the page, its <c>queryid</c> and the scope - a link that reopens the same statement.</summary>
        public static string ToStatement(IStudioSettings settings, StudioDescriptor descriptor, StudioScope? scope, long queryId)
        {
            ArgumentNullException.ThrowIfNull(settings);
            ArgumentNullException.ThrowIfNull(descriptor);

            string url = PostgresStudio.Components.StudioLink.To(settings, StatementLinks.Route) + "?" + StatementLinks.QueryIdParameter + "=" + queryId.ToString(CultureInfo.InvariantCulture);
            return PostgresStudio.Components.StudioScopeQuery.Append(url, scope, descriptor.SourceQueryParameter);
        }
    }
}
