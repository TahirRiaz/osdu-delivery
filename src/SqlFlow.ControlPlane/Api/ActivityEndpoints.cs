using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SqlFlow.Catalog;

namespace SqlFlow.ControlPlane.Api;

/// <summary>One entry of an activity trace, streamed to the GUI's bottom trace panel: the DTO twin of
/// <see cref="CatalogActivityEvent"/>. <see cref="Terminal"/> marks the activity's final event and
/// <see cref="Status"/> its outcome ("succeeded"/"failed"); every non-terminal row leaves <see cref="Status"/>
/// null.</summary>
public sealed record ActivityEventDto(
    long Id, Guid ActivityId, string Kind, string SubjectKey, int Ordinal, DateTime TimestampUtc, string Level,
    string? Step, string? Message, bool Terminal, string? Status);

/// <summary>The payload of the activity stream's final <c>end</c> event: the newest activity's terminal status,
/// or "idle" when the subject has no trace yet.</summary>
public sealed record ActivityStreamEndDto(string Status);

/// <summary>
/// The general-purpose activity trace surface: any long-running control-plane operation writes an append-only
/// <see cref="CatalogActivityEvent"/> log (via <see cref="ActivityTrace"/>) keyed by a <c>kind</c> and a
/// <c>subject</c>, and the GUI tails it here exactly as the run trace tails a run's events. One live SSE stream
/// serves every operation (a repository sync, a lineage computation, and so on), so a new operation gets a live
/// trace panel for free without its own table or endpoint.
/// </summary>
public static class ActivityEndpoints
{
    // The same cadence as the run trace stream: a tail short enough that a new line reaches the browser under a
    // second, a heartbeat that keeps idle proxies from reaping the connection during a quiet phase.
    private static readonly TimeSpan TailInterval = TimeSpan.FromMilliseconds(750);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    public static RouteGroupBuilder MapActivityEndpoints(this RouteGroupBuilder group)
    {
        ArgumentNullException.ThrowIfNull(group);
        var activities = group.MapGroup("/activities").WithTags("Activities");
        activities.MapGet("/stream", StreamActivityAsync).WithName("StreamActivityTrace");
        return group;
    }

    /// <summary>
    /// The live activity trace as Server-Sent Events: one <c>entry</c> event per new
    /// <see cref="CatalogActivityEvent"/> for the given (<paramref name="kind"/>, <paramref name="subject"/>), then
    /// a single <c>end</c> event once the newest event is terminal (the operation finished) and the client has
    /// received it. The <paramref name="afterId"/> cursor resumes a dropped connection without replaying rows the
    /// client already holds; on a fresh connection (cursor 0) the retained scrollback of recent activities replays
    /// first. A subject with no trace at all ends immediately with an "idle" status.
    /// </summary>
    private static async Task<IResult> StreamActivityAsync(
        string kind, string subject, CatalogDbContext db, HttpContext http,
        IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions> json, long? afterId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(kind) || string.IsNullOrWhiteSpace(subject))
        {
            return TypedResults.Problem(
                detail: "kind and subject are required.", statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid request");
        }

        var response = http.Response;
        response.Headers.ContentType = "text/event-stream";
        response.Headers.CacheControl = "no-cache";
        // Tell buffering reverse proxies (nginx) to pass frames through as they are written.
        response.Headers["X-Accel-Buffering"] = "no";

        var serializer = json.Value.SerializerOptions;
        var cursor = afterId ?? 0L;
        var lastHeartbeat = DateTime.UtcNow;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                // The newest event for this (kind, subject): its id and terminal flag decide when the stream ends.
                var newest = await db.ActivityEvents.AsNoTracking()
                    .Where(e => e.Kind == kind && e.SubjectKey == subject)
                    .OrderByDescending(e => e.Id)
                    .Select(e => new { e.Id, e.Terminal, e.Status })
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);

                if (newest is null)
                {
                    // Nothing has ever been traced for this subject: end at once so the panel is not left hanging.
                    await WriteSseAsync(
                        response, "end", JsonSerializer.Serialize(new ActivityStreamEndDto("idle"), serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                var fresh = await db.ActivityEvents.AsNoTracking()
                    .Where(e => e.Kind == kind && e.SubjectKey == subject && e.Id > cursor)
                    .OrderBy(e => e.Id)
                    .Select(e => new ActivityEventDto(
                        e.Id, e.ActivityId, e.Kind, e.SubjectKey, e.Ordinal, e.TimestampUtc, e.Level, e.Step,
                        e.Message, e.Terminal, e.Status))
                    .ToListAsync(ct).ConfigureAwait(false);

                foreach (var entry in fresh)
                {
                    await WriteSseAsync(response, "entry", JsonSerializer.Serialize(entry, serializer), ct)
                        .ConfigureAwait(false);
                    cursor = entry.Id;
                    lastHeartbeat = DateTime.UtcNow;
                }

                // Terminal once the newest event is the activity's final one AND the client has received it. A newer
                // activity that begins after a terminal one (a fresh sync triggered while the panel is open) writes a
                // non-terminal event that becomes the newest, so the stream stays open through it.
                if (newest.Terminal && cursor >= newest.Id)
                {
                    await WriteSseAsync(
                        response, "end",
                        JsonSerializer.Serialize(new ActivityStreamEndDto(newest.Status ?? "unknown"), serializer), ct)
                        .ConfigureAwait(false);
                    break;
                }

                if (DateTime.UtcNow - lastHeartbeat >= HeartbeatInterval)
                {
                    await response.WriteAsync(": hb\n\n", ct).ConfigureAwait(false);
                    await response.Body.FlushAsync(ct).ConfigureAwait(false);
                    lastHeartbeat = DateTime.UtcNow;
                }

                await Task.Delay(TailInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // The client went away (tab closed, navigation): the normal way a live stream ends mid-activity.
        }

        return TypedResults.Empty;
    }

    private static async Task WriteSseAsync(HttpResponse response, string eventName, string data, CancellationToken ct)
    {
        await response.WriteAsync($"event: {eventName}\ndata: {data}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
