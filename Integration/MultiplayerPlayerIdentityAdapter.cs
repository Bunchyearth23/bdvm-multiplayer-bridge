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

    public static string RequirePersistentLocalPlayerId(IClient client)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (!(client is IPersistentLocalPlayerIdentity persistent))
            throw new InvalidDataException("The Multiplayer client does not expose its authenticated persistent identity.");
        return PlayerIdentity.FromMultiplayerGuid(persistent.PersistentId);
    }
}
