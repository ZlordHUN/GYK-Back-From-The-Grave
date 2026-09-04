# Source Layout

The mod remains one assembly with established namespaces. Physical folders group
files by responsibility for navigation; they do not define new assembly or API
boundaries.

- `Bootstrap/`: plugin entry point and configuration.
- `Features/`: self-contained optional features.
- `LocalCoop/`: experimental same-machine co-op.
- `Network/`: protocol, Steam transport, discovery, diagnostics, and session
  orchestration.
- `Multiplayer/`: synchronized state grouped by gameplay domain.
- `Patches/`: Harmony patches grouped by lifecycle and domain.
- `UI/`: interface components grouped by user surface.
- `Utils/`: shared helpers grouped by concern.

Every shared file must have the same relative path and content under
`Public Release/src/`. Intentional private differences are declared in
`scripts/public-parity-exclusions.json` and verified by
`scripts/check-public-parity.py`.
