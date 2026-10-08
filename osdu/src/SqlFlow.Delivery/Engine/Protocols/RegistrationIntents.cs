using SqlFlow.Delivery.Ledger;
using SqlFlow.Delivery.Protocols;

namespace SqlFlow.Delivery.Engine.Protocols;

/// <summary>One registration about to go under an id the route chose: the route's slot for it, and the id.</summary>
internal sealed record Registration(string Slot, string Id);

/// <summary>What one registration was declared to write, or why it could not be declared (nothing is then registered).</summary>
internal sealed record RegistrationIntent(Registration Registration, TargetArtifact? Artifact, string? Refusal);

/// <summary>
/// The intents of registrations under ids a route chooses itself (docs/atomic-delivery-plan.md, The routes): a dataset record,
/// the dataset a record of another kind keeps its files in, a workflow's inputs. The Dataset service writes such a registration
/// through storage under the id it is given, so the registration's answer can be lost after it landed. Storage says, before
/// it is sent, whether it holds each id: a registration of an id it holds writes a new version of it
/// (<see cref="ArtifactRoles.Version"/>, naming the version an undo writes back), and one of an id it does not hold creates it
/// (<see cref="ArtifactRoles.Record"/>, which an undo removes once storage confirms OSDU created it after the unit began).
/// </summary>
internal static class RegistrationIntents
{
    /// <summary>
    /// Asks storage for every id of <paramref name="registrations"/> in its batched read (<c>POST /query/records</c>), and
    /// answers each with its intent, or with why storage could not say, aligned with <paramref name="registrations"/>.
    /// </summary>
    public static async Task<IReadOnlyList<RegistrationIntent>> DeclareAsync(OsduRecordProtocol storage, IReadOnlyList<Registration> registrations, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(registrations);
        if (registrations.Count == 0)
        {
            return [];
        }

        var held = await storage.VerifyBatchAsync(registrations.Select(r => new VerifyRequest(r.Id, null)).ToList(), ct).ConfigureAwait(false);
        var intents = new List<RegistrationIntent>(registrations.Count);
        for (var i = 0; i < registrations.Count; i++)
        {
            var registration = registrations[i];
            var read = i < held.Count ? held[i] : null;
            if (read is null || read.Outcome == VerifyOutcome.Error || (read.Outcome != VerifyOutcome.Missing && read.ObservedVersion is null))
            {
                intents.Add(new RegistrationIntent(registration, null, $"storage could not say whether it holds {registration.Id} ({read?.Detail ?? "no version in its answer"}), so what its registration would replace is not known; nothing was registered"));
                continue;
            }

            var exists = read.Outcome != VerifyOutcome.Missing;
            intents.Add(new RegistrationIntent(registration, new TargetArtifact
            {
                Slot = registration.Slot,
                Role = exists ? ArtifactRoles.Version : ArtifactRoles.Record,
                TargetId = registration.Id,
                PriorVersion = exists ? read.ObservedVersion : null,
                Status = ArtifactStatus.Intent,
            }, null));
        }

        return intents;
    }

    /// <summary>The intent of <paramref name="intent"/> once its registration landed, at the version the Dataset service answered.</summary>
    public static TargetArtifact Landed(TargetArtifact intent, long? version)
    {
        ArgumentNullException.ThrowIfNull(intent);
        return intent with { Version = version, Status = ArtifactStatus.Pending };
    }
}
