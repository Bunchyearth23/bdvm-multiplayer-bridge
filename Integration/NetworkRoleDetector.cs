using System;
using BDVM.Domain;

namespace BDVM.Adapters;

public sealed class NetworkRoleDetector : INetworkRoleDetector
{
    private readonly INetworkApiStateReader stateReader;

    public NetworkRoleDetector(INetworkApiStateReader stateReader) =>
        this.stateReader = stateReader ?? throw new ArgumentNullException(nameof(stateReader));

    public NetworkRoleReport Detect() => NetworkAuthorityPolicy.Classify(stateReader.Read());
}
