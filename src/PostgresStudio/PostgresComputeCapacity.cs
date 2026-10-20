namespace PostgresStudio;

/// <summary>A compute capacity reading, from a host's <see cref="IPostgresCapacitySource" />. Built by its factories, which check it.</summary>
/// <remarks>
/// <see cref="Current" /> is not checked against <see cref="Maximum" />: a cloud metric is averaged over a period, and a
/// maximum lowered mid-period legitimately leaves the average above it.
/// </remarks>
public sealed record PostgresComputeCapacity
{
    private PostgresComputeCapacity(double current, double minimum, double maximum, string unit, DateTimeOffset measuredAt)
    {
        Current = current;
        Minimum = minimum;
        Maximum = maximum;
        Unit = unit;
        MeasuredAt = measuredAt;
    }

    /// <summary>The capacity in use when it was measured.</summary>
    public double Current { get; }

    /// <summary>The least the capacity may scale down to.</summary>
    public double Minimum { get; }

    /// <summary>The most the capacity may scale up to.</summary>
    public double Maximum { get; }

    /// <summary>The unit: <c>ACU</c> for Aurora Serverless v2.</summary>
    public string Unit { get; }

    /// <summary>When the reading was taken, by the source's clock.</summary>
    public DateTimeOffset MeasuredAt { get; }

    /// <summary>
    /// Whether the cluster uses I/O-optimized storage (Aurora's <c>aurora-iopt1</c> storage type), or
    /// <see langword="null" /> when the source does not say. It changes which I/O advice applies, and the setting can
    /// change at most once every thirty days.
    /// </summary>
    public bool? IoOptimized { get; init; }

    /// <summary>A reading in any unit.</summary>
    /// <param name="current">The capacity in use.</param>
    /// <param name="minimum">The least it may scale down to.</param>
    /// <param name="maximum">The most it may scale up to.</param>
    /// <param name="unit">The unit, as a person reads it.</param>
    /// <param name="measuredAt">When it was measured.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is negative or not finite, or minimum exceeds maximum.</exception>
    /// <exception cref="ArgumentException"><paramref name="unit" /> is null, empty or whitespace.</exception>
    public static PostgresComputeCapacity Of(double current, double minimum, double maximum, string unit, DateTimeOffset measuredAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(unit);

        RequireNonNegative(current, nameof(current));
        RequireNonNegative(minimum, nameof(minimum));
        RequireNonNegative(maximum, nameof(maximum));
        RequireOrdered(minimum, maximum);

        return new PostgresComputeCapacity(current, minimum, maximum, unit, measuredAt);
    }

    /// <summary>
    /// Aurora Serverless v2, from CloudWatch's <c>ServerlessDatabaseCapacity</c> and the cluster's scaling
    /// configuration. The unit is <c>ACU</c>.
    /// </summary>
    /// <param name="currentAcu">The capacity in use, in ACU.</param>
    /// <param name="minimumAcu">The cluster's minimum, in ACU.</param>
    /// <param name="maximumAcu">The cluster's maximum, in ACU.</param>
    /// <param name="measuredAt">When it was measured.</param>
    /// <returns>The reading.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A value is outside 0 to 256 ACU or not finite, or minimum exceeds maximum.</exception>
    public static PostgresComputeCapacity AuroraServerlessV2(double currentAcu, double minimumAcu, double maximumAcu, DateTimeOffset measuredAt)
    {
        RequireAcu(currentAcu, nameof(currentAcu));
        RequireAcu(minimumAcu, nameof(minimumAcu));
        RequireAcu(maximumAcu, nameof(maximumAcu));
        RequireOrdered(minimumAcu, maximumAcu);

        return new PostgresComputeCapacity(currentAcu, minimumAcu, maximumAcu, "ACU", measuredAt);
    }

    /// <summary>The most ACU an Aurora Serverless v2 cluster may be configured with.</summary>
    internal const double MaxAcu = 256;

    private static void RequireNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "A capacity must be a finite number, zero or more.");
        }
    }

    private static void RequireAcu(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0 || value > MaxAcu)
        {
            throw new ArgumentOutOfRangeException(name, value, "Aurora Serverless v2 capacity is between 0 and 256 ACU.");
        }
    }

    private static void RequireOrdered(double minimum, double maximum)
    {
        if (minimum > maximum)
        {
            throw new ArgumentOutOfRangeException(nameof(minimum), minimum, "The minimum capacity cannot exceed the maximum.");
        }
    }
}
