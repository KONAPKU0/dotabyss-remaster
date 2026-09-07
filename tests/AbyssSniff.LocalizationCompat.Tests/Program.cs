using System.Text;
using Mono.Cecil;

namespace AbyssSniff.LocalizationCompat;

internal static class Program
{
    private static int _passed;

    public static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var repoRoot = args.Length > 0 ? Path.GetFullPath(args[0]) : Directory.GetCurrentDirectory();
        var tests = new (string Name, Action Body)[]
        {
            ("code names are equivalent in both directions", CodeNamesAreEquivalent),
            ("suffixed mana gems match either-language prefixes", ManaGemPrefixesAreEquivalent),
            ("translated controls and abyss categories canonicalize", UiAndCategoryLabelsCanonicalize),
            ("cache appearing and changing after startup reloads", CacheAutomaticallyReloads),
            ("missing, malformed, and duplicate translations fail open", InvalidCachesFailOpen),
            ("AbyssSniff patch targets still exist", () => ValidateAbyssSniffTargets(repoRoot)),
            ("full upstream v1.6.1 DLL is preserved byte-for-byte", () => ValidateUpstreamAbyssSniff(repoRoot)),
            ("release plugin metadata and installation are valid", () => ValidateReleasePlugin(repoRoot))
        };

        foreach (var test in tests)
        {
            try
            {
                test.Body();
                _passed++;
                Console.WriteLine("PASS " + test.Name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("FAIL " + test.Name + Environment.NewLine + ex);
                return 1;
            }
        }

        Console.WriteLine($"All {_passed} checks passed.");
        return 0;
    }

    private static void CodeNamesAreEquivalent()
    {
        WithCache((root, cache) =>
        {
            WriteValidCache(root);
            AssertEqual("アビスビジョン", cache.Canonicalize("深渊视界"));
            AssertEqual("アビスビジョン", cache.Canonicalize("アビスビジョン"));
            AssertTrue(cache.ContainsEquivalent("深渊视界", "アビスビジョン"));
            AssertTrue(cache.ContainsEquivalent("アビスビジョン", "深渊视界"));
        });
    }

    private static void ManaGemPrefixesAreEquivalent()
    {
        WithCache((root, cache) =>
        {
            WriteValidCache(root);
            AssertTrue(cache.ContainsEquivalent(
                "热情能量水晶【刚力】",
                "情熱のマナクリスタル"));
            AssertTrue(cache.ContainsEquivalent(
                "情熱のマナクリスタル【剛力】",
                "热情能量水晶"));
            AssertTrue(cache.ContainsEquivalent(
                "冲击能量水晶【虚空】",
                "衝撃のマナクリスタル"));
        });
    }

    private static void UiAndCategoryLabelsCanonicalize()
    {
        WithCache((root, cache) =>
        {
            WriteValidCache(root);
            AssertEqual("決定", cache.Canonicalize("确定"));
            AssertEqual("キャンセル", cache.Canonicalize("取消"));
            AssertEqual("確認", cache.Canonicalize("确认"));
            AssertEqual("クイック選択", cache.Canonicalize("快速选择"));
            AssertEqual("ラッシュ", cache.Canonicalize("冲锋"));
            AssertEqual("インパクト", cache.Canonicalize("冲击"));
            AssertEqual("セーフ", cache.Canonicalize("安全"));
            AssertEqual("リスク", cache.Canonicalize("风险"));

            var expanded = cache.ExpandAliases(new[] { "決定", "キャンセル" });
            AssertContains(expanded, "确定");
            AssertContains(expanded, "取消");
        });
    }

    private static void CacheAutomaticallyReloads()
    {
        WithCache((root, cache) =>
        {
            AssertEqual("深渊视界", cache.Canonicalize("深渊视界"));
            WriteValidCache(root);
            AssertEqual("アビスビジョン", cache.Canonicalize("深渊视界"));

            var locale = Path.Combine(root, "zh_Hans");
            File.WriteAllText(
                Path.Combine(locale, "static.json"),
                "{\"table\":{\"name\":{\"アビスビジョン\":\"深渊视觉新版\"}}}",
                Encoding.UTF8);
            AssertEqual("アビスビジョン", cache.Canonicalize("深渊视觉新版"));
            AssertEqual("深渊视界", cache.Canonicalize("深渊视界"));
        });
    }

    private static void InvalidCachesFailOpen()
    {
        WithCache((root, cache) =>
        {
            AssertEqual("没有缓存", cache.Canonicalize("没有缓存"));
            var locale = Path.Combine(root, "zh_Hans");
            Directory.CreateDirectory(locale);
            File.WriteAllText(Path.Combine(locale, "static.json"), "{not valid json", Encoding.UTF8);
            AssertEqual("损坏缓存", cache.Canonicalize("损坏缓存"));
        });

        WithCache((root, cache) =>
        {
            var locale = Path.Combine(root, "zh_Hans");
            Directory.CreateDirectory(locale);
            File.WriteAllText(
                Path.Combine(locale, "static.json"),
                "{\"table\":{\"name\":{\"原文甲\":\"重复译名\",\"原文乙\":\"重复译名\"}}}",
                Encoding.UTF8);
            AssertEqual("重复译名", cache.Canonicalize("重复译名"));
            AssertEqual("原文甲", cache.Canonicalize("原文甲"));
            AssertTrue(cache.ContainsEquivalent("重复译名", "原文甲"));
            AssertTrue(cache.ContainsEquivalent("重复译名", "原文乙"));
        });
    }

    private static void ValidateAbyssSniffTargets(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "BepInEx", "plugins", "AbyssSniff", "AbyssSniff.dll");
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        RequireMethod(assembly, "AbyssSniff.Reroll.AutoPlayer", "GetLabel", "System.String", "UnityEngine.GameObject");
        RequireMethod(assembly, "AbyssSniff.Reroll.AutoPlayer", "ScreenHasText", "System.Boolean", "System.String[]");
        RequireMethod(assembly, "AbyssSniff.Reroll.NetherBuffPicker", "ReadTmpField", "System.String", "System.Object", "System.String");
        RequireMethod(assembly, "AbyssSniff.Reroll.NetherBuffPicker", "CategoryOfName", "System.Int32", "System.String");
        RequireMethod(assembly, "AbyssSniff.Reroll.NetherBuffPicker", "InfoOfName", "AbyssSniff.Reroll.NetherBuffPicker/CodeInfo", "System.String");
        var confirm = RequireMethod(assembly, "AbyssSniff.Reroll.NetherBuffPicker", "ClickConfirmButton", "System.Boolean");
        AssertTrue(confirm.HasBody && confirm.Body.Instructions.Any(instruction =>
            instruction.Operand is MethodReference method &&
            method.DeclaringType.FullName == "System.String" &&
            method.Name == "Trim"), "ClickConfirmButton string.Trim() patch point is missing");
        RequireMethod(assembly, "AbyssSniff.Reroll.ForceChainAuto", "NameAllowed", "System.Boolean", "System.String");
        var forceChain = RequireType(assembly, "AbyssSniff.Reroll.ForceChainAuto");
        AssertTrue(forceChain.Fields.Any(field => field.Name == "AllowNames" && field.IsStatic), "ForceChainAuto.AllowNames is missing or not static");
        // Harmony binds these prefix arguments by their names, not just their types.
        foreach (var (typeName, methodName, parameterName) in new[]
                 {
                     ("AbyssSniff.Reroll.AutoPlayer", "ScreenHasText", "markers"),
                     ("AbyssSniff.Reroll.NetherBuffPicker", "CategoryOfName", "name"),
                     ("AbyssSniff.Reroll.NetherBuffPicker", "InfoOfName", "name"),
                     ("AbyssSniff.Reroll.ForceChainAuto", "NameAllowed", "name")
                 })
        {
            var method = RequireType(assembly, typeName).Methods.Single(candidate => candidate.Name == methodName);
            AssertTrue(method.IsStatic && method.Parameters[0].Name == parameterName,
                "Harmony prefix parameter contract changed: " + method.FullName);
        }
    }

    private static void ValidateReleasePlugin(string repoRoot)
    {
        var installDirectory = Path.Combine(repoRoot, "BepInEx", "plugins", "AbyssSniff");
        var originalPath = Path.Combine(installDirectory, "AbyssSniff.dll");
        var pluginPath = Path.Combine(installDirectory, "AbyssSniff.LocalizationCompat.dll");
        AssertTrue(File.Exists(originalPath), "original AbyssSniff.dll was removed");
        AssertTrue(File.Exists(pluginPath), "release compatibility DLL is missing from the install directory");
        var installedDlls = Directory.GetFiles(installDirectory, "*.dll", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        AssertTrue(installedDlls.SequenceEqual(new[] { "AbyssSniff.LocalizationCompat.dll", "AbyssSniff.dll" }),
            "install directory must contain only the upstream plugin and localization add-on, without stale interop DLLs");
        AssertTrue(File.Exists(Path.Combine(repoRoot, "BepInEx", "config", "BepInEx.cfg")),
            "upstream v1.6.1 BepInEx logging configuration is missing");

        using var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(repoRoot, "BepInEx", "core"));
        resolver.AddSearchDirectory(installDirectory);
        using var assembly = AssemblyDefinition.ReadAssembly(pluginPath, new ReaderParameters
        {
            AssemblyResolver = resolver
        });
        var targetFramework = assembly.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "System.Runtime.Versioning.TargetFrameworkAttribute");
        AssertTrue(targetFramework is not null, "TargetFrameworkAttribute is missing");
        AssertEqual(".NETCoreApp,Version=v6.0", targetFramework!.ConstructorArguments[0].Value as string);

        var pluginType = RequireType(assembly, "AbyssSniff.LocalizationCompat.Plugin");
        AssertEqual("BepInEx.Unity.IL2CPP.BasePlugin", pluginType.BaseType?.FullName);
        var metadata = pluginType.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        AssertTrue(metadata is not null, "BepInPlugin metadata is missing");
        AssertEqual("AbyssSniff.LocalizationCompat", metadata!.ConstructorArguments[0].Value as string);

        var dependency = pluginType.CustomAttributes.FirstOrDefault(attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInDependency");
        AssertTrue(dependency is not null, "BepInDependency metadata is missing");
        AssertEqual("AbyssSniff", dependency!.ConstructorArguments[0].Value as string);
    }

    private static void ValidateUpstreamAbyssSniff(string repoRoot)
    {
        var path = Path.Combine(repoRoot, "BepInEx", "plugins", "AbyssSniff", "AbyssSniff.dll");
        // Assert byte-for-byte upstream identity, not merely a version string. This
        // prevents accidentally shipping the old automation-only or a repatched DLL.
        AssertEqual(
            "64AD4F817374111ADBB0D75E3A60E75E3000D59DFC02DCD69ACBDCDE53FE27F6",
            Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path))));
        using var assembly = AssemblyDefinition.ReadAssembly(path);
        var plugin = RequireType(assembly, "AbyssSniff.Plugin");
        var metadata = plugin.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        AssertEqual("AbyssSniff", metadata.ConstructorArguments[0].Value as string);
        AssertEqual("AbyssSniff", metadata.ConstructorArguments[1].Value as string);
        AssertEqual("1.6.1", metadata.ConstructorArguments[2].Value as string);
        var load = RequireMethod(assembly, "AbyssSniff.Plugin", "Load", "System.Void");
        foreach (var required in new[]
                 {
                     "AbyssSniff.Patches.InteropBindingAudit.Initialize",
                     "AbyssSniff.Patches.ApiSniffPatch.Initialize",
                     "AbyssSniff.Reroll.NetherCheckpointPatch.Initialize",
                     "AbyssSniff.Reroll.DropAnalyzer.Initialize"
                 })
            AssertContains(Calls(load), required);
    }

    private static IEnumerable<string> Calls(MethodDefinition method)
    {
        return method.Body.Instructions
            .Where(instruction => instruction.OpCode.Code is Mono.Cecil.Cil.Code.Call or Mono.Cecil.Cil.Code.Callvirt)
            .Select(instruction => instruction.Operand)
            .OfType<MethodReference>()
            .Select(reference => reference.DeclaringType.FullName + "." + reference.Name);
    }

    private static MethodDefinition RequireMethod(
        AssemblyDefinition assembly,
        string typeName,
        string methodName,
        string? returnType,
        params string[] parameterTypes)
    {
        var type = RequireType(assembly, typeName);
        var matches = type.Methods.Where(method =>
            method.Name == methodName &&
            method.Parameters.Select(parameter => parameter.ParameterType.FullName).SequenceEqual(parameterTypes) &&
            (returnType is null || method.ReturnType.FullName == returnType)).ToArray();
        AssertTrue(matches.Length == 1, $"expected one {typeName}.{methodName}, found {matches.Length}");
        return matches[0];
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName)
    {
        var type = assembly.MainModule.Types.FirstOrDefault(candidate => candidate.FullName == fullName);
        AssertTrue(type is not null, "type is missing: " + fullName);
        return type!;
    }

    private static void WriteValidCache(string root)
    {
        var locale = Path.Combine(root, "zh_Hans");
        Directory.CreateDirectory(locale);
        File.WriteAllText(
            Path.Combine(locale, "static.json"),
            "{\"m_nether_codes\":{\"name\":{\"アビスビジョン\":\"深渊视界\",\"情熱のマナクリスタル【剛力】\":\"热情能量水晶【刚力】\",\"衝撃のマナクリスタル【虚空】\":\"冲击能量水晶【虚空】\"}}}",
            Encoding.UTF8);
        File.WriteAllText(
            Path.Combine(locale, "ui_texts.json"),
            "{\"controls\":{\"決定\":\"确定\",\"キャンセル\":\"取消\",\"確認\":\"确认\",\"クイック選択\":\"快速选择\"}}",
            Encoding.UTF8);
    }

    private static void WithCache(Action<string, TranslationCache> test)
    {
        var root = Path.Combine(Path.GetTempPath(), "AbyssSniff.LocalizationCompat.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            test(root, new TranslationCache(root, TimeSpan.Zero));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private static void AssertTrue(bool condition, string? message = null)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message ?? "expected true");
        }
    }

    private static void AssertEqual(string? expected, string? actual)
    {
        if (!string.Equals(expected, actual, StringComparison.Ordinal))
        {
            throw new InvalidOperationException($"expected '{expected}', got '{actual}'");
        }
    }

    private static void AssertContains(IEnumerable<string> values, string expected)
    {
        AssertTrue(values.Contains(expected, StringComparer.Ordinal), "missing alias: " + expected);
    }
}
