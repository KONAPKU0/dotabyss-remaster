using System.Collections;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace AbyssSniff.LocalizationCompat;

internal static class CompatibilityPatches
{
    private static TranslationCache? _aliases;
    private static Action<string>? _info;
    private static Action<string>? _warn;
    private static FieldInfo? _allowNamesField;

    public static void Apply(
        Harmony harmony,
        TranslationCache aliases,
        Action<string> info,
        Action<string> warn)
    {
        _aliases = aliases;
        _info = info;
        _warn = warn;

        var abyssSniff = AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(assembly => assembly.GetName().Name == "AbyssSniff");
        if (abyssSniff is null)
        {
            warn("AbyssSniff assembly was not found; compatibility patches were skipped");
            return;
        }

        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.AutoPlayer", "GetLabel",
            new[] { "UnityEngine.GameObject" }, postfix: nameof(CanonicalizeResultPostfix));
        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.AutoPlayer", "ScreenHasText",
            new[] { "System.String[]" }, prefix: nameof(ExpandMarkersPrefix));

        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.NetherBuffPicker", "ReadTmpField",
            new[] { "System.Object", "System.String" }, postfix: nameof(CanonicalizeResultPostfix));
        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.NetherBuffPicker", "CategoryOfName",
            new[] { "System.String" }, prefix: nameof(CanonicalizeNamePrefix));
        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.NetherBuffPicker", "InfoOfName",
            new[] { "System.String" }, prefix: nameof(CanonicalizeNamePrefix));
        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.NetherBuffPicker", "ClickConfirmButton",
            Array.Empty<string>(), transpiler: nameof(ConfirmButtonTranspiler));

        var forceChainType = abyssSniff.GetType("AbyssSniff.Reroll.ForceChainAuto", false);
        _allowNamesField = forceChainType?.GetField(
            "AllowNames",
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
        if (_allowNamesField is null)
        {
            warn("target field missing: AbyssSniff.Reroll.ForceChainAuto.AllowNames");
        }

        Patch(harmony, abyssSniff, "AbyssSniff.Reroll.ForceChainAuto", "NameAllowed",
            new[] { "System.String" }, prefix: nameof(NameAllowedPrefix));
    }

    public static string? CanonicalizeForMatch(string? value)
    {
        return _aliases?.Canonicalize(value) ?? value;
    }

    private static void CanonicalizeResultPostfix(ref string? __result)
    {
        __result = CanonicalizeForMatch(__result);
    }

    private static void CanonicalizeNamePrefix(ref string? name)
    {
        name = CanonicalizeForMatch(name);
    }

    private static void ExpandMarkersPrefix(ref string[]? markers)
    {
        if (_aliases is not null)
        {
            markers = _aliases.ExpandAliases(markers);
        }
    }

    private static bool NameAllowedPrefix(string? name, ref bool __result)
    {
        if (_aliases is null || _allowNamesField is null)
        {
            return true;
        }

        try
        {
            var allowNames = _allowNamesField.GetValue(null) as IEnumerable;
            if (allowNames is null)
            {
                return true;
            }

            var hasEntries = false;
            foreach (var item in allowNames)
            {
                if (item is not string allowed)
                {
                    continue;
                }

                hasEntries = true;
                if (_aliases.ContainsEquivalent(name, allowed))
                {
                    __result = true;
                    return false;
                }
            }

            __result = !hasEntries;
            return false;
        }
        catch (Exception ex)
        {
            _warn?.Invoke("force-chain alias check failed; original matcher will run: " + ex.Message);
            return true;
        }
    }

    private static IEnumerable<CodeInstruction> ConfirmButtonTranspiler(
        IEnumerable<CodeInstruction> instructions)
    {
        var source = instructions.ToList();
        var result = new List<CodeInstruction>(source.Count + 1);
        var trim = AccessTools.Method(typeof(string), nameof(string.Trim), Type.EmptyTypes);
        var canonicalize = AccessTools.Method(
            typeof(CompatibilityPatches),
            nameof(CanonicalizeForMatch));
        var insertions = 0;

        foreach (var instruction in source)
        {
            result.Add(instruction);
            if (trim is not null && instruction.Calls(trim))
            {
                result.Add(new CodeInstruction(OpCodes.Call, canonicalize));
                insertions++;
            }
        }

        if (insertions == 0)
        {
            _warn?.Invoke("ClickConfirmButton no longer contains string.Trim(); confirm-label patch was skipped");
            return source;
        }

        _info?.Invoke($"patched ClickConfirmButton at {insertions} text read point(s)");
        return result;
    }

    private static void Patch(
        Harmony harmony,
        Assembly assembly,
        string typeName,
        string methodName,
        IReadOnlyList<string> parameterTypeNames,
        string? prefix = null,
        string? postfix = null,
        string? transpiler = null)
    {
        try
        {
            var type = assembly.GetType(typeName, false);
            if (type is null)
            {
                _warn?.Invoke("target type missing: " + typeName);
                return;
            }

            var candidates = type
                .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance)
                .Where(method => method.Name == methodName)
                .Where(method => ParametersMatch(method, parameterTypeNames))
                .ToArray();
            if (candidates.Length != 1)
            {
                _warn?.Invoke($"target method mismatch: {typeName}.{methodName} (found {candidates.Length})");
                return;
            }

            harmony.Patch(
                candidates[0],
                prefix is null ? null : new HarmonyMethod(AccessTools.Method(typeof(CompatibilityPatches), prefix)),
                postfix is null ? null : new HarmonyMethod(AccessTools.Method(typeof(CompatibilityPatches), postfix)),
                transpiler is null ? null : new HarmonyMethod(AccessTools.Method(typeof(CompatibilityPatches), transpiler)));
            _info?.Invoke("patched " + typeName + "." + methodName);
        }
        catch (Exception ex)
        {
            _warn?.Invoke($"patch skipped for {typeName}.{methodName}: {ex.Message}");
        }
    }

    private static bool ParametersMatch(MethodInfo method, IReadOnlyList<string> expected)
    {
        var actual = method.GetParameters();
        if (actual.Length != expected.Count)
        {
            return false;
        }

        for (var i = 0; i < actual.Length; i++)
        {
            if (actual[i].ParameterType.FullName != expected[i])
            {
                return false;
            }
        }

        return true;
    }
}
