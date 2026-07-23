[CmdletBinding()]
param(
    [string]$MSBuildPath = 'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe',
    [string]$ArtifactRoot,
    [switch]$SkipDeploy
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'ASWDEBUG.csproj'
$converterProject = Join-Path $repositoryRoot 'Tools\CompactNavConverter\CompactNavConverter.csproj'
$cecilPath = Join-Path $repositoryRoot 'Lib\Mono.Cecil.dll'

if ([string]::IsNullOrWhiteSpace($ArtifactRoot)) {
    $ArtifactRoot = Join-Path $repositoryRoot 'artifacts\SurvivalBot'
}
if (-not (Test-Path -LiteralPath $MSBuildPath -PathType Leaf)) {
    throw "MSBuild not found: $MSBuildPath"
}
if (-not (Test-Path -LiteralPath $cecilPath -PathType Leaf)) {
    throw "Mono.Cecil not found: $cecilPath"
}

$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$runRoot = Join-Path $ArtifactRoot $runId
$privateBuild = Join-Path $runRoot 'build\Private'
$releaseBuild = Join-Path $runRoot 'build\ReleaseA'
$privateIntermediate = Join-Path $runRoot 'obj\Private'
$releaseIntermediate = Join-Path $runRoot 'obj\ReleaseA'
$converterBuild = Join-Path $runRoot 'build\CompactNavConverter'
$converterIntermediate = Join-Path $runRoot 'obj\CompactNavConverter'
$privatePackage = Join-Path $runRoot 'Private\Game'
$releasePackage = Join-Path $runRoot 'ReleaseA\Game'

function New-ArtifactDirectory {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) {
        New-Item -ItemType Directory -Path $Path | Out-Null
    }
}

function Invoke-ProjectBuild {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Configuration,
        [Parameter(Mandatory = $true)][string]$OutputPath,
        [Parameter(Mandatory = $true)][string]$IntermediatePath,
        [string]$Edition
    )

    New-ArtifactDirectory $OutputPath
    New-ArtifactDirectory $IntermediatePath
    $arguments = @(
        $Project,
        '/t:Build',
        "/p:Configuration=$Configuration",
        "/p:OutputPath=$OutputPath\",
        "/p:IntermediateOutputPath=$IntermediatePath\",
        '/p:DeployToGame=false',
        '/m',
        '/v:minimal'
    )
    if (-not [string]::IsNullOrEmpty($Edition)) {
        $arguments += "/p:SurvivalEdition=$Edition"
    }

    & $MSBuildPath @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "MSBuild failed for $Project ($Configuration/$Edition), exit code $LASTEXITCODE"
    }
}

function Copy-RequiredFile {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )
    if (-not (Test-Path -LiteralPath $Source -PathType Leaf)) {
        throw "Required file not found: $Source"
    }
    New-ArtifactDirectory (Split-Path -Parent $Destination)
    Copy-Item -LiteralPath $Source -Destination $Destination -Force
}

function Get-AllCecilTypes {
    param([System.Collections.IEnumerable]$Types)
    $result = New-Object System.Collections.ArrayList
    foreach ($type in $Types) {
        [void]$result.Add($type)
        if ($type.HasNestedTypes) {
            foreach ($nested in (Get-AllCecilTypes $type.NestedTypes)) {
                [void]$result.Add($nested)
            }
        }
    }
    return $result
}

function Assert-Condition {
    param(
        [Parameter(Mandatory = $true)][bool]$Condition,
        [Parameter(Mandatory = $true)][string]$Message
    )
    if (-not $Condition) {
        throw "Variant audit failed: $Message"
    }
}

function ConvertFrom-Utf8Base64 {
    param([Parameter(Mandatory = $true)][string]$Value)
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($Value))
}

function Test-VariantAssembly {
    param(
        [Parameter(Mandatory = $true)][string]$AssemblyPath,
        [Parameter(Mandatory = $true)][ValidateSet('Private', 'ReleaseA')][string]$Edition
    )

    $assembly = [Mono.Cecil.AssemblyDefinition]::ReadAssembly($AssemblyPath)
    try {
        $types = @(Get-AllCecilTypes $assembly.MainModule.Types)
        $typeNames = @($types | ForEach-Object { $_.FullName })
        $internalTypes = @(
            'ASWDEBUG.Cheats.AutoBattle.AutoBattleManager',
            'ASWDEBUG.Cheats.SurvivalBot.LocalNavigationCombatTest',
            'ASWDEBUG.Cheats.SurvivalBot.MapBakeSceneLoader'
        )
        $removedCommonTypes = @(
            'ASWDEBUG.Cheats.SurvivalBot.SurvivalTacticsPreset',
            'ASWDEBUG.Cheats.SurvivalBot.SurvivalDefensePreset'
        )
        $allMethods = @($types | ForEach-Object { $_.Methods })
        $stringLiterals = @(
            $allMethods |
                Where-Object { $_.HasBody } |
                ForEach-Object { $_.Body.Instructions } |
                Where-Object { $_.Operand -is [string] } |
                ForEach-Object { [string]$_.Operand }
        )

        foreach ($typeName in $removedCommonTypes) {
            Assert-Condition (-not ($typeNames -contains $typeName)) "$Edition contains removed type $typeName"
        }

        if ($Edition -eq 'Private') {
            foreach ($typeName in $internalTypes) {
                Assert-Condition ($typeNames -contains $typeName) "Private is missing internal type $typeName"
            }
        }
        else {
            foreach ($typeName in $internalTypes) {
                Assert-Condition (-not ($typeNames -contains $typeName)) "ReleaseA contains internal type $typeName"
            }
        }

        $settingsType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.SurvivalBot.SurvivalBotSettings'
        } | Select-Object -First 1
        Assert-Condition ($null -ne $settingsType) "$Edition is missing SurvivalBotSettings"
        $removedSettingsMethods = @(
            'get_TacticsMode',
            'get_TacticsPreset',
            'get_RoleStrategyEnabled',
            'get_DefenseMode',
            'get_DefensePreset',
            'SetTacticsMode',
            'SetRoleStrategyEnabled',
            'SetDefenseMode'
        )
        $settingsMethodNames = @($settingsType.Methods | ForEach-Object { $_.Name })
        foreach ($methodName in $removedSettingsMethods) {
            Assert-Condition (-not ($settingsMethodNames -contains $methodName)) "$Edition contains removed setting $methodName"
        }
        $removedUiFragments = @(
            '5oiY5pyv',
            '6IGM5Lia562W55Wl',
            '5L+d5ZG95oqA6IO9'
        ) | ForEach-Object { ConvertFrom-Utf8Base64 $_ }
        foreach ($fragment in $removedUiFragments) {
            $matched = @($stringLiterals | Where-Object { $_.Contains($fragment) })
            Assert-Condition ($matched.Count -eq 0) "$Edition contains removed UI literal $fragment"
        }

        $managerType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.SurvivalBot.SurvivalBotManager'
        } | Select-Object -First 1
        $antiIdleType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.SurvivalBot.SurvivalAntiIdle'
        } | Select-Object -First 1
        $adapterType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.SurvivalCombatAdapter'
        } | Select-Object -First 1
        $roleType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.SurvivalRoleKind'
        } | Select-Object -First 1
        $compactRuntimeType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.CompactNav.CompactRainNavRuntime'
        } | Select-Object -First 1
        $routePlannerType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.AutoBattleRoutePlanner'
        } | Select-Object -First 1
        $routeFinalizeType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.AutoBattleRoutePlanner/RainRouteFinalizeJob'
        } | Select-Object -First 1
        $denseProbeType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Cheats.AutoBattle.AutoBattleRoutePlanner/DenseSegmentProbe'
        } | Select-Object -First 1
        Assert-Condition ($null -ne $managerType) "$Edition is missing SurvivalBotManager"
        Assert-Condition ($null -ne $antiIdleType) "$Edition is missing SurvivalAntiIdle"
        Assert-Condition ($null -ne $adapterType) "$Edition is missing SurvivalCombatAdapter"
        Assert-Condition ($null -ne $roleType) "$Edition is missing SurvivalRoleKind"
        Assert-Condition ($null -ne $compactRuntimeType) "$Edition is missing CompactRainNavRuntime"
        Assert-Condition ($null -ne $routePlannerType) "$Edition is missing AutoBattleRoutePlanner"
        Assert-Condition ($null -ne $routeFinalizeType) "$Edition is missing sliced route finalizer"
        Assert-Condition ($null -ne $denseProbeType) "$Edition is missing sliced dense probe"
        $requiredManagerMethods = @(
            'TickRoleDirector',
            'TickGuardDirector',
            'TickAssaultDirector',
            'MoveCombatStrafe',
            'IsSafeCombatDirection',
            'TraceCombatMovement',
            'PreAimPursuitTarget',
            'IsActiveVisibleEmergencyThreat',
            'TickFindCliff',
            'MoveWhileSearchingCliff',
            'ShouldRejectExposedHideRoute',
            'TryValidateCliffApproach'
        )
        $managerMethodNames = @($managerType.Methods | ForEach-Object { $_.Name })
        foreach ($methodName in $requiredManagerMethods) {
            Assert-Condition ($managerMethodNames -contains $methodName) "$Edition is missing director method $methodName"
        }
        $antiIdleMethodNames = @($antiIdleType.Methods | ForEach-Object { $_.Name })
        Assert-Condition ($antiIdleMethodNames -contains 'OnFightStateUpdate') `
            "$Edition is missing FightState anti-idle reset"
        Assert-Condition ($settingsMethodNames -contains 'get_IgnoreIdleKickEnabled') `
            "$Edition is missing anti-idle setting"
        $requiredAdapterMethods = @(
            'DetectSurvivalRole',
            'TryUseSurvivalSkill',
            'PrepareSurvivalTargetSkill',
            'PreAimSurvivalTarget'
        )
        $adapterMethodNames = @($adapterType.Methods | ForEach-Object { $_.Name })
        foreach ($methodName in $requiredAdapterMethods) {
            Assert-Condition ($adapterMethodNames -contains $methodName) "$Edition is missing adapter method $methodName"
        }
        $compactRuntimeMethodNames = @($compactRuntimeType.Methods | ForEach-Object { $_.Name })
        Assert-Condition ($compactRuntimeMethodNames -contains 'CollectNearbyBoundaries') `
            "$Edition is missing compact boundary lookup"
        $routePlannerMethodNames = @($routePlannerType.Methods | ForEach-Object { $_.Name })
        Assert-Condition ($routePlannerMethodNames -contains 'TickRainRouteFinalize') `
            "$Edition is missing sliced route finalization entry"
        $routeFinalizeMethodNames = @($routeFinalizeType.Methods | ForEach-Object { $_.Name })
        foreach ($methodName in @('Tick', 'Optimize', 'Validate', 'Annotate')) {
            Assert-Condition ($routeFinalizeMethodNames -contains $methodName) `
                "$Edition is missing sliced finalizer method $methodName"
        }

        if ($Edition -eq 'ReleaseA') {
            $forbiddenManagerMethods = @(
                'SetCombatTestEnabled',
                'SetRoomTestEnabled',
                'SetMapBakeEnabled',
                'RequestDirectMapBake',
                'SetLevel33TestEnabled',
                'TickCombatTest',
                'TickRoomTest',
                'TickMapBake',
                'EnsureMapBake',
                'PlayerSyncPrefix',
                'LocalTestShotPrefix'
            )
            $managerMethodNames = @($allMethods | ForEach-Object { $_.Name })
            foreach ($methodName in $forbiddenManagerMethods) {
                Assert-Condition (-not ($managerMethodNames -contains $methodName)) "ReleaseA contains $methodName"
            }
            $internalUiFragments = @(
                '5oiY5paX5rWL6K+V',
                '5byA5oi/5rWL6K+V',
                '5Zyw5Zu+5bu65Zu+',
                '57qv5a+76Lev5beh5Zue',
                '55u05o6l5Yqg6L295bm25bu65Zu+'
            ) | ForEach-Object { ConvertFrom-Utf8Base64 $_ }
            foreach ($fragment in $internalUiFragments) {
                $matched = @($stringLiterals | Where-Object { $_.Contains($fragment) })
                Assert-Condition ($matched.Count -eq 0) "ReleaseA contains internal UI literal $fragment"
            }
        }

        $profileType = $types | Where-Object {
            $_.FullName -eq 'ASWDEBUG.Build.SurvivalBuildProfile'
        } | Select-Object -First 1
        Assert-Condition ($null -ne $profileType) "$Edition is missing build profile metadata"
        $editionField = $profileType.Fields | Where-Object { $_.Name -eq 'Edition' } | Select-Object -First 1
        Assert-Condition ($null -ne $editionField -and $editionField.HasConstant) "$Edition profile has no Edition constant"
        Assert-Condition ([string]$editionField.Constant -eq $Edition) "$Edition profile constant is $($editionField.Constant)"

        return [ordered]@{
            edition = $Edition
            typeCount = $types.Count
            internalTools = ($Edition -eq 'Private')
            audit = 'passed'
        }
    }
    finally {
        if ($assembly -is [System.IDisposable]) {
            ([System.IDisposable]$assembly).Dispose()
        }
    }
}

function Get-HashRecord {
    param([Parameter(Mandatory = $true)][string]$Path)
    $file = Get-Item -LiteralPath $Path
    return [ordered]@{
        path = $file.FullName.Substring($runRoot.Length + 1)
        length = $file.Length
        sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
    }
}

function Copy-GamePackage {
    param(
        [Parameter(Mandatory = $true)][string]$AssemblyPath,
        [Parameter(Mandatory = $true)][string]$PackageRoot,
        [string]$ConverterPath,
        [switch]$FullSupportFiles
    )

    $managed = Join-Path $PackageRoot 'ASWII_Data\Managed'
    Copy-RequiredFile $AssemblyPath (Join-Path $managed 'ASWDEBUG.dll')
    Copy-RequiredFile (Join-Path $repositoryRoot 'Lib\0Harmony.dll') (Join-Path $managed '0Harmony.dll')
    if ($FullSupportFiles) {
        Copy-RequiredFile (Join-Path $repositoryRoot 'Lib\Mono.Cecil.dll') (Join-Path $managed 'Mono.Cecil.dll')
        Copy-RequiredFile (Join-Path $repositoryRoot 'Lib\BouncyCastle.Crypto.dll') (Join-Path $managed 'BouncyCastle.Crypto.dll')
    }
    Copy-RequiredFile (Join-Path $repositoryRoot 'Doorstop\x86\winhttp.dll') (Join-Path $PackageRoot 'winhttp.dll')
    Copy-RequiredFile (Join-Path $repositoryRoot 'Doorstop\x86\doorstop_config.ini') (Join-Path $PackageRoot 'doorstop_config.ini')
    if (-not [string]::IsNullOrEmpty($ConverterPath)) {
        Copy-RequiredFile $ConverterPath (Join-Path $PackageRoot 'ASWDEBUG.Tools\CompactNavConverter.exe')
    }
}

function Deploy-PrivatePackage {
    param([Parameter(Mandatory = $true)][string]$PackageRoot)

    $gameRoot = 'C:\Users\x1a0reN\AppData\Local\ASWII\Data'
    $managed = Join-Path $gameRoot 'ASWII_Data\Managed'
    $pairs = @(
        @((Join-Path $PackageRoot 'ASWII_Data\Managed\ASWDEBUG.dll'), (Join-Path $managed 'ASWDEBUG.dll')),
        @((Join-Path $PackageRoot 'ASWII_Data\Managed\0Harmony.dll'), (Join-Path $managed '0Harmony.dll')),
        @((Join-Path $PackageRoot 'ASWII_Data\Managed\Mono.Cecil.dll'), (Join-Path $managed 'Mono.Cecil.dll')),
        @((Join-Path $PackageRoot 'ASWII_Data\Managed\BouncyCastle.Crypto.dll'), (Join-Path $managed 'BouncyCastle.Crypto.dll')),
        @((Join-Path $PackageRoot 'winhttp.dll'), (Join-Path $gameRoot 'winhttp.dll')),
        @((Join-Path $PackageRoot 'doorstop_config.ini'), (Join-Path $gameRoot 'doorstop_config.ini')),
        @((Join-Path $PackageRoot 'ASWDEBUG.Tools\CompactNavConverter.exe'),
          (Join-Path $gameRoot 'ASWDEBUG.Tools\CompactNavConverter.exe'))
    )

    foreach ($pair in $pairs) {
        Copy-RequiredFile $pair[0] $pair[1]
        $sourceHash = (Get-FileHash -LiteralPath $pair[0] -Algorithm SHA256).Hash
        $targetHash = (Get-FileHash -LiteralPath $pair[1] -Algorithm SHA256).Hash
        if ($sourceHash -ne $targetHash) {
            throw "Deployment hash mismatch: $($pair[1])"
        }
    }
}

New-ArtifactDirectory $runRoot
Write-Host "[1/6] Building Private edition..."
Invoke-ProjectBuild $projectPath 'Debug' $privateBuild $privateIntermediate 'Private'
Write-Host "[2/6] Building ReleaseA edition..."
Invoke-ProjectBuild $projectPath 'Release' $releaseBuild $releaseIntermediate 'ReleaseA'
Write-Host "[3/6] Building Private-only CompactNavConverter..."
Invoke-ProjectBuild $converterProject 'Release' $converterBuild $converterIntermediate ''

$privateAssembly = Join-Path $privateBuild 'ASWDEBUG.dll'
$releaseAssembly = Join-Path $releaseBuild 'ASWDEBUG.dll'
$converterAssembly = Join-Path $converterBuild 'CompactNavConverter.exe'

Add-Type -Path $cecilPath
Write-Host "[4/6] Auditing compiled feature boundaries..."
$privateAudit = Test-VariantAssembly $privateAssembly 'Private'
$releaseAudit = Test-VariantAssembly $releaseAssembly 'ReleaseA'

Write-Host "[5/6] Creating deployable packages..."
Copy-GamePackage $privateAssembly $privatePackage $converterAssembly -FullSupportFiles
Copy-GamePackage $releaseAssembly $releasePackage

$packageFiles = @(
    Get-ChildItem -LiteralPath (Join-Path $runRoot 'Private') -File -Recurse
    Get-ChildItem -LiteralPath (Join-Path $runRoot 'ReleaseA') -File -Recurse
)
$hashes = @($packageFiles | Sort-Object FullName | ForEach-Object { Get-HashRecord $_.FullName })
$gitCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null)
$gitDirty = [bool](& git -C $repositoryRoot status --porcelain 2>$null)
$manifest = [ordered]@{
    schemaVersion = 1
    generatedAt = (Get-Date).ToString('o')
    repository = $repositoryRoot
    gitCommit = [string]$gitCommit
    gitDirty = $gitDirty
    featureMatrix = [ordered]@{
        Private = [ordered]@{
            internalTools = $true
            mapBake = $true
            combatTest = $true
            roomTest = $true
            level33LocalPatrol = $true
            configurableTactics = $false
            configurableRoleStrategy = $false
            configurableSurvivalDefenseSkills = $false
            automaticRoleDirector = $true
            ignoreIdleKick = $true
            continuousCombatMovement = $true
            incrementalCliffSearch = $true
            compactBoundaryCliffSource = $true
            slicedExactRouteFinalization = $true
            huntTargetPreAim = $true
            concealedRouteAudit = $true
            lethalCliffValidation = $true
        }
        ReleaseA = [ordered]@{
            internalTools = $false
            mapBake = $false
            combatTest = $false
            roomTest = $false
            level33LocalPatrol = $false
            configurableTactics = $false
            configurableRoleStrategy = $false
            configurableSurvivalDefenseSkills = $false
            automaticRoleDirector = $true
            ignoreIdleKick = $true
            continuousCombatMovement = $true
            incrementalCliffSearch = $true
            compactBoundaryCliffSource = $true
            slicedExactRouteFinalization = $true
            huntTargetPreAim = $true
            concealedRouteAudit = $true
            lethalCliffValidation = $true
        }
    }
    audits = @($privateAudit, $releaseAudit)
    files = $hashes
}
$manifestPath = Join-Path $runRoot 'manifest.json'
$manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "[6/6] Finalizing deployment..."
if (-not $SkipDeploy) {
    Deploy-PrivatePackage $privatePackage
    Write-Host 'Private edition deployed with SHA-256 verification.'
}
else {
    Write-Host 'Deployment skipped by -SkipDeploy.'
}

Write-Host "Variant build complete: $runRoot"
Write-Output $runRoot
