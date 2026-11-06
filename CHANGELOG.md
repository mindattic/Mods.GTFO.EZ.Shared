# Changelog

## Unreleased

- **Infinite health (Insert) and infinite ammo (Delete) now cover the whole team** (GES-T12). The
  toggle used to live only on the presser's game, and GTFO applies damage on the game that owns the
  player (each human's own, the host's for its bots), so only the presser was protected - and bots
  were deliberately left out. Now a press goes to the host, which broadcasts the team's state over
  GTFO-API (repeated every few seconds while anything is on, and early in each level, for late
  joiners); every game blocks damage to the players it owns, bots included, and refills its own
  player's ammo. Every player sees "Infinite health: ON for the whole team (Name)". Every mod
  compiling `InfiniteToggles.cs` now needs GTFO-API and `RemoteEvents.cs` too.

## v17

- **`SharedOwner.Order` no longer lists `MindAttic.GTFO.CrashTestDummies`.** That mod dropped its
  Insert/Delete feature (and this submodule entirely) in its own v17, so the entry could never match
  again; removed rather than left dead. `GTFO.CrashTestDummies` and `GTFO.SkipDrop` both join the
  `vN` scheme at v17 too, so every GTFO.* mod now shares one version number.

## v16

- **Insert and Delete swapped: Insert is now infinite health, Delete is now infinite ammo.**
- **`InfiniteToggles.cs` always logs its on/off announcements to the console too**, not just as a
  fallback for when the event-log message fails.
- Joins the GTFO.EZ family's `vN` versioning scheme, starting here at v16 to match its current
  release.
