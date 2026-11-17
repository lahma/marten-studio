using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Postgres;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Postgres.Tests;

/// <summary>
/// An evaluation at a moment, for a unit test of a check, in the Postgres module's words: C# 14 static extension members, so that the tests
/// keep calling <c>HealthEvaluation.At(now)</c>.
/// </summary>
/// <remarks>
/// <b>Test-only on purpose.</b> Production code never builds an evaluation by hand and never falls back to one module's words: the sweep sets
/// <see cref="HealthEvaluation.Text" /> from the registered settings.
/// </remarks>
internal static class PostgresHealthEvaluationAt
{
    extension(HealthEvaluation)
    {
        /// <summary>An evaluation at <paramref name="now" />, in the Postgres module's words unless <paramref name="text" /> says otherwise.</summary>
        /// <param name="now">When.</param>
        /// <param name="runtime">The runtime, or <see langword="null" /> for none.</param>
        /// <param name="history">The history, or none.</param>
        /// <param name="text">The words, or <see langword="null" /> for the Postgres module's.</param>
        public static HealthEvaluation At(
            DateTimeOffset now,
            IStudioRuntime? runtime = null,
            HealthHistoryView? history = null,
            StudioText? text = null) =>
            new(now, runtime ?? NoStudioRuntime.Instance, history ?? HealthHistoryView.Empty) { Text = text ?? PostgresText.Instance };
    }
}
