using Mono.Cecil;
using Mono.Cecil.Cil;

namespace AbyssSniff.AutomationOnly.Patcher;

internal static class Program
{
    private const string PluginTypeName = "AbyssSniff.Plugin";
    private const string DropAnalyzerTypeName = "AbyssSniff.Reroll.DropAnalyzer";
    private const string CharacterFixesTypeName = "AbyssSniff.Reroll.CharacterFixes";

    private static readonly HashSet<string> DisabledStartupInitializers = new(StringComparer.Ordinal)
    {
        "AbyssSniff.Patches.DefenceProbePatch",
        "AbyssSniff.Patches.DpsProbePatch",
        "AbyssSniff.Patches.AccessoryProbePatch",
        "AbyssSniff.Patches.AbnormalConditionFixPatch",
        "AbyssSniff.Patches.BuffBugFixPatch",
        "AbyssSniff.Patches.AutoDefensiveTowerPatch",
        "AbyssSniff.Patches.LaveriaTeamKillFixPatch",
        "AbyssSniff.Patches.AttackContinuousProbePatch",
        "AbyssSniff.Patches.SylviaSummonProbePatch",
        "AbyssSniff.Patches.ExplorationMissionBadgeFixPatch",
        "AbyssSniff.Patches.ManaGemUnequipPatch",
        "AbyssSniff.Reroll.UpdateNotifier"
    };

    private static readonly HashSet<string> RequiredStartupInitializers = new(StringComparer.Ordinal)
    {
        "AbyssSniff.Patches.ApiSniffPatch",
        "AbyssSniff.Reroll.NetherCheckpointPatch",
        "AbyssSniff.Reroll.DropAnalyzer"
    };

    public static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("usage: AbyssSniff.AutomationOnly.Patcher <input.dll> <output.dll>");
            return 2;
        }

        var inputPath = Path.GetFullPath(args[0]);
        var outputPath = Path.GetFullPath(args[1]);
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine("input DLL does not exist: " + inputPath);
            return 2;
        }

        try
        {
            using var assembly = AssemblyDefinition.ReadAssembly(inputPath, new ReaderParameters
            {
                InMemory = true,
                ReadSymbols = false
            });

            var plugin = RequireType(assembly, PluginTypeName);
            var load = RequireMethod(plugin, "Load");
            var disabled = DisableStartupInitializers(load);
            ValidateRequiredStartupInitializers(load);

            var dropAnalyzer = RequireType(assembly, DropAnalyzerTypeName);
            var initialize = RequireMethod(dropAnalyzer, "Initialize");
            DisableCharacterFixInitialization(initialize);
            DisableDormantCombatUiAndInput(assembly);

            UpdatePluginMetadata(plugin);
            UpdateLoadBanner(load);

            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            assembly.Write(outputPath, new WriterParameters { WriteSymbols = false });
            Console.WriteLine("Created automation-only AbyssSniff.dll");
            Console.WriteLine("Disabled startup modules: " + string.Join(", ", disabled));
            Console.WriteLine("Kept startup modules: " + string.Join(", ", RequiredStartupInitializers));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static IReadOnlyList<string> DisableStartupInitializers(MethodDefinition load)
    {
        var disabled = new List<string>();
        foreach (var instruction in load.Body.Instructions)
        {
            if (!IsCallTo(instruction, "Initialize", out var method) ||
                !DisabledStartupInitializers.Contains(method.DeclaringType.FullName))
            {
                continue;
            }

            disabled.Add(method.DeclaringType.FullName);
            instruction.OpCode = OpCodes.Nop;
            instruction.Operand = null;
        }

        var missing = DisabledStartupInitializers.Except(disabled, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "expected combat startup calls were not found: " + string.Join(", ", missing));
        }

        return disabled;
    }

    private static void ValidateRequiredStartupInitializers(MethodDefinition load)
    {
        var present = load.Body.Instructions
            .Where(instruction => IsCallTo(instruction, "Initialize", out _))
            .Select(instruction => ((MethodReference)instruction.Operand).DeclaringType.FullName)
            .ToHashSet(StringComparer.Ordinal);
        var missing = RequiredStartupInitializers.Except(present, StringComparer.Ordinal).ToArray();
        if (missing.Length != 0)
        {
            throw new InvalidOperationException(
                "required automation startup calls are missing: " + string.Join(", ", missing));
        }
    }

    private static void DisableCharacterFixInitialization(MethodDefinition initialize)
    {
        var matches = initialize.Body.Instructions
            .Where(instruction => IsCallTo(instruction, "Initialize", out var method) &&
                                  method.DeclaringType.FullName == CharacterFixesTypeName)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"expected one CharacterFixes.Initialize call, found {matches.Length}");
        }

        // CharacterFixes.Initialize consumes the already-loaded config argument. Replacing
        // the call with pop keeps the evaluation stack valid while making the feature inert.
        matches[0].OpCode = OpCodes.Pop;
        matches[0].Operand = null;
    }

    private static void DisableDormantCombatUiAndInput(AssemblyDefinition assembly)
    {
        var dispatcher = RequireType(assembly, "AbyssSniff.Reroll.MainThreadDispatcher");
        var update = RequireMethod(dispatcher, "Update");
        ReplaceSingleCall(
            update,
            CharacterFixesTypeName,
            "Toggle",
            OpCodes.Ldc_I4_0);
        ReplaceSingleCall(
            update,
            "AbyssSniff.Patches.ManaGemUnequipPatch",
            "Tick",
            OpCodes.Nop);

        var onGui = RequireMethod(dispatcher, "OnGUI");
        foreach (var typeName in new[]
                 {
                     "AbyssSniff.Patches.BossResistanceOverlay",
                     "AbyssSniff.Patches.DpsOverlay",
                     "AbyssSniff.Reroll.UpdateNotifier"
                 })
        {
            ReplaceSingleCall(onGui, typeName, "OnGUI", OpCodes.Nop);
        }
    }

    private static void ReplaceSingleCall(
        MethodDefinition method,
        string declaringType,
        string calledMethod,
        OpCode replacement)
    {
        var matches = method.Body.Instructions
            .Where(instruction => IsCallTo(instruction, calledMethod, out var reference) &&
                                  reference.DeclaringType.FullName == declaringType)
            .ToArray();
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                $"expected one {declaringType}.{calledMethod} call in {method.FullName}, found {matches.Length}");
        }

        matches[0].OpCode = replacement;
        matches[0].Operand = null;
    }

    private static void UpdatePluginMetadata(TypeDefinition plugin)
    {
        var metadata = plugin.CustomAttributes.Single(attribute =>
            attribute.AttributeType.FullName == "BepInEx.BepInPlugin");
        metadata.ConstructorArguments[1] = new CustomAttributeArgument(
            metadata.ConstructorArguments[1].Type,
            "AbyssSniff Automation Only");
        metadata.ConstructorArguments[2] = new CustomAttributeArgument(
            metadata.ConstructorArguments[2].Type,
            "1.6.0-automation.1");
    }

    private static void UpdateLoadBanner(MethodDefinition load)
    {
        foreach (var instruction in load.Body.Instructions)
        {
            if (instruction.OpCode == OpCodes.Ldstr &&
                instruction.Operand is string value &&
                value.Contains("AbyssSniff v1.6.0 loaded", StringComparison.Ordinal))
            {
                instruction.Operand = value.Replace(
                    "AbyssSniff v1.6.0 loaded",
                    "AbyssSniff v1.6.0 automation-only loaded",
                    StringComparison.Ordinal);
                return;
            }
        }

        throw new InvalidOperationException("AbyssSniff load banner was not found");
    }

    private static bool IsCallTo(
        Instruction instruction,
        string methodName,
        out MethodReference method)
    {
        if ((instruction.OpCode == OpCodes.Call || instruction.OpCode == OpCodes.Callvirt) &&
            instruction.Operand is MethodReference candidate &&
            candidate.Name == methodName)
        {
            method = candidate;
            return true;
        }

        method = null!;
        return false;
    }

    private static TypeDefinition RequireType(AssemblyDefinition assembly, string fullName)
    {
        return assembly.MainModule.Types.SingleOrDefault(type => type.FullName == fullName)
            ?? throw new InvalidOperationException("type is missing: " + fullName);
    }

    private static MethodDefinition RequireMethod(TypeDefinition type, string name)
    {
        return type.Methods.SingleOrDefault(method => method.Name == name && !method.HasParameters)
            ?? throw new InvalidOperationException($"method is missing: {type.FullName}.{name}()");
    }
}
