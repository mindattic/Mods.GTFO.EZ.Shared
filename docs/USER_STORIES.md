---
codex: 1
project: Mods.GTFO.EZ.Shared
code: GES
layer: stories
status: living
updated: 2026-10-20
---
<!-- GENERATED from MindAttic.Tickets/data/Tickets.db by `tickets db render`. Do not edit: this file is rewritten from the database; change stories and tickets with the tickets CLI. -->

# Mods.GTFO.EZ.Shared — User Stories
> ✅ done (shipped & tested) · 🟡 partial · ⬜ planned. Every ✅ cites the test.

## Priority backlog
_Nothing recorded yet._

## Tickets
> Permanent log of every change and fix — this repo's ticket system. Rows are append-only: never
> delete or reword one; only move its Status (open → closed / wont-do) and fill in Ref. Status is
> a word, not a glyph, so tickets don't count toward the story index. Keep this section last.

| ID | Date | Type | Status | Summary | Ref |
|---|---|---|---|---|---|
| GES-T1 | 2026-10-20 | docs | closed | Start the permanent ticket log in the workflow database | — |
| GES-T2 | 2026-10-20 | docs | closed | Publish GTFO.EZ.Shared docs to GitHub Pages via the shared Agency DocFX pipeline: run adopt-docs-site.ps1 (README.md as home page, docs/*.md as articles, API reference only where the code builds in CI), enable Pages with build_type=workflow, set the repo homepage URL, retire tools/build-readme.ps1 and README.htm, confirm the docs Actions run is green and https://mindattic.github.io/GTFO.EZ.Shared/ returns 200. Depends on MAT-T8. | .github/workflows/docs.yml |
| GES-T3 | 2026-10-20 | chore | open | Bug hunt: run an exhaustive /bughunt over the whole repo and fix every real bug found (each fix under its own fix ticket), then hunt again; close this only when a full pass finds nothing | user request 2026-10-20: approved repo sweep gap-fill |
| GES-T4 | 2026-10-21 | chore | closed | Add docs | — |
| GES-T5 | 2026-10-24 | docs | closed | Update USER_STORIES.md | — |
| GES-T6 | 2026-10-24 | feature | closed | Insert/Delete toggle announcements always log to the console too, not just as a fallback when the event-log message fails | InfiniteToggles.cs |
| GES-T7 | 2026-10-24 | feature | closed | Swap Insert and Delete: Insert is now infinite health, Delete is now infinite ammo | InfiniteToggles.cs |
| GES-T8 | 2026-10-24 | docs | closed | Put GTFO.EZ.Shared on the GTFO.EZ family's vN versioning scheme, starting at v16: new CHANGELOG.md, README Versioning section | CHANGELOG.md |
| GES-T9 | 2026-10-24 | chore | closed | v17: drop MindAttic.GTFO.CrashTestDummies from SharedOwner.Order (it no longer carries this code); refresh README to reflect the 4-mod family and GTFO.CrashTestDummies/GTFO.SkipDrop joining the vN scheme | SharedOwner.cs |
| GES-T10 | 2026-11-01 | chore | closed | Rename repo to Mods.GTFO.EZ.Shared for mod organization (GitHub + folder + refs) | 0ef398838c13c840ee6fd5127e955b858a607254 |
| GES-T11 | 2026-11-02 | fix | closed | Bughunt sweep, two passes: find and fix real bugs across tracked source (user request 2026-11-02) | — |
| GES-T12 | 2026-11-06 | fix | closed | Infinite health (Insert) only protects the player who pressed it; it must cover the whole team | 07d78df |
| GES-T13 | 2026-11-06 | fix | closed | Infinite ammo: turning it on should top off all ammo first, and it should also cover throwables (grenades) and tool ammo | 62cb97a |
| GES-T14 | 2026-11-06 | fix | closed | Infinite toggles' team sync: confirm through the host before announcing 'whole team' (warn when the host has no sync / a peer is missing it), no permanent latch-off on one NetworkAPI error, always-on host heartbeat + state on join, per-toggle grace, owner chosen by highest ProtocolVersion not fixed mod order, protocol version in messages | 39b34a4 |
| GES-T15 | 2026-11-10 | chore | closed | Crash recovery: commit the command-catalog sync (.agent/commands/*.md) and regenerated docs/USER_STORIES.md left uncommitted by the session crash | — |
