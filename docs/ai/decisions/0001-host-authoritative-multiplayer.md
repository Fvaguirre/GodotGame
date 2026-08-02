# ADR 0001: Host-authoritative multiplayer

- **Status:** accepted
- **Date:** (pre-existing; recorded here from `DEV_GUIDE.md`)

## Context
Co-op LAN game (ENet, ≤4 players, port 7777) with enemies, waves, bosses, and loot that must stay
consistent across machines. Peer-authoritative simulation would desync and duplicate spawns/damage.

## Decision
The **host simulates the entire world** (enemies, waves, bosses, loot, director). **Clients own only
their own avatar** and route their actions/damage to the host via RPC.

## Consequences
- Deterministic, cheat-resistant world state; one source of truth.
- Every gameplay feature must ask "who runs this?" — new sim logic runs host-side; clients request.
- Death mutates `Game.I.Enemies` synchronously → always iterate a `.ToArray()` snapshot.
- New AoE / on-death fan-out must be **budget-capped** (see ADR-adjacent gotcha in `DEV_HANDOFF.md`),
  or a cascade can freeze the host in MP.

## Alternatives rejected
- **Peer-to-peer / client-authoritative sim** — desync, duplicated entities, trust issues.

## Relevant files / symbols
`Game.cs` (world sim, director), `Enemy.cs:Hurt`/`RemoveEnemy`, the `Net`/RPC layer; `DEV_GUIDE.md`
(Multiplayer model), `DEV_HANDOFF.md` (fan-out budgets).
