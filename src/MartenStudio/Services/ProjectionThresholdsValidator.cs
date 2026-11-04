using Microsoft.Extensions.Options;

using PostgresStudio.Services;

namespace MartenStudio.Services;

/// <summary>
/// Validates <see cref="MartenStudioOptions.ProjectionThresholds" />: both numbers greater than zero, and the critical threshold
/// at least the warning threshold. A type of its own, not a lambda, because a message names the projection it is about. A name
/// that no projection of a store has is not a failure here - projections are registered per environment - and the Projections
/// page says so.
/// </summary>
internal sealed class ProjectionThresholdsValidator : IValidateOptions<MartenStudioOptions>
{
    private const string Prefix = "MartenStudioOptions.ProjectionThresholds[";

    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, MartenStudioOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];

        foreach ((string projection, ProjectionLagThreshold? threshold) in options.ProjectionLagThresholdsByName)
        {
            string subject = Prefix + "'" + StudioLogValue.Cap(projection) + "']";

            if (string.IsNullOrWhiteSpace(projection))
            {
                failures.Add(Prefix + "'']: a projection name cannot be empty or whitespace");
            }
            else if (threshold is null)
            {
                failures.Add(subject + " cannot be null: give it a ProjectionLagThreshold, or remove the entry");
            }
            else if (threshold.WarningBehind <= 0 || threshold.CriticalBehind <= 0)
            {
                failures.Add(subject + ": WarningBehind and CriticalBehind must both be greater than zero");
            }
            else if (threshold.CriticalBehind < threshold.WarningBehind)
            {
                failures.Add(subject + ": CriticalBehind must be at least WarningBehind, or a shard would be red before it was amber");
            }
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
