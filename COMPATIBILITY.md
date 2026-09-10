# Compatibility matrix

This file is the published compatibility contract for **BDVM.MultiplayerBridge 1.1.0**. It describes the current incremental release; it is not a promise that BDVM is feature-complete.

## Current line

| Surface | Accepted line | Refusal behavior |
| --- | --- | --- |
| Module API | 1.x | An older or newer major is refused before registration. |
| Checkpoint | `bdvm.checkpoint` schema 2 | Unknown schemas and unsupported legacy packages are refused without mutation. |
| Web API | 1.0 when applicable | An incompatible web module is disabled without stopping the host or other modules. |
| External runtime dependencies | BDVM Multiplayer fork and `MultiplayerAPI` 1.4.0 or later | A missing or older API is refused before the bridge registers because it cannot provide authenticated durable individual wallets. |

## Supported profiles

This release is selected by these dependency-closed profiles: **complete**. The canonical machine-readable profiles live in the BDVM integration workspace and are validated before packaging. The minimal profile contains no Multiplayer or MultiplayerAPI dependency.

## Package transitions

| Scenario | Policy |
| --- | --- |
| Module absent | Preserve its opaque payload and suspend its workflows; do not sell, refund, delete or migrate implicitly. |
| Module reinstalled | Resume only after dependency, API and state-schema validation. |
| Same-major upgrade | Supported when the persisted schema is unchanged or an explicit migration exists. |
| Same-major downgrade | Supported only when the older reader explicitly accepts the current persisted schema. |
| Older major | Refuse before registration and leave authoritative state untouched. |

Game, save/reload and host/client behavior still requires the release's documented manual runtime campaign. Repository CI validates this contract and metadata; it does not claim Unity runtime validation.
