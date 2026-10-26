using System.Globalization;

namespace PostgresStudio.Internal.Sql;

/// <summary>
/// The two statements Sessions › Blocking and C12 hand to a person who may end another session (X3, K2): text to copy,
/// built here with the process id as a number, and never run by the studio.
/// </summary>
/// <remarks>
/// <para>
/// <b>The studio never runs either</b> (plan K2, issue #8's non-goal): it has no button that cancels or ends a session, and
/// nothing in either package executes these. They are words for whoever runs the database - a superuser, a member of the
/// session's own role, or of <c>pg_signal_backend</c>, which cannot signal a superuser's session.
/// </para>
/// <para>
/// <b>What each does.</b> <c>pg_cancel_backend</c> cancels the statement a session is running; inside an explicit
/// transaction that leaves the transaction aborted and its locks held until the application rolls it back, and to a session
/// idle in its transaction it does nothing at all. <c>pg_terminate_backend</c> ends the session, which rolls its transaction
/// back and releases every lock it held.
/// </para>
/// </remarks>
internal static class SessionHandOver
{
    /// <summary><c>select pg_terminate_backend(4242);</c></summary>
    /// <param name="processId">The session's process id.</param>
    public static string Terminate(int processId) =>
        "select pg_terminate_backend(" + processId.ToString(CultureInfo.InvariantCulture) + ");";

    /// <summary><c>select pg_cancel_backend(4242);</c></summary>
    /// <param name="processId">The session's process id.</param>
    public static string Cancel(int processId) =>
        "select pg_cancel_backend(" + processId.ToString(CultureInfo.InvariantCulture) + ");";
}
