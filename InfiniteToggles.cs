using System;
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

    private static int _lastFrame = -1;
    private static bool _ammoWasOn;
    private static float _nextSentryRefill;
    private static bool _syncBroken;

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

    private static void Press(string key)
    {
        var on = !SharedOwner.Get(key);
        SharedOwner.Set(key, on);
        Show($"{Name(key)}: {(on ? "ON" : "OFF")} for the whole team");
        Sync(() => InfiniteSync.Pressed(key, on));
    }

    // GTFO-API comes with BepInExPack_GTFO, so it's always there; if it somehow isn't, the toggles
    // still work for this machine (the presser and, on the host, its bots) instead of not at all.
    private static void Sync(Action action)
    {
        if (_syncBroken) return;
        try
        {
            action();
        }
        catch (Exception e)
        {
            _syncBroken = true;
            SharedOwner.Log.LogWarning($"Infinite health/ammo can't reach teammates (GTFO-API unavailable?) - only this game gets it. {e.Message}");
        }
    }

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
// the client straight away and sent to the host as a request; the host applies it and broadcasts
// the state to everyone, naming who pressed it. The host also repeats the state every few seconds
// while anything is on, and for the first half minute of each level, so a late joiner (or a client
// still carrying an old state from another lobby) ends up matching. Only the owning copy calls in
// here, and all of it is kept out of InfiniteToggles' own methods so a missing GTFO-API can't stop
// those from compiling.
internal static class InfiniteSync
{
    private const string RequestEvent = "MindAttic.GTFO.InfiniteToggles.Request";
    private const string StateEvent = "MindAttic.GTFO.InfiniteToggles.State";

    private const byte HealthBit = 1;
    private const byte AmmoBit = 2;

    private const float HeartbeatSeconds = 5f;
    private const float LevelStartSyncSeconds = 30f;
    private const float RequestGraceSeconds = 2f;

    // Plain fields so GTFO-API can copy them as raw bytes.
    private struct Request
    {
        public byte Key; // HealthBit or AmmoBit
        public byte On;
    }

    private struct State
    {
        public byte Flags;   // HealthBit | AmmoBit, whichever are on
        public byte Changed; // the bit that just changed, 0 for a repeat
        public ulong By;     // who pressed it (player lookup), 0 for a repeat
    }

    private static bool _registered;
    private static float _lastTick = -100f;
    private static float _syncUntil;
    private static float _nextHeartbeat;
    private static float _requestedAt = -100f;

    internal static void Tick()
    {
        Register();

        // Tick only runs in a level, so a gap means a new level started.
        var now = Time.unscaledTime;
        if (now - _lastTick > 2f) _syncUntil = now + LevelStartSyncSeconds;
        _lastTick = now;

        if (!SNet.IsMaster || now < _nextHeartbeat) return;
        _nextHeartbeat = now + HeartbeatSeconds;
        var flags = Flags();
        if (flags != 0 || now < _syncUntil) Broadcast(flags, 0, 0);
    }

    internal static void Pressed(string key, bool on)
    {
        Register();
        var bit = Bit(key);
        if (SNet.IsMaster)
        {
            Broadcast(Flags(), bit, SNet.LocalPlayer != null ? SNet.LocalPlayer.Lookup : 0UL);
            return;
        }
        var master = SNet.Master;
        if (master == null) return;
        _requestedAt = Time.unscaledTime;
        NetworkAPI.InvokeEvent(RequestEvent, new Request { Key = bit, On = (byte)(on ? 1 : 0) }, master);
    }

    private static void Register()
    {
        if (_registered) return;
        _registered = true;
        if (!NetworkAPI.IsEventRegistered(RequestEvent)) RemoteEvents.Register<Request>(RequestEvent, OnRequest);
        if (!NetworkAPI.IsEventRegistered(StateEvent)) RemoteEvents.Register<State>(StateEvent, OnState);
    }

    // On the host: a client pressed Insert or Delete.
    private static void OnRequest(ulong sender, Request request)
    {
        if (!SNet.IsMaster) return;
        var key = Key(request.Key);
        if (key == null) return;
        var on = request.On != 0;
        if (SharedOwner.Get(key) != on)
        {
            SharedOwner.Set(key, on);
            InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {(on ? "ON" : "OFF")} for the whole team{By(sender)}");
        }
        Broadcast(Flags(), request.Key, sender);
    }

    // On a client: the team's state from the host.
    private static void OnState(ulong sender, State state)
    {
        if (SNet.IsMaster) return;
        var master = SNet.Master;
        if (master != null && master.Lookup != sender) return;
        // A repeat sent before the host saw our own press mustn't undo it for a moment.
        if (state.Changed == 0 && Time.unscaledTime - _requestedAt < RequestGraceSeconds) return;

        foreach (var bit in new[] { HealthBit, AmmoBit })
        {
            var key = Key(bit)!;
            var on = (state.Flags & bit) != 0;
            if (SharedOwner.Get(key) == on) continue;
            SharedOwner.Set(key, on);
            var by = state.Changed == bit ? By(state.By) : "";
            InfiniteToggles.Show($"{InfiniteToggles.Name(key)}: {(on ? "ON" : "OFF")} for the whole team{by}");
        }
    }

    private static void Broadcast(byte flags, byte changed, ulong by) =>
        NetworkAPI.InvokeEvent(StateEvent, new State { Flags = flags, Changed = changed, By = by });

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
        try
        {
            if (lookup == 0 || (SNet.LocalPlayer != null && SNet.LocalPlayer.Lookup == lookup)) return "";
            return SNet.TryGetPlayer(lookup, out var player) && player != null && !string.IsNullOrWhiteSpace(player.NickName)
                ? $" ({player.NickName})"
                : "";
        }
        catch (Exception)
        {
            return "";
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
