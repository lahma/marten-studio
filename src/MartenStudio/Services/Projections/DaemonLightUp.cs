using System.Reflection;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

using MartenStudio.Services.Configuration;

namespace MartenStudio.Services.Projections;

/// <summary>Why a shard did not start, as the daemon classified it.</summary>
/// <param name="Reason">
/// <c>ShardStartException.Reason</c> by name (<c>AgentPaused</c>, <c>HighWaterNotRunning</c>, <c>ShardNotRegistered</c>,
/// <c>StartRace</c>, <c>Faulted</c>, <c>Rebuilding</c>), or <see langword="null" /> on a JasperFx that does not
/// classify (below 2.76).
/// </param>
/// <param name="IsTransient">
/// <c>ShardStartException.IsTransient</c>: whether a retry is expected to resolve it - or <see langword="null" />
/// when JasperFx does not say.
/// </param>
/// <param name="Message">The exception's own message, which is the human half and is shown as it is.</param>
internal sealed record ShardStartRefusal(string? Reason, bool? IsTransient, string Message);

/// <summary>
/// The daemon members newer than the floor, reached through the F1 probes and nowhere else.
/// </summary>
/// <remarks>
/// <para>
/// <b>Reflection is confined to members the floor lacks</b>, each behind its <see cref="RuntimeFeature" />, so a
/// member that moves fails <c>MartenRuntimeProbeTests</c> at the latest Marten rather than quietly answering
/// "absent". Everything the floor has (<c>ShardStartException</c> itself, <c>IProjectionDaemon.StartAgentAsync</c>
/// with a <c>ShardName</c>) is called directly.
/// </para>
/// <para>
/// <b>Only a "yes" from <c>IsRebuilding</c> means anything.</b> It is a default interface method that answers
/// <see langword="false" /> for a daemon that does not track rebuilds (JasperFx 2.86), so <see langword="false" />
/// is "not known to be rebuilding", never "not rebuilding" - and the studio's own operation tracker is asked as well.
/// </para>
/// </remarks>
internal static class DaemonLightUp
{
    private static readonly Type[] ShardNameParameter = [typeof(ShardName)];

    private static readonly Type[] ExceptionParameter = [typeof(Exception)];

    private static readonly Lazy<MethodInfo?> IsRebuildingMethod = new(
        static () => typeof(IProjectionDaemon).GetMethod("IsRebuilding", BindingFlags.Public | BindingFlags.Instance, ShardNameParameter),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<MethodInfo?> IsTransientMethod = new(
        static () => typeof(IProjectionDaemon).Assembly
            .GetType("JasperFx.Events.Daemon.TransientFailures", throwOnError: false)?
            .GetMethod("IsTransient", BindingFlags.Public | BindingFlags.Static, ExceptionParameter),
        LazyThreadSafetyMode.ExecutionAndPublication);

    private static readonly Lazy<MethodInfo?> SystemicMethod = new(
        RuntimeFeature.SystemicDatabaseFailureMethod,
        LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// Whether <paramref name="daemon" /> says it is rebuilding <paramref name="shard" />'s projection right now.
    /// <see langword="false" /> on a JasperFx without <c>IsRebuilding</c>, and on one whose daemon does not track it.
    /// </summary>
    public static bool IsRebuilding(IProjectionDaemon daemon, ShardName shard)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(shard);

        if (!RuntimeFeature.DaemonIsRebuilding.IsAvailable(MartenRuntime.Current) || IsRebuildingMethod.Value is not { } method)
        {
            return false;
        }

        try
        {
            return method.Invoke(daemon, [shard]) is true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or NotSupportedException or NotImplementedException)
        {
            return false;
        }
    }

    /// <summary>Whether the daemon here can say whether it is rebuilding at all (JasperFx 2.86+).</summary>
    public static bool CanTellRebuilding => RuntimeFeature.DaemonIsRebuilding.IsAvailable(MartenRuntime.Current);

    /// <summary>
    /// What a <see cref="ShardStartException" /> says about itself: its classified reason and whether it is transient
    /// when JasperFx classifies it (2.76+, <see cref="RuntimeFeature.ShardStartReason" />), and its message.
    /// </summary>
    public static ShardStartRefusal Describe(ShardStartException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!RuntimeFeature.ShardStartReason.IsAvailable(MartenRuntime.Current))
        {
            return new ShardStartRefusal(null, null, exception.Message);
        }

        Type type = exception.GetType();
        string? reason = type.GetProperty("Reason", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception)?.ToString();
        bool? transient = type.GetProperty("IsTransient", BindingFlags.Public | BindingFlags.Instance)?.GetValue(exception) as bool?;

        return new ShardStartRefusal(reason, transient, exception.Message);
    }

    /// <summary>
    /// Whether JasperFx classifies <paramref name="exception" /> as transient (<c>TransientFailures.IsTransient</c>,
    /// JasperFx.Events 2.89, first in Marten 9.50.3), or <see langword="null" /> when it cannot say.
    /// </summary>
    public static bool? IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!RuntimeFeature.TransientFailureClassifier.IsAvailable(MartenRuntime.Current) || IsTransientMethod.Value is not { } method)
        {
            return null;
        }

        try
        {
            return method.Invoke(null, [exception]) as bool?;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether JasperFx calls <paramref name="exception" /> a systemic database failure - a non-transient
    /// <c>DbException</c> anywhere in its graph, which it never dead-letters (<c>ProjectionExceptions.IsSystemicDatabaseFailure</c>,
    /// JasperFx.Events 2.93, first in Marten 9.52, <see cref="RuntimeFeature.SystemicDatabaseFailure" />) - or
    /// <see langword="null" /> when it cannot say.
    /// </summary>
    public static bool? IsSystemicDatabaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (!RuntimeFeature.SystemicDatabaseFailure.IsAvailable(MartenRuntime.Current) || SystemicMethod.Value is not { } method)
        {
            return null;
        }

        try
        {
            return method.Invoke(null, [exception]) as bool?;
        }
        catch (TargetInvocationException)
        {
            return null;
        }
    }

    /// <summary>
    /// What a classified failure says about being a systemic database failure, where the daemon hands over its
    /// <see cref="ShardFailure" /> and not the exception: <see langword="true" /> for one JasperFx calls permanent
    /// (<c>ShardFailure.IsTransient</c> false) whose innermost exception is a <c>DbException</c>, <see langword="false" />
    /// for a transient one, and <see langword="null" /> otherwise - including below JasperFx.Events 2.93, and a permanent
    /// failure whose root is not a database exception, because the database error may sit further up its graph.
    /// </summary>
    public static bool? IsSystemicDatabaseFailure(ShardFailure? failure)
    {
        if (failure is null || !RuntimeFeature.SystemicDatabaseFailure.IsAvailable(MartenRuntime.Current))
        {
            return null;
        }

        if (typeof(ShardFailure).GetProperty("IsTransient", BindingFlags.Public | BindingFlags.Instance)?.GetValue(failure) is not bool transient)
        {
            return null;
        }

        if (transient)
        {
            return false;
        }

        return IsDatabaseExceptionType(failure.RootExceptionType) ? true : null;
    }

    /// <summary>
    /// Whether <paramref name="typeName" /> - a <c>ShardFailure</c>'s full type name, as JasperFx writes it - names a
    /// <see cref="System.Data.Common.DbException" /> among the assemblies this process has loaded.
    /// </summary>
    internal static bool IsDatabaseExceptionType(string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return false;
        }

        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? type;
            try
            {
                type = assembly.GetType(typeName, throwOnError: false);
            }
            catch (Exception exception) when (exception is ArgumentException or FileLoadException or BadImageFormatException)
            {
                continue;
            }

            if (type is not null)
            {
                return typeof(System.Data.Common.DbException).IsAssignableFrom(type);
            }
        }

        return false;
    }

    /// <summary>A sentence for a refusal: the reason, and whether a retry is expected to help, when JasperFx says.</summary>
    public static string Explain(ShardStartRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(refusal);

        string classified = refusal.Reason is { Length: > 0 } reason
            ? " (" + reason + (refusal.IsTransient switch
            {
                true => ", which a retry is expected to resolve",
                false => ", which a retry will not resolve on its own",
                null => string.Empty,
            }) + ")"
            : string.Empty;

        return "The daemon did not start it" + classified + ": " + refusal.Message;
    }
}
