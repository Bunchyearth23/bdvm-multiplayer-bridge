using System;
using System.IO;
using BDVM.Domain;
using MPAPI.Interfaces;

namespace BDVM.Adapters;

public static class MultiplayerPlayerIdentityAdapter
{
    public static string RequirePersistentPlayerId(IPlayer player)
    {
        if (player == null) throw new ArgumentNullException(nameof(player));
        if (!(player is IPersistentPlayerIdentity persistent))
            throw new InvalidDataException("The Multiplayer player wrapper does not expose an authoritative persistent identity.");
        return PlayerIdentity.FromMultiplayerGuid(persistent.PersistentId);
    }
}
