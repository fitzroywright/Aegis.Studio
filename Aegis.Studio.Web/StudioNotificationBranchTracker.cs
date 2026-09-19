using System.Collections.Concurrent;

namespace Aegis.Studio.Web;

public sealed record StudioNotificationBranchObservation(
    string State,
    DateTimeOffset ObservedAtUtc,
    string? Detail);

public sealed class StudioNotificationBranchTracker
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, StudioNotificationBranchObservation>> flows =
        new(StringComparer.OrdinalIgnoreCase);

    public void Observe(
        string correlationId,
        string branch,
        bool failed,
        DateTimeOffset observedAtUtc,
        string? detail = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(correlationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(branch);

        ConcurrentDictionary<string, StudioNotificationBranchObservation> state =
            flows.GetOrAdd(correlationId.Trim(), _ => new(StringComparer.OrdinalIgnoreCase));

        state[branch.Trim()] = new(
            failed ? "Failed" : "Completed",
            observedAtUtc,
            detail);
    }

    public IReadOnlyDictionary<string, StudioNotificationBranchObservation> Get(string correlationId)
    {
        if (string.IsNullOrWhiteSpace(correlationId) ||
            !flows.TryGetValue(correlationId.Trim(), out ConcurrentDictionary<string, StudioNotificationBranchObservation>? state))
        {
            return new Dictionary<string, StudioNotificationBranchObservation>(StringComparer.OrdinalIgnoreCase);
        }

        return new Dictionary<string, StudioNotificationBranchObservation>(state, StringComparer.OrdinalIgnoreCase);
    }
}
