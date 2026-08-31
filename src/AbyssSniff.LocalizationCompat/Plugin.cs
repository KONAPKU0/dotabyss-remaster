using BepInEx;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;

namespace AbyssSniff.LocalizationCompat;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
[BepInDependency("AbyssSniff", BepInDependency.DependencyFlags.HardDependency)]
public sealed class Plugin : BasePlugin
{
    public const string PluginGuid = "AbyssSniff.LocalizationCompat";
    public const string PluginName = "AbyssSniff Localization Compatibility";
    public const string PluginVersion = "1.0.0";

    private Harmony? _harmony;

    public override void Load()
    {
        var cacheRoot = Path.Combine(Paths.PluginPath, "AbyssMod", "cache");
        var aliases = new TranslationCache(
            cacheRoot,
            TimeSpan.FromSeconds(2),
            message => Log.LogInfo("[LocalizationCompat] " + message),
            message => Log.LogWarning("[LocalizationCompat] " + message));
        aliases.ReloadNow();

        _harmony = new Harmony(PluginGuid);
        CompatibilityPatches.Apply(
            _harmony,
            aliases,
            message => Log.LogInfo("[LocalizationCompat] " + message),
            message => Log.LogWarning("[LocalizationCompat] " + message));

        Log.LogInfo("[LocalizationCompat] ready; watching " + cacheRoot);
    }

    public override bool Unload()
    {
        _harmony?.UnpatchSelf();
        return true;
    }
}
