# Changelog

## Unreleased

- **Infinite toggles' team sync only claims "whole team" once the host confirms it** (GES-T14). A
  client's press applies on its own game and says "requested from the host..."; the host's broadcast
  then announces "for the whole team (Name)", and with no answer within 2 s it says the host doesn't
  have team sync, so only the presser has it. Every game says Hello to the host at level start (which
  also asks for the state), and announcements name any player in the level the host hasn't heard
  the protocol from ("except Bob (GTFO.* mod missing/outdated)"). The host repeats the state every
  5 s whenever it's in a level (not only while something is on) and right away when someone joins.
  A press is held per toggle, so a broadcast about one toggle can't undo a pending press of the
  other. Every message carries `SharedOwner.ProtocolVersion` (now 2); a peer on another version is
  ignored and named. Events are renamed (`...Request.v2`/`...State.v2`, plus a fixed-layout
  `...Hello`), so GES-T12/T13 builds never misread them. One failing GTFO-API call no longer turns
  team sync off for the session: only GTFO-API genuinely missing does, anything else retries, and
  event registration counts as done only once it succeeded.
- **The shared-keys owner is the newest copy, not the first mod in a fixed order** (GES-T14).
  `SharedOwner.ProtocolVersion` is read from every loaded copy and the highest wins (fixed order
  breaks ties). A copy from before it (no `ProtocolVersion`) that comes first still owns, since it
  would claim the keys anyway and two owners would double-toggle every press.
- **Infinite health (Insert) and infinite ammo (Delete) now cover the whole team** (GES-T12). The
  toggle used to live only on the presser's game, and GTFO applies damage on the game that owns the
  player (each human's own, the host's for its bots), so only the presser was protected - and bots
  were deliberately left out. Now a press goes to the host, which broadcasts the team's state over
  GTFO-API (repeated every few seconds while anything is on, and early in each level, for late
  joiners); every game blocks damage to the players it owns, bots included, and refills its own
  player's ammo. Every player sees "Infinite health: ON for the whole team (Name)". Every mod
  compiling `InfiniteToggles.cs` now needs GTFO-API and `RemoteEvents.cs` too.
- **Infinite ammo (Delete) tops everything off and covers grenades and tools** (GES-T13). Turning it
  on (or a teammate turning it on) fills every magazine in the backpack, not just the gun in hand;
  while it's on the consumable slot (Hand Grenades, glow sticks, mines...) is kept full as long as one
  is carried, placed sentries are kept full, and the host keeps its bots' ammo full too. Spending
  gun, tool or consumable ammo costs nothing while it's on (`PlayerAmmoStorage.UpdateAmmoInPack`/
  `UpdateBulletsInPack` see a spend of 0), so the last grenade in a stack can't be used up between
  two refills. Resource packs (med/ammo/tool packs) are left as they are.

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
