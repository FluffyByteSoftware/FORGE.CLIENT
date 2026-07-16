# ForgeClient

The Godot game client for **Stratum** — a custom C# multiplayer RPG
server targeting ~25 concurrent players in a persistent shared voxel
world. This repo owns everything that runs inside Godot: login/auth
flow, connection management, scenes, input, rendering, and (later)
client-side prediction. Server-side code lives in the sibling Stratum
repo (FluffyByteSoftware).

## Status

Working login client, verified live against the dev servers:

- TLS/TCP auth against the LoginServer — password and Ed25519 key
  paths, with per-account seed persistence ("remember me").
- Character create on a held-open connection with in-place name retry.
- UDP session against Sentinel: token admission, protocol version
  check, keep-alive echo, gameplay-channel diagnostic — held open
  until quit.

No gameplay, no patcher, no UI polish yet. Dev posture throughout:
accept-any TLS certificate and plaintext seed storage are flagged
pre-ship gates.

## Stack

- **Godot 4.7 Stable Mono** (C# / .NET)
- **LiteNetLib 2.1.4** — pinned to the server's version (skew is a
  wire hazard)
- **Shared / Networking / SystemTools** — consumed as cross-repo
  project references from the Stratum solution, read-only: the client
  owns zero schemas and zero wire formats. Library changes are
  designed as requests into the server repo and gate through its
  Probe.

## Layout
