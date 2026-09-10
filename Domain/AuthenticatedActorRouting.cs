using System;
using System.Collections.Generic;
using System.Linq;

namespace BDVM.Domain;

public sealed class AuthenticatedTransportActor
{
    public string TransportIdentity { get; set; } = "";
    public string PlayerId { get; set; } = "";
}

public static class AuthenticatedActorRouting
{
    public static string Resolve(string transportIdentity, string? localPlayerId, IEnumerable<string> existingPlayerIds,
        IEnumerable<AuthenticatedTransportActor> connectedActors)
    {
        var identity = (transportIdentity ?? "").Trim();
        if (identity.Length == 0 || identity.Length > CompanyProtocolLimits.MaximumIdBytes || identity.Any(char.IsControl))
            throw new UnauthorizedAccessException("A bounded authenticated transport identity is required.");

        if (!string.IsNullOrWhiteSpace(localPlayerId) && string.Equals(identity, localPlayerId, StringComparison.Ordinal))
            return localPlayerId!;

        var knownPlayers = (existingPlayerIds ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).ToArray();
        var exact = knownPlayers
            .Where(value => string.Equals(value, identity, StringComparison.Ordinal)).Distinct(StringComparer.Ordinal).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) throw new UnauthorizedAccessException("The authenticated transport identity is ambiguous.");

        var connected = (connectedActors ?? Array.Empty<AuthenticatedTransportActor>())
            .Where(value => value != null && string.Equals(value.TransportIdentity, identity, StringComparison.OrdinalIgnoreCase))
            .Select(value => value.PlayerId).Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).ToArray();
        if (connected.Length == 1) return connected[0];
        if (connected.Length > 1) throw new UnauthorizedAccessException("The authenticated transport identity is ambiguous.");

        // In a solo career there is no Multiplayer username to bind the HTTP Basic
        // principal to. Remote Dispatch has already authenticated and explicitly
        // authorized that principal, so the only existing economic player is the
        // unambiguous local actor. This fallback deliberately stops applying as soon
        // as another economic player exists; multiplayer identities must then use an
        // exact durable ID or an authenticated connected-peer binding.
        if (!string.IsNullOrWhiteSpace(localPlayerId) && knownPlayers.Length == 1 &&
            string.Equals(knownPlayers[0], localPlayerId, StringComparison.Ordinal))
            return localPlayerId!;
        throw new UnauthorizedAccessException("The authenticated transport identity is not linked to an economic player.");
    }
}
