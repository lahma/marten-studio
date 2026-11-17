using DataStudio.Marten.Services;
using DataStudio.Postgres.Services.Health;
using DataStudio.Postgres.Services.Settings;

namespace DataStudio.Marten.Tests;

/// <summary>
/// An evaluation at a moment, for a unit test of a check, in the Marten module's words: C# 14 static extension members, so that the tests keep
/// calling <c>HealthEvaluation.At(now)</c>. The shared checks are evaluated here as a Marten host runs them, so a sentence that fell back to
/// another module's words would be seen by the assertion on it.
/// </summary>
/// <remarks>
/// <b>Test-only on purpose.</b> Production code never builds an evaluation by hand and never falls back to one module's words: the sweep sets
/// <see cref="HealthEvaluation.Text" /> from the registered settings.
/// </remarks>
internal static class MartenHealthEvaluationAt
{
    extension(HealthEvaluation)
    {
        /// <summary>An evaluation at <paramref name="now" />, in the Marten module's words unless <paramref name="text" /> says otherwise.</summary>
        /// <param name="now">When.</param>
        /// <param name="runtime">The runtime, or <see langword="null" /> for none.</param>
        /// <param name="history">The history, or none.</param>
        /// <param name="text">The words, or <see langword="null" /> for the Marten module's.</param>
        public static HealthEvaluation At(
            DateTimeOffset now,
            IStudioRuntime? runtime = null,
            HealthHistoryView? history = null,
            StudioText? text = null) =>
            new(now, runtime ?? NoStudioRuntime.Instance, history ?? HealthHistoryView.Empty) { Text = text ?? MartenStudioText.Instance };
    }
}
