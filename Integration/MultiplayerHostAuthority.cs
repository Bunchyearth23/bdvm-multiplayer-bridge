using BDVM.Domain;
using MPAPI;

namespace BDVM.Adapters;

public sealed class MultiplayerNetworkApiStateReader : INetworkApiStateReader
{
    public NetworkApiState Read()
    {
        if (RuntimeAuthorityMode.IsDelegated) return new NetworkApiState { ExternalAuthorityConfigured = true };
        var api = MultiplayerAPI.Instance;
        return new NetworkApiState
        {
            ApiAvailable = MultiplayerAPI.IsMultiplayerLoaded && api != null && api.IsMultiplayerLoaded,
            IsConnected = api?.IsConnected ?? false,
            IsHost = api?.IsHost ?? false,
            IsSinglePlayer = api?.IsSinglePlayer ?? false
        };
    }
}
