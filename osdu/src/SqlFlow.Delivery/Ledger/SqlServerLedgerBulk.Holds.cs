using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using SqlFlow.Delivery.Data;

namespace SqlFlow.Delivery.Ledger;

/// <summary>
/// The statements behind the holds runs keep on the submissions they work on (<see cref="SubmissionHold"/>): a shared,
/// session-owned application lock per submission, taken on a connection the hold keeps open, and the take-over a later run
/// makes of a submission nobody holds any more.
/// </summary>
internal static partial class SqlServerLedgerBulk
{
    // A run working on a submission holds it shared, waiting at most @timeout milliseconds: only a take-over's exclusive
    // probe (below) can stand in its way, and only for as long as one statement takes.
    private const string HoldSubmissionSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Shared', @LockOwner = N'Session', @LockTimeout = @timeout;
        SELECT CAST(@granted AS int);
        """;

    // A take-over asks for the submission exclusively without waiting, which it is granted only when no session holds it:
    // the run that registered it, its members and any run naming it have all ended. It then holds it shared, as a run
    // working on it does, so a run naming the submission meanwhile is not kept waiting; another take-over still finds it
    // held. Between the release and the shared request only another take-over's probe can come, and then this one is not
    // granted and leaves the submission to that one.
    private const string TakeOverSubmissionSql = """
        SET NOCOUNT ON;
        DECLARE @granted int;
        EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = 0;
        IF @granted >= 0
        BEGIN
            EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
            EXEC @granted = sys.sp_getapplock @Resource = @resource, @LockMode = N'Shared', @LockOwner = N'Session', @LockTimeout = 0;
        END;
        SELECT CAST(@granted AS int);
        """;

    // A hold ends with the session that took it; letting it go sooner is this.
    private const string ReleaseSubmissionSql = """
        SET NOCOUNT ON;
        EXEC sys.sp_releaseapplock @Resource = @resource, @LockOwner = N'Session';
        """;

    /// <summary>How long letting go of a hold may take before its connection is given up instead.</summary>
    private const int ReleaseTimeoutSeconds = 30;

    /// <summary>
    /// The statement that lets go of the hold on <paramref name="resource"/> on <paramref name="connection"/>, the
    /// connection that took it; the caller runs it, as it disposes, and owns the command.
    /// </summary>
    public static SqlCommand ReleaseSubmission(SqlConnection connection, string resource)
    {
        var command = Command(connection, null, ReleaseSubmissionSql, slice: null);
        command.CommandTimeout = ReleaseTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        return command;
    }

    /// <summary>
    /// Takes the shared hold on <paramref name="resource"/> on <paramref name="db"/>'s connection, which it leaves open for
    /// the hold to keep, waiting at most <paramref name="timeoutMs"/>. Returns <c>sp_getapplock</c>'s answer: 0 or 1
    /// granted, below 0 not.
    /// </summary>
    public static Task<int> HoldSubmissionAsync(OsduDbContext db, string resource, int timeoutMs, CancellationToken ct)
        => LockAsync(db, HoldSubmissionSql, resource, timeoutMs, ct);

    /// <summary>
    /// Takes over <paramref name="resource"/> on <paramref name="db"/>'s connection, which it leaves open for the hold to
    /// keep, when no session holds it. Returns <c>sp_getapplock</c>'s answer: 0 or 1 taken and held shared, below 0 held by
    /// another session.
    /// </summary>
    public static Task<int> TakeOverSubmissionAsync(OsduDbContext db, string resource, CancellationToken ct)
        => LockAsync(db, TakeOverSubmissionSql, resource, 0, ct);

    private static async Task<int> LockAsync(OsduDbContext db, string sql, string resource, int timeoutMs, CancellationToken ct)
    {
        await db.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        var connection = (SqlConnection)db.Database.GetDbConnection();
        await using var command = Command(connection, null, sql, slice: null);
        // The wait is bounded by @timeout on the server; the command's own timeout is longer, so the answer always arrives.
        command.CommandTimeout = (timeoutMs / 1000) + BulkTimeoutSeconds;
        command.Parameters.Add(new SqlParameter("@resource", SqlDbType.NVarChar, 255) { Value = resource });
        command.Parameters.Add(new SqlParameter("@timeout", SqlDbType.Int) { Value = timeoutMs });
        var answer = await command.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return Convert.ToInt32(answer, CultureInfo.InvariantCulture);
    }
}
