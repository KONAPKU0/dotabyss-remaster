param(
    [Parameter(Mandatory = $true)]
    [string]$InputDll,

    [Parameter(Mandatory = $true)]
    [string]$OutputDll
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$cecilPath = Join-Path $repoRoot 'BepInEx\core\Mono.Cecil.dll'
Add-Type -Path $cecilPath

$disabledTypes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($name in @(
    'AbyssSniff.Patches.DefenceProbePatch',
    'AbyssSniff.Patches.DpsProbePatch',
    'AbyssSniff.Patches.AccessoryProbePatch',
    'AbyssSniff.Patches.AbnormalConditionFixPatch',
    'AbyssSniff.Patches.BuffBugFixPatch',
    'AbyssSniff.Patches.AutoDefensiveTowerPatch',
    'AbyssSniff.Patches.LaveriaTeamKillFixPatch',
    'AbyssSniff.Patches.AttackContinuousProbePatch',
    'AbyssSniff.Patches.SylviaSummonProbePatch',
    'AbyssSniff.Patches.ExplorationMissionBadgeFixPatch',
    'AbyssSniff.Patches.ManaGemUnequipPatch',
    'AbyssSniff.Reroll.UpdateNotifier'
)) {
    [void]$disabledTypes.Add($name)
}

$requiredTypes = @(
    'AbyssSniff.Patches.ApiSniffPatch',
    'AbyssSniff.Reroll.NetherCheckpointPatch',
    'AbyssSniff.Reroll.DropAnalyzer'
)

function Get-RequiredType {
    param(
        [Mono.Cecil.AssemblyDefinition]$Assembly,
        [string]$FullName
    )

    $matches = @($Assembly.MainModule.Types | Where-Object FullName -eq $FullName)
    if ($matches.Count -ne 1) {
        throw "Expected one type '$FullName', found $($matches.Count)."
    }
    return $matches[0]
}

function Get-RequiredMethod {
    param(
        [Mono.Cecil.TypeDefinition]$Type,
        [string]$Name
    )

    $matches = @($Type.Methods | Where-Object { $_.Name -eq $Name -and -not $_.HasParameters })
    if ($matches.Count -ne 1) {
        throw "Expected one method '$($Type.FullName).$Name()', found $($matches.Count)."
    }
    return $matches[0]
}

$inputPath = (Resolve-Path -LiteralPath $InputDll).Path
$outputPath = [System.IO.Path]::GetFullPath($OutputDll)
$reader = [Mono.Cecil.ReaderParameters]::new()
$reader.InMemory = $true
$reader.ReadSymbols = $false
$assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($inputPath, $reader)

try {
    $plugin = Get-RequiredType $assembly 'AbyssSniff.Plugin'
    $load = Get-RequiredMethod $plugin 'Load'
    $disabledFound = [System.Collections.Generic.HashSet[string]]::new(
        [System.StringComparer]::Ordinal)

    foreach ($instruction in $load.Body.Instructions) {
        if (($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Call) -and
            ($instruction.OpCode.Code -ne [Mono.Cecil.Cil.Code]::Callvirt)) {
            continue
        }
        if ($instruction.Operand -isnot [Mono.Cecil.MethodReference]) {
            continue
        }

        $reference = [Mono.Cecil.MethodReference]$instruction.Operand
        if ($reference.Name -eq 'Initialize' -and $disabledTypes.Contains($reference.DeclaringType.FullName)) {
            [void]$disabledFound.Add($reference.DeclaringType.FullName)
            $instruction.OpCode = [Mono.Cecil.Cil.OpCodes]::Nop
            $instruction.Operand = $null
        }
    }

    $missingDisabled = @($disabledTypes | Where-Object { -not $disabledFound.Contains($_) })
    if ($missingDisabled.Count -ne 0) {
        throw 'Missing expected combat startup calls: ' + ($missingDisabled -join ', ')
    }

    $remainingCalls = @($load.Body.Instructions |
        Where-Object { $_.Operand -is [Mono.Cecil.MethodReference] } |
        ForEach-Object { ([Mono.Cecil.MethodReference]$_.Operand).DeclaringType.FullName })
    $missingRequired = @($requiredTypes | Where-Object { $_ -notin $remainingCalls })
    if ($missingRequired.Count -ne 0) {
        throw 'Missing required automation startup calls: ' + ($missingRequired -join ', ')
    }

    $dropAnalyzer = Get-RequiredType $assembly 'AbyssSniff.Reroll.DropAnalyzer'
    $dropInitialize = Get-RequiredMethod $dropAnalyzer 'Initialize'
    $characterCalls = @($dropInitialize.Body.Instructions | Where-Object {
        $_.Operand -is [Mono.Cecil.MethodReference] -and
        ([Mono.Cecil.MethodReference]$_.Operand).DeclaringType.FullName -eq 'AbyssSniff.Reroll.CharacterFixes' -and
        ([Mono.Cecil.MethodReference]$_.Operand).Name -eq 'Initialize'
    })
    if ($characterCalls.Count -ne 1) {
        throw "Expected one CharacterFixes.Initialize call, found $($characterCalls.Count)."
    }
    $characterCalls[0].OpCode = [Mono.Cecil.Cil.OpCodes]::Pop
    $characterCalls[0].Operand = $null

    $dispatcher = Get-RequiredType $assembly 'AbyssSniff.Reroll.MainThreadDispatcher'
    $dispatcherUpdate = Get-RequiredMethod $dispatcher 'Update'
    $dispatcherOnGui = Get-RequiredMethod $dispatcher 'OnGUI'
    $runtimeCalls = @(
        @($dispatcherUpdate, 'AbyssSniff.Reroll.CharacterFixes', 'Toggle', [Mono.Cecil.Cil.OpCodes]::Ldc_I4_0),
        @($dispatcherUpdate, 'AbyssSniff.Patches.ManaGemUnequipPatch', 'Tick', [Mono.Cecil.Cil.OpCodes]::Nop),
        @($dispatcherOnGui, 'AbyssSniff.Patches.BossResistanceOverlay', 'OnGUI', [Mono.Cecil.Cil.OpCodes]::Nop),
        @($dispatcherOnGui, 'AbyssSniff.Patches.DpsOverlay', 'OnGUI', [Mono.Cecil.Cil.OpCodes]::Nop),
        @($dispatcherOnGui, 'AbyssSniff.Reroll.UpdateNotifier', 'OnGUI', [Mono.Cecil.Cil.OpCodes]::Nop)
    )
    foreach ($spec in $runtimeCalls) {
        $method = [Mono.Cecil.MethodDefinition]$spec[0]
        $declaringType = [string]$spec[1]
        $calledMethod = [string]$spec[2]
        $replacement = [Mono.Cecil.Cil.OpCode]$spec[3]
        $matches = @($method.Body.Instructions | Where-Object {
            $_.Operand -is [Mono.Cecil.MethodReference] -and
            ([Mono.Cecil.MethodReference]$_.Operand).DeclaringType.FullName -eq $declaringType -and
            ([Mono.Cecil.MethodReference]$_.Operand).Name -eq $calledMethod
        })
        if ($matches.Count -ne 1) {
            throw "Expected one $declaringType.$calledMethod call in $($method.FullName), found $($matches.Count)."
        }
        $matches[0].OpCode = $replacement
        $matches[0].Operand = $null
    }

    $metadata = @($plugin.CustomAttributes | Where-Object {
        $_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'
    })
    if ($metadata.Count -ne 1) {
        throw "Expected one BepInPlugin attribute, found $($metadata.Count)."
    }
    $nameArgument = [Mono.Cecil.CustomAttributeArgument]::new(
        $metadata[0].ConstructorArguments[1].Type,
        [object]'AbyssSniff Automation Only')
    $versionArgument = [Mono.Cecil.CustomAttributeArgument]::new(
        $metadata[0].ConstructorArguments[2].Type,
        [object]'1.6.0-automation.1')
    $metadata[0].ConstructorArguments[1] = $nameArgument
    $metadata[0].ConstructorArguments[2] = $versionArgument

    $bannerMatches = @($load.Body.Instructions | Where-Object {
        $_.OpCode.Code -eq [Mono.Cecil.Cil.Code]::Ldstr -and
        $_.Operand -is [string] -and
        $_.Operand.Contains('AbyssSniff v1.6.0 loaded', [System.StringComparison]::Ordinal)
    })
    if ($bannerMatches.Count -ne 1) {
        throw "Expected one load banner, found $($bannerMatches.Count)."
    }
    $bannerMatches[0].Operand = $bannerMatches[0].Operand.Replace(
        'AbyssSniff v1.6.0 loaded',
        'AbyssSniff v1.6.0 automation-only loaded',
        [System.StringComparison]::Ordinal)

    $outputDirectory = Split-Path -Parent $outputPath
    if ($outputDirectory) {
        [System.IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
    }
    $writer = [Mono.Cecil.WriterParameters]::new()
    $writer.WriteSymbols = $false
    $assembly.Write($outputPath, $writer)

    Write-Output "Created $outputPath"
    Write-Output ('Disabled: ' + (($disabledFound | Sort-Object) -join ', '))
    Write-Output ('Kept: ' + ($requiredTypes -join ', '))
}
finally {
    $assembly.Dispose()
}
