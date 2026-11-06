using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace MindAttic.GTFO.Shared;

// Code GTFO.EZ and its Absurd companions each compile their own copy of (from GTFO.EZ.Shared, a git
// submodule in each mod). With several of the mods installed, several copies run, so anything that
// must happen once - a key press, a warden message - only happens in one copy, the owner (the newest
// ProtocolVersion, Order breaking ties). State that has to be the same in every copy lives once for the whole process (Get/Set), not
// in a copy's statics.
internal static class SharedOwner
{
    internal static readonly ManualLogSource Log = Logger.CreateLogSource("MindAttic.GTFO.Shared");

    // Every GTFO.* mod's master switch: the first entry in its .cfg. Off, the mod returns from Load
    // before patching anything, so it counts as not installed.
    internal const string MasterSection = "0. Mod";
    internal const string MasterKey = "Enabled";

    private static readonly string[] Order =
    {
        "MindAttic.GTFO.EZ",
        "MindAttic.GTFO.EZ.AbsurdWeapons",
        "MindAttic.GTFO.EZ.AbsurdMoves",
        "MindAttic.GTFO.EZ.AbsurdItems",
    };

    // Which revision of this shared code a copy carries. Bump it on any change to the shared code's
    // behaviour or to InfiniteSync's messages: the newest loaded copy owns the keys (FindOwner), and
    // every network message carries it, so a teammate on a different revision is ignored and named
    // rather than half-understood (GES-T14). 1 was GES-T12/T13's sync, which didn't carry it.
    internal const int ProtocolVersion = 2;

    private static bool? _isOwner;

    // True in exactly one loaded copy: the installed, switched-on copy with the highest
    // ProtocolVersion, the first in Order breaking a tie. Mixed installs happen (each mod updates on
    // its own), and the owner's code is what runs the keys and the team sync, so the newest wins
    // rather than whichever mod happens to come first. A mod version from before the shared code (an
    // older GTFO.EZ), or one whose master switch is off, is passed over.
    internal static bool IsOwner => _isOwner ??= FindOwner();

    private static bool FindOwner()
    {
        try
        {
            var mine = Assembly.GetExecutingAssembly();
            Assembly? best = null;
            var bestVersion = int.MinValue;
            var first = true;
            foreach (var guid in Order)
            {
                if (!TryGetActive(guid, out var plugin)) continue;
                var assembly = plugin.GetType().Assembly;
                var type = assembly.GetType(typeof(SharedOwner).FullName!);
                if (type == null) continue;
                var version = VersionOf(type);
                // A copy from before ProtocolVersion picks by Order alone, so when it comes first it
                // runs the keys whatever this copy decides - let it, rather than have two owners
                // toggling on every press.
                if (first && version == null) return assembly == mine;
                first = false;
                if ((version ?? 0) > bestVersion)
                {
                    best = assembly;
                    bestVersion = version ?? 0;
                }
            }
            return best == mine;
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't tell which GTFO.* mod runs the shared keys. {e.Message}");
        }
        return false;
    }

    // Another copy's ProtocolVersion, or null for a copy from before it existed.
    private static int? VersionOf(Type sharedOwner)
    {
        var field = sharedOwner.GetField(nameof(ProtocolVersion), BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (field == null) return null;
        var value = field.IsLiteral ? field.GetRawConstantValue() : field.GetValue(null);
        return value is int version ? version : null;
    }

    // Whether the mod with this plugin GUID is installed, loaded and switched on ([0. Mod] Enabled
    // not false; a version without the switch counts as on). For checks on another GTFO.* mod.
    internal static bool IsActive(string guid)
    {
        try
        {
            return TryGetActive(guid, out _);
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't check whether {guid} is installed. {e.Message}");
            return false;
        }
    }

    // Another mod's setting, read off its own config: false when that mod isn't active (see
    // IsActive) or hasn't bound the entry, so the caller picks what a missing setting means.
    internal static bool TryGetSetting<T>(string guid, string section, string key, [NotNullWhen(true)] out ConfigEntry<T>? entry)
    {
        entry = null;
        try
        {
            return TryGetActive(guid, out var plugin) && plugin.Config.TryGetEntry(section, key, out entry);
        }
        catch (Exception e)
        {
            Log.LogWarning($"Couldn't read [{section}] {key} from {guid}. {e.Message}");
            entry = null;
            return false;
        }
    }

    private static bool TryGetActive(string guid, [NotNullWhen(true)] out BasePlugin? plugin)
    {
        plugin = null;
        if (!IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var info) || info.Instance is not BasePlugin loaded) return false;
        if (loaded.Config.TryGetEntry<bool>(MasterSection, MasterKey, out var enabled) && !enabled.Value) return false;
        plugin = loaded;
        return true;
    }

    internal static bool Get(string key) => AppDomain.CurrentDomain.GetData("MindAttic.GTFO." + key) is true;

    internal static void Set(string key, bool value) => AppDomain.CurrentDomain.SetData("MindAttic.GTFO." + key, value);
}
