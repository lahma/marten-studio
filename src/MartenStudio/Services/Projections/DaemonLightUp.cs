using System.Reflection;

using JasperFx.Events.Daemon;
using JasperFx.Events.Projections;

namespace MartenStudio.Services.Projections;

/// <summary>Why a shard did not start, as the daemon classified it.</summary>
/// <param name="Reason">
/// <c>ShardStartException.Reason</c> by name (<c>AgentPaused</c>, <c>HighWaterNotRunning</c>, <c>ShardNotRegistered</c>,
/// <c>StartRace</c>, <c>Faulted</c>, <c>Rebuilding</c>).
/// </param>
/// <param name="IsTransient">
/// <c>ShardStartException.IsTransient</c>: whether a retry is expected to resolve it.
/// </param>
/// <param name="Message">The exception's own message, which is the human half and is shown as it is.</param>
internal sealed record ShardStartRefusal(string? Reason, bool? IsTransient, string Message);

/// <summary>
/// The daemon's own classifications, asked in one place: whether it is rebuilding a shard, why a start was refused,
/// and whether a failure is transient or systemic.
/// </summary>
/// <remarks>
/// <para>
/// <b>Only a "yes" from <c>IsRebuilding</c> means anything.</b> It is a default interface method that answers
/// <see langword="false" /> for a daemon that does not track rebuilds, so <see langword="false" /> is "not known to be
/// rebuilding", never "not rebuilding" - and the studio's own operation tracker is asked as well.
/// </para>
/// </remarks>
internal static class DaemonLightUp
{
    /// <summary>
    /// Whether <paramref name="daemon" /> says it is rebuilding <paramref name="shard" />'s projection right now.
    /// <see langword="false" /> for a daemon that does not track it.
    /// </summary>
    public static bool IsRebuilding(IProjectionDaemon daemon, ShardName shard)
    {
        ArgumentNullException.ThrowIfNull(daemon);
        ArgumentNullException.ThrowIfNull(shard);

        try
        {
            return daemon.IsRebuilding(shard);
        }
        catch (Exception exception) when (exception is NotSupportedException or NotImplementedException)
        {
            return false;
        }
    }

    /// <summary>
    /// What a <see cref="ShardStartException" /> says about itself: its classified reason, whether it is transient, and
    /// its message.
    /// </summary>
    public static ShardStartRefusal Describe(ShardStartException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return new ShardStartRefusal(exception.Reason.ToString(), exception.IsTransient, exception.Message);
    }

    /// <summary>
    /// Whether JasperFx classifies <paramref name="exception" /> as transient (<c>TransientFailures.IsTransient</c>).
    /// </summary>
    public static bool IsTransient(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return TransientFailures.IsTransient(exception);
    }

    /// <summary>
    /// Whether JasperFx calls <paramref name="exception" /> a systemic database failure - a non-transient
    /// <c>DbException</c> anywhere in its graph, which it never dead-letters (<c>ProjectionExceptions.IsSystemicDatabaseFailure</c>).
    /// </summary>
    public static bool IsSystemicDatabaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        return ProjectionExceptions.IsSystemicDatabaseFailure(exception);
    }

    /// <summary>
    /// What a classified failure says about being a systemic database failure, where the daemon hands over its
    /// <see cref="ShardFailure" /> and not the exception: <see langword="true" /> for one JasperFx calls permanent
    /// (<c>ShardFailure.IsTransient</c> false) whose innermost exception is a <c>DbException</c>, <see langword="false" />
    /// for a transient one, and <see langword="null" /> otherwise - including a permanent failure whose root is not a
    /// database exception, because the database error may sit further up its graph.
    /// </summary>
    public static bool? IsSystemicDatabaseFailure(ShardFailure? failure)
    {
        if (failure is null)
        {
            return null;
        }

        if (failure.IsTransient)
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

    /// <summary>A sentence for a refusal: the reason, and whether a retry is expected to help.</summary>
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
