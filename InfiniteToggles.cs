using System;
using System.Collections.Generic;
using System.IO;
using Gear;
using GTFO.API;
using HarmonyLib;
using Il2CppInterop.Runtime;
using Player;
using SNetwork;
using UnityEngine;

namespace MindAttic.GTFO.Shared;

// Insert toggles infinite health, Delete infinite ammo, in every GTFO.* mod, for the whole team:
// whoever presses it, every player gets it (GES-T12, GES-T13). Each change is announced in the
// event log (MessageHelper) and always in the console too, where it can't cut off a warden
// objective. Only the owning copy (SharedOwner) reads the keys, syncs and refills ammo; the on/off
// state is shared, so every copy's damage block and any mod's own guns (InfiniteAmmoOn) agree.
// Off at every game launch, and they stay as set across expeditions until toggled again.
//
// Why it has to be synced: GTFO applies damage to a player on the machine that owns that player -
// each human on their own game, the host's bots on the host (Dam_PlayerDamageLocal.ReceiveXDamage
// runs there; the host's enemies just send the hit over). Ammo is the same: each player's backpack
// lives on their own game. So the toggle has to reach every machine, which then protects and
// refills its own player (and the host its bots). The host holds the team's state: a client's
// press goes to the host, which broadcasts it to everyone (InfiniteSync).
internal static class InfiniteToggles
{
    internal const string AmmoKey = "InfiniteAmmo";
    internal const string HealthKey = "InfiniteHealth";

    internal static bool InfiniteAmmoOn => SharedOwner.Get(AmmoKey);
    internal static bool InfiniteHealthOn => SharedOwner.Get(HealthKey);

    private static readonly InventorySlot[] AmmoSlots = { InventorySlot.GearStandard, InventorySlot.GearSpecial, InventorySlot.GearClass };

    private const float SentryRefillSeconds = 0.5f;
    private const float SyncErrorLogSeconds = 10f;

    private static int _lastFrame = -1;
    private static bool _ammoWasOn;
    private static float _nextSentryRefill;
    private static bool _syncMissing;
    private static float _nextSyncError;

    internal static void Tick()
    {
        // The host's bots run the same Update; once a frame is enough.
        if (Time.frameCount == _lastFrame || !SharedOwner.IsOwner) return;
        _lastFrame = Time.frameCount;

        Sync(InfiniteSync.Tick);

        if (FocusStateManager.CurrentState == eFocusState.FPS)
        {
            if (Input.GetKeyDown(KeyCode.Insert)) Press(HealthKey);
            if (Input.GetKeyDown(KeyCode.Delete)) Press(AmmoKey);
        }

        var ammoOn = InfiniteAmmoOn;
        if (ammoOn)
        {
            // Just switched on (here or by a teammate): every magazine full too, not just the one in hand.
            if (!_ammoWasOn) TopOffClips();
            Refill();
        }
        _ammoWasOn = ammoOn;
    }

    internal static string Name(string key) => key == HealthKey ? "Infinite health" : "Infinite ammo";

    internal static string OnOff(bool on) => on ? "ON" : "OFF";

    // Applied here straight away; what it says depends on who can confirm it for the team
    // (InfiniteSync.Pressed: the host says so at once, a client waits for the host's answer).
    private static void Press(string key)
    {
        var on = !SharedOwner.Get(key);
        SharedOwner.Set(key, on);
        if (!Sync(() => InfiniteSync.Pressed(key, on)))
            Show($"{Name(key)}: {OnOff(on)} - team sync unavailable, only you have it");
    }

    // GTFO-API comes with BepInExPack_GTFO, so it's always there; if it genuinely isn't (the call
    // can't even be linked), team sync stays off for the session and the toggles still work for this
    // machine (the presser and, on the host, its bots). Any other failure is one call going wrong:
    // logged (at most every few seconds - this runs every frame) and tried again next time.
    private static bool Sync(Action action)
    {
        if (_syncMissing) return false;
        try
        {
            action();
            return true;
        }
        catch (Exception e) when (IsMissingApi(e))
        {
            _syncMissing = true;
            SharedOwner.Log.LogWarning($"Infinite health/ammo can't reach teammates (GTFO-API missing) - only this game gets it. {e.Message}");
        }
        catch (Exception e)
        {
            if (Time.unscaledTime >= _nextSyncError)
            {
                _nextSyncError = Time.unscaledTime + SyncErrorLogSeconds;
                SharedOwner.Log.LogWarning($"Infinite health/ammo team sync failed (will retry). {e}");
            }
        }
        return false;
    }

    private static bool IsMissingApi(Exception e) => e switch
    {
        FileNotFoundException or FileLoadException or TypeLoadException or MissingMemberException => true,
        TypeInitializationException { InnerException: { } inner } => IsMissingApi(inner),
        _ => false,
    };

    // Always in the console, not just as a fallback for when the event log message fails - so the
    // on/off state is never just on-screen text that scrolled past.
    internal static void Show(string message)
    {
        try
        {
            MessageHelper.Show(message, MessageType.EventLog);
        }
        catch (Exception e)
        {
            SharedOwner.Log.LogInfo($"{message} (no warden message: {e.Message})");
            return;
        }
        SharedOwner.Log.LogInfo(message);
    }

    // ── Ammo ──────────────────────────────────────────────────────────────

    // Ammo types that never go down while infinite ammo is on: both guns, the tool and the
    // consumable slot (grenades, glow sticks, mines...). Resource packs (med/ammo/tool packs) are
    // left alone - they're supplies, not ammo.
    internal static bool KeepsFull(AmmoType type) =>
        (type is AmmoType.Standard or AmmoType.Special or AmmoType.Class or AmmoType.CurrentConsumable) && InfiniteAmmoOn;

    // Every magazine in the backpack filled, for the local player and (host) its bots.
    private static void TopOffClips()
    {
        try
        {
            PlayerBackpackManager.LocalBackpack?.AmmoStorage?.FillAllClips();
            if (!SNet.IsMaster) return;
            foreach (var agent in PlayerManager.PlayerAgentsInLevel)
                if (IsBot(agent) && PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack))
                    backpack?.AmmoStorage?.FillAllClips();
        }
        catch (Exception e)
        {
            SharedOwner.Log.LogWarning($"Infinite ammo: couldn't fill every magazine. {e.Message}");
        }
    }

    // The local player's ammo, the host's bots' ammo and every placed sentry's, all back to full.
    private static void Refill()
    {
        try
        {
            if (PlayerManager.TryGetLocalPlayerAgent(out var player) && player != null && player.Alive)
                Refill(player, PlayerBackpackManager.LocalBackpack, true);

            if (SNet.IsMaster)
                foreach (var agent in PlayerManager.PlayerAgentsInLevel)
                    if (IsBot(agent) && agent.Alive && PlayerBackpackManager.TryGetBackpack(agent.Owner, out var backpack))
                        Refill(agent, backpack, false);

            if (Time.unscaledTime >= _nextSentryRefill)
            {
                _nextSentryRefill = Time.unscaledTime + SentryRefillSeconds;
                RefillSentries();
            }
        }
        catch (Exception e)
        {
            SharedOwner.Log.LogWarning($"Infinite ammo: refill failed. {e.Message}");
        }
    }

    private static bool IsBot(PlayerAgent? agent) => agent != null && agent.Owner != null && agent.Owner.IsBot;

    // Tops every gear slot's reserve, the consumable slot and the wielded gun's magazine back up to
    // full. Only the local player has a HUD to update.
    private static void Refill(PlayerAgent player, PlayerBackpack? backpack, bool isLocal)
    {
        var storage = backpack?.AmmoStorage;
        if (backpack == null || storage == null) return;

        var changed = false;
        foreach (var slot in AmmoSlots)
        {
            var ammo = storage.GetInventorySlotAmmo(slot);
            if (ammo == null || ammo.AmmoInPack >= ammo.AmmoMaxCap) continue;
            storage.SetAmmo(ammo.AmmoType, ammo.AmmoMaxCap);
            changed = true;
        }

        // Grenades and the like: only while one is actually carried, or an empty slot would get a
        // stale count that the next pickup adds to.
        var consumable = storage.ConsumableAmmo;
        if (consumable != null && consumable.AmmoInPack < consumable.AmmoMaxCap
            && backpack.TryGetBackpackItem(InventorySlot.Consumable, out var item) && item != null)
        {
            storage.SetAmmo(AmmoType.CurrentConsumable, consumable.AmmoMaxCap);
            changed = true;
        }

        var gun = player.Inventory?.WieldedItem?.TryCast<BulletWeapon>();
        if (gun != null && gun.GetCurrentClip() < gun.ClipSize)
        {
            gun.SetCurrentClip(gun.ClipSize);
            storage.SetClipAmmoInSlot(player.Inventory!.WieldedSlot);
            changed = true;
        }

        if (!changed) return;
        if (isLocal) storage.UpdateAllAmmoUI();
        storage.NeedsSync = true;
    }

    // Placed sentries draw on their own ammo, not the backpack's. Set on every machine: the host's
    // copy decides whether the sentry fires, each client's shows the count.
    private static void RefillSentries()
    {
        foreach (var obj in UnityEngine.Object.FindObjectsOfType(Il2CppType.Of<SentryGunInstance>()))
        {
            var sentry = obj?.TryCast<SentryGunInstance>();
            if (sentry == null) continue;
            var max = sentry.AmmoMaxCap;
            if (max > 0f && sentry.Ammo < max) sentry.Ammo = max;
        }
    }

    // ── Health ────────────────────────────────────────────────────────────

    // Damage to any player this game owns - you, and on the host its bots - is dropped while
    // infinite health is on. Every teammate's own game does the same for them (the state is synced).
    // Runs inside every damage call, so it never throws: anything unexpected lets the damage through.
    internal static bool AllowDamage(Dam_PlayerDamageLocal damage)
    {
        if (!InfiniteHealthOn) return true;
        try
        {
            return damage.Owner == null;
        }
        catch (Exception)
        {
            return true;
        }
    }
}

// The team's toggle state over GTFO-API. The host is the authority: a client's press is applied on
// the client straight away (pending) and sent to the host as a request; the host applies it and
// broadcasts the state to everyone, naming who pressed it, and only that answer is announced as
// "for the whole team". No answer within RequestGraceSeconds means the host has no team sync (mod
// missing, outdated or switched off), and the client is told only it has it.
//
// Every game says Hello to the host when a level starts (or the host changes); the host answers
// with the state, and remembers which players speak this ProtocolVersion so every announcement can
// name the ones who don't ("except Bob"). While in a level the host repeats the state every few
// seconds, whatever it is (a client still carrying an old state from another lobby ends up
// matching), and right away when someone new is in the level. Every message carries
// SharedOwner.ProtocolVersion; one from a different version is ignored and its sender named.
//
// Only the owning copy calls in here, and all of it is kept out of InfiniteToggles' own methods so a
// missing GTFO-API can't stop those from compiling.
internal static class InfiniteSync
{
    // Hello's name and layout never change, so games on any later version can still tell each other
    // apart; the others are renamed whenever their layout changes, so an older game never misreads
    // one (GES-T12/T13's were "...Request"/"...State" without a suffix).
    private const string HelloEvent = "MindAttic.GTFO.InfiniteToggles.Hello";
    private const string RequestEvent = "MindAttic.GTFO.InfiniteToggles.Request.v2";
    private const string StateEvent = "MindAttic.GTFO.InfiniteToggles.State.v2";

    private const byte HealthBit = 1;
    private const byte AmmoBit = 2;
    private static readonly byte[] Bits = { HealthBit, AmmoBit };

    private const float HeartbeatSeconds = 5f;
    private const float RosterCheckSeconds = 0.5f;
    private const float RequestGraceSeconds = 2f;
    private const float RehelloSeconds = 5f;

    // Plain fields so GTFO-API can copy them as raw bytes.
    private struct Hello
    {
        public int Version; // SharedOwner.ProtocolVersion; also asks the host for the state
    }

    private struct Request
    {
        public int Version;
        public byte Key; // HealthBit or AmmoBit
        public byte On;
    }

    private struct State
    {
        public int Version;
        public byte Flags;   // HealthBit | AmmoBit, whichever are on
        public byte Changed; // the bit a press just set (the answer to it), 0 for a repeat
        public ulong By;     // who pressed it (player lookup), 0 for a repeat
        // Up to three players in the level the host hasn't heard this protocol from (0 = none):
        // their toggles don't follow the team's.
        public ulong Missing1, Missing2, Missing3;
    }

    private static bool _registered;
    private static float _lastTick = -100f;
    private static float _nextHeartbeat;
    private static float _nextRosterCheck;

    // Host: the protocol version each player said Hello with, and who's in the level.
    private static readonly Dictionary<ulong, int> PeerVersions = new();
    private static readonly HashSet<ulong> Roster = new();
    private static readonly HashSet<ulong> FlaggedPeers = new();

    // Client: a press waiting for the host's answer, per toggle, so a broadcast about the other
    // toggle (or a repeat sent before the host saw the press) can't undo it.
    private static readonly float[] PendingAt = { -100f, -100f, -100f };
    private static readonly bool[] PendingOn = new bool[3];
    private static ulong _helloTo;
    private static float _nextHello;
    private static int _flaggedHostVersion;

    internal static void Tick()
    {
        Register();

        // Tick only runs in a level, so a gap means a new level started.
        var now = Time.unscaledTime;
        var levelStart = now - _lastTick > 2f;
        _lastTick = now;

        if (SNet.IsMaster)
        {
            HostTick(now, levelStart);
            return;
        }

        var master = SNet.Master;
        if (master == null) return;
        if (levelStart || master.Lookup != _helloTo) SayHello(master, now);
        foreach (var bit in Bits)
        {
            if (PendingAt[bit] < 0f || now - PendingAt[bit] < RequestGraceSeconds) continue;
            PendingAt[bit] = -100f;
            var why = _flaggedHostVersion != 0 ? "the host's GTFO.* mods are on a different version" : "the host doesn't have team sync";
            InfiniteToggles.Show($"{InfiniteToggles.Name(Key(bit)!)}: {InfiniteToggles.OnOff(PendingOn[bit])} - {why}, only you have it");
        }
    }

    private static void HostTick(float now, bool levelStart)
    {
        var joined = false;
        if (levelStart || now >= _nextRosterCheck)
        {
            _nextRosterCheck = now + RosterCheckSeconds;
            var current = Humans();
            foreach (var lookup in current)
                if (!Roster.Contains(lookup)) joined = true;
            // Someone who left the lobby has to say Hello again if they come back (maybe without the
            // mod now). Not just out of the level: agents come and go around level changes.
            foreach (var lookup in Roster)
                if (!current.Contains(lookup) && !InLobby(lookup))
                {
                    PeerVersions.Remove(lookup);
                    FlaggedPeers.Remove(lookup);
                }
            Roster.Clear();
            Roster.UnionWith(current);
        }

        if (!joined && now < _nextHeartbeat) return;
        _nextHeartbeat = now + HeartbeatSeconds;
        Broadcast(0, 0);
    }

    internal static void Pressed(string key, bool on)
    {
        Register();
        var bit = Bit(key);
        if (SNet.IsMaster)
        {
            Broadcast(bit, SNet.LocalPlayer != null ? SNet.LocalPlayer.Lookup : 0UL);
            InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} for the whole team{Except(MissingPeers())}");
            return;
        }
        var master = SNet.Master;
        if (master == null)
        {
            InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} - no host to share it with, only you have it");
            return;
        }
        PendingAt[bit] = Time.unscaledTime;
        PendingOn[bit] = on;
        NetworkAPI.InvokeEvent(RequestEvent, new Request { Version = SharedOwner.ProtocolVersion, Key = bit, On = (byte)(on ? 1 : 0) }, master);
        InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} - requested from the host...");
    }

    // Marked done only once every event is registered; a failure part way is retried next frame
    // (the ones already in are skipped).
    private static void Register()
    {
        if (_registered) return;
        if (!NetworkAPI.IsEventRegistered(HelloEvent)) RemoteEvents.Register<Hello>(HelloEvent, OnHello);
        if (!NetworkAPI.IsEventRegistered(RequestEvent)) RemoteEvents.Register<Request>(RequestEvent, OnRequest);
        if (!NetworkAPI.IsEventRegistered(StateEvent)) RemoteEvents.Register<State>(StateEvent, OnState);
        _registered = true;
    }

    // ── Host ──────────────────────────────────────────────────────────────

    // A player's game came into a level (or got a new host): note its version, send it the state.
    private static void OnHello(ulong sender, Hello hello)
    {
        if (!SNet.IsMaster) return;
        PeerVersions[sender] = hello.Version;
        if (hello.Version != SharedOwner.ProtocolVersion)
        {
            Flag(sender, hello.Version);
            return;
        }
        FlaggedPeers.Remove(sender);
        if (SNet.TryGetPlayer(sender, out var player) && player != null) Send(player, 0, 0);
    }

    // A client pressed Insert or Delete.
    private static void OnRequest(ulong sender, Request request)
    {
        if (!SNet.IsMaster) return;
        if (request.Version != SharedOwner.ProtocolVersion)
        {
            Flag(sender, request.Version);
            return;
        }
        PeerVersions[sender] = request.Version;
        var key = Key(request.Key);
        if (key == null) return;
        var on = request.On != 0;
        SharedOwner.Set(key, on);
        // Answered even when nothing changed, so the presser hears back.
        Broadcast(request.Key, sender);
        InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} for the whole team{By(sender)}{Except(MissingPeers())}");
    }

    // Once per player: their toggles won't follow the team's.
    private static void Flag(ulong lookup, int version)
    {
        if (!FlaggedPeers.Add(lookup)) return;
        InfiniteToggles.Show($"{PlayerName(lookup)}'s GTFO.* mods are on a different version (team sync v{version}, yours v{SharedOwner.ProtocolVersion}) - infinite health/ammo won't reach them");
    }

    private static void Broadcast(byte changed, ulong by) => NetworkAPI.InvokeEvent(StateEvent, MakeState(changed, by));

    private static void Send(SNet_Player player, byte changed, ulong by) => NetworkAPI.InvokeEvent(StateEvent, MakeState(changed, by), player);

    private static State MakeState(byte changed, ulong by)
    {
        var missing = MissingPeers();
        return new State
        {
            Version = SharedOwner.ProtocolVersion,
            Flags = Flags(),
            Changed = changed,
            By = by,
            Missing1 = missing.Count > 0 ? missing[0] : 0,
            Missing2 = missing.Count > 1 ? missing[1] : 0,
            Missing3 = missing.Count > 2 ? missing[2] : 0,
        };
    }

    // The other humans in the level (the host's own bots are its to protect).
    private static HashSet<ulong> Humans()
    {
        var humans = new HashSet<ulong>();
        foreach (var agent in PlayerManager.PlayerAgentsInLevel)
        {
            var owner = agent?.Owner;
            if (owner == null || owner.IsBot || owner.IsLocal) continue;
            humans.Add(owner.Lookup);
        }
        return humans;
    }

    // Humans in the level that haven't said Hello on this protocol version.
    private static List<ulong> MissingPeers()
    {
        var missing = new List<ulong>();
        foreach (var lookup in Humans())
            if (!PeerVersions.TryGetValue(lookup, out var version) || version != SharedOwner.ProtocolVersion)
                missing.Add(lookup);
        return missing;
    }

    // ── Client ────────────────────────────────────────────────────────────

    // Also asks the host for the state; rate-limited, since a host that lists us as missing gets one
    // again.
    private static void SayHello(SNet_Player master, float now)
    {
        if (master.Lookup == _helloTo && now < _nextHello) return;
        _helloTo = master.Lookup;
        _nextHello = now + RehelloSeconds;
        _flaggedHostVersion = 0;
        NetworkAPI.InvokeEvent(HelloEvent, new Hello { Version = SharedOwner.ProtocolVersion }, master);
    }

    // The team's state from the host.
    private static void OnState(ulong sender, State state)
    {
        if (SNet.IsMaster) return;
        var master = SNet.Master;
        if (master == null || master.Lookup != sender) return;
        if (state.Version != SharedOwner.ProtocolVersion)
        {
            if (_flaggedHostVersion == state.Version) return;
            _flaggedHostVersion = state.Version;
            InfiniteToggles.Show($"The host's GTFO.* mods are on a different version (team sync v{state.Version}, yours v{SharedOwner.ProtocolVersion}) - infinite health/ammo only reach you");
            return;
        }

        var missing = new List<ulong>();
        var local = SNet.LocalPlayer != null ? SNet.LocalPlayer.Lookup : 0UL;
        foreach (var lookup in new[] { state.Missing1, state.Missing2, state.Missing3 })
        {
            if (lookup == 0) continue;
            // The host lost our Hello (or we joined just now): say it again.
            if (lookup == local) SayHello(master, Time.unscaledTime);
            else missing.Add(lookup);
        }

        var now = Time.unscaledTime;
        foreach (var bit in Bits)
        {
            var key = Key(bit)!;
            var on = (state.Flags & bit) != 0;
            var answer = state.Changed == bit;
            if (PendingAt[bit] >= 0f)
            {
                // Our press is in flight: only the host's answer to a press of this toggle settles
                // it; a repeat, or news about the other toggle, leaves this one as pressed.
                if (answer)
                {
                    PendingAt[bit] = -100f;
                    SharedOwner.Set(key, on);
                    InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} for the whole team{By(state.By)}{Except(missing)}");
                    continue;
                }
                if (now - PendingAt[bit] < RequestGraceSeconds) continue;
                // The request was lost: the host's state stands.
                PendingAt[bit] = -100f;
            }
            if (SharedOwner.Get(key) == on) continue;
            SharedOwner.Set(key, on);
            InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {InfiniteToggles.OnOff(on)} for the whole team{(answer ? By(state.By) : "")}{Except(missing)}");
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private static byte Flags() =>
        (byte)((InfiniteToggles.InfiniteHealthOn ? HealthBit : 0) | (InfiniteToggles.InfiniteAmmoOn ? AmmoBit : 0));

    private static byte Bit(string key) => key == InfiniteToggles.HealthKey ? HealthBit : AmmoBit;

    private static string? Key(byte bit) => bit switch
    {
        HealthBit => InfiniteToggles.HealthKey,
        AmmoBit => InfiniteToggles.AmmoKey,
        _ => null,
    };

    // " (Name)" for whoever pressed it; nothing for yourself or someone unknown.
    private static string By(ulong lookup)
    {
        if (lookup == 0 || (SNet.LocalPlayer != null && SNet.LocalPlayer.Lookup == lookup)) return "";
        var name = PlayerName(lookup, "");
        return name.Length > 0 ? $" ({name})" : "";
    }

    // " - except Bob, Ann (mod missing/outdated)" for players the toggle doesn't reach.
    private static string Except(List<ulong> missing)
    {
        if (missing.Count == 0) return "";
        var names = new List<string>();
        foreach (var lookup in missing) names.Add(PlayerName(lookup));
        return $" - except {string.Join(", ", names)} (GTFO.* mod missing/outdated)";
    }

    private static bool InLobby(ulong lookup)
    {
        try
        {
            return SNet.TryGetPlayer(lookup, out var player) && player != null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string PlayerName(ulong lookup, string unknown = "a player")
    {
        try
        {
            return SNet.TryGetPlayer(lookup, out var player) && player != null && !string.IsNullOrWhiteSpace(player.NickName)
                ? player.NickName
                : unknown;
        }
        catch (Exception)
        {
            return unknown;
        }
    }
}

[HarmonyPatch(typeof(Dam_PlayerDamageLocal))]
internal static class InfiniteToggles_Patches
{
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.Update))]
    [HarmonyPostfix]
    private static void AfterUpdate() => InfiniteToggles.Tick();

    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveBulletDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveExplosionDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveMeleeDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceivePushDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveFireDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveTentacleAttackDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveTentacleTrapGrabDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveParasiteTrapDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveTentacleTankGrabDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveShooterProjectileDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveParasiteDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveStickyDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveFallDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveNoAirDamage))]
    [HarmonyPatch(nameof(Dam_PlayerDamageLocal.ReceiveSetDead))]
    [HarmonyPrefix]
    private static bool BeforeDamage(Dam_PlayerDamageLocal __instance) => InfiniteToggles.AllowDamage(__instance);
}

// While infinite ammo is on, spending ammo of a kept-full type (InfiniteToggles.KeepsFull) costs
// nothing: the spend is turned into 0 rather than skipped, so the game's own bookkeeping (and its
// return value) runs as normal. The per-frame refill is the backstop; this is what stops the last
// grenade in a stack from being used up (and the item dropped) between two refills. Every copy of
// this code applies it; the change is the same however many times it runs.
[HarmonyPatch(typeof(PlayerAmmoStorage))]
internal static class InfiniteToggles_AmmoPatches
{
    [HarmonyPatch(nameof(PlayerAmmoStorage.UpdateAmmoInPack))]
    [HarmonyPrefix]
    private static void BeforeUpdateAmmo(AmmoType ammoType, ref float delta)
    {
        if (delta < 0f && InfiniteToggles.KeepsFull(ammoType)) delta = 0f;
    }

    [HarmonyPatch(nameof(PlayerAmmoStorage.UpdateBulletsInPack))]
    [HarmonyPrefix]
    private static void BeforeUpdateBullets(AmmoType ammoType, ref int bulletCount)
    {
        if (bulletCount < 0 && InfiniteToggles.KeepsFull(ammoType)) bulletCount = 0;
    }
}
