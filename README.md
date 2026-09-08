# BDVM - Multiplayer Bridge

Optional host-authoritative protocol and runtime adapter for Derail Valley Multiplayer.

## Dependency status

This module is **not standalone**. Its network features require:

- `BDVM.Common`;
- a compatible installation of [Derail Valley Multiplayer](https://github.com/Bunchyearth23/dv-multiplayer) providing `MultiplayerAPI`.

The Multiplayer mod and its DLLs are not bundled here. If Multiplayer is absent, omit this bridge; the solo BDVM modules remain usable. The bridge fails closed instead of emulating a network authority.

## Upstream and provenance

BDVM fork: https://github.com/Bunchyearth23/dv-multiplayer, branch `bdvm-integration`. Upstream: https://github.com/AMacro/dv-multiplayer, continued from https://github.com/Insprill/dv-multiplayer (Apache-2.0). API consumption is kept separate from any adapted upstream code and required notices must be preserved.

## License

Licensed under the Apache License, Version 2.0. See `LICENSE`.
