# BDVM - Multiplayer Bridge

`BDVM.MultiplayerBridge` carries BDVM's host-authoritative protocol over a compatible Derail Valley Multiplayer installation. It lets remote players observe shared state and submit validated intents without giving clients ownership of the economy.

## Status

| Property | Value |
| --- | --- |
| Module kind | Optional runtime bridge |
| Target framework | .NET Framework 4.8 (`net48`) |
| Build contract | `BDVM.Common` |
| Runtime dependencies | `Multiplayer`, `MultiplayerAPI` |
| Standalone | No |

The Multiplayer mod and its DLLs are not bundled. If Multiplayer is absent, omit this bridge; solo BDVM features remain usable. The current `BDVM.Full` bundle declares Multiplayer as a requirement because it includes this bridge.

## Responsibilities

- Define bounded protocol envelopes, message types, company intents and results.
- Validate payload size, identity, intent shape and protocol version before execution.
- Execute economic mutations only through the authoritative host.
- Return snapshots and results to clients while preventing direct client-side writes.
- Journal request IDs and track client requests so retries cannot duplicate an operation.
- Resolve persistent multiplayer identities instead of relying on transient connection order.
- Adapt packet registration and peer roles to the compatible `MultiplayerAPI`.

## Key surfaces

The portable protocol includes `CompanyProtocolEnvelope`, `CompanyIntent`, `ProtocolResult`, `CompanyProtocolHost`, codecs, validators and request journals. Runtime adapters include server/client protocol adapters, `PersistentMultiplayerPeerIdentityResolver`, `RuntimeCompanyIntentExecutor`, role detection and authoritative state readers.

## Boundaries

This repository is not the Multiplayer mod, does not provide networking by itself and never promotes a client to economic authority. If the API is missing, incompatible or unable to identify a peer safely, the bridge fails closed.

## Dependencies and composition

The small project builds against `BDVM.Common`; `Domain/` and `Integration/` are temporarily linked into `BDVM.Full`, where the integration compiles against `MultiplayerAPI`. Standalone packaging will make those runtime requirements explicit without bundling the upstream DLLs.

## Build

The marker assembly can be built with Common beside this repository:

```powershell
dotnet build .\BDVM.MultiplayerBridge.csproj -c Release
```

To compile the actual runtime adapter, build `BDVM.Full` with the compatible `MultiplayerAPI.dll` available either from the integration workspace or under the game's `Mods/Multiplayer` directory.

## Testing and installation

BDVM domain tests cover validation, journaling, host execution and client observation. The Multiplayer fork retains its own validation suite. Install matching versions of `BDVM.Full` and the Multiplayer fork; do not install this DLL alone.

## Upstream and provenance

- BDVM fork: [Bunchyearth23/dv-multiplayer](https://github.com/Bunchyearth23/dv-multiplayer), branch `bdvm-integration`.
- Direct upstream: [AMacro/dv-multiplayer](https://github.com/AMacro/dv-multiplayer).
- Earlier upstream: [Insprill/dv-multiplayer](https://github.com/Insprill/dv-multiplayer).
- Upstream license: Apache-2.0.

API consumption remains separated from adapted upstream code, and all required notices must be preserved.

## Compatibility

Protocol changes must remain backward-compatible within a major line or use an explicit new protocol version. Unknown messages, duplicate request IDs and non-host mutation paths are rejected.

## License

Licensed under the Apache License, Version 2.0. See [LICENSE](LICENSE).
