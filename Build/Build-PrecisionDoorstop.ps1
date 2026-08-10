[CmdletBinding()]
param(
    [switch]$DeployToGame,
    [switch]$SkipProtectedBuild,
    [string]$GameRoot = 'C:\Users\x1a0reN\AppData\Local\ASWII\Data'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$protectedBuild = Join-Path $PSScriptRoot 'Build-ProtectedRelease.ps1'
$protectedAssembly = Join-Path $projectRoot 'bin\Protected\ASWDEBUG.dll'
$configSource = Join-Path $projectRoot 'Doorstop\x86\doorstop_config.ini'

function Get-PeMachine {
    param([Parameter(Mandatory = $true)][string]$Path)

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::ReadWrite)
    try {
        $reader = New-Object IO.BinaryReader($stream)
        try {
            if ($reader.ReadUInt16() -ne 0x5A4D) {
                throw "PE file is missing the MZ signature: $Path"
            }
            $stream.Position = 0x3C
            $peOffset = $reader.ReadUInt32()
            $stream.Position = $peOffset
            if ($reader.ReadUInt32() -ne 0x00004550) {
                throw "PE file is missing the PE signature: $Path"
            }
            return $reader.ReadUInt16()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

if ($DeployToGame -and (Get-Process -Name 'ASWII' -ErrorAction SilentlyContinue)) {
    throw 'ASWII.exe is running. Exit the game before deployment.'
}

if (-not $SkipProtectedBuild) {
    & $protectedBuild
}
if (-not (Test-Path -LiteralPath $protectedAssembly -PathType Leaf)) {
    throw "Protected core is missing: $protectedAssembly"
}

$configText = Get-Content -LiteralPath $configSource -Raw
foreach ($pattern in @(
    '(?m)^enabled=true\s*$',
    '(?m)^target_assembly=ASWII_Data\\Managed\\ASWDEBUG\.dll\s*$',
    '(?m)^dll_search_path_override=ASWII_Data\\Managed\s*$'
)) {
    if ($configText -notmatch $pattern) {
        throw "Doorstop x86 configuration is invalid: $pattern"
    }
}

$files = @(
    [pscustomobject]@{
        Name = 'ASWDEBUG.dll'
        Source = $protectedAssembly
        RelativeTarget = 'ASWII_Data\Managed\ASWDEBUG.dll'
    },
    [pscustomobject]@{
        Name = '0Harmony.dll'
        Source = (Join-Path $projectRoot 'Lib\0Harmony.dll')
        RelativeTarget = 'ASWII_Data\Managed\0Harmony.dll'
    },
    [pscustomobject]@{
        Name = 'Mono.Cecil.dll'
        Source = (Join-Path $projectRoot 'Lib\Mono.Cecil.dll')
        RelativeTarget = 'ASWII_Data\Managed\Mono.Cecil.dll'
    },
    [pscustomobject]@{
        Name = 'BouncyCastle.Crypto.dll'
        Source = (Join-Path $projectRoot 'Lib\BouncyCastle.Crypto.dll')
        RelativeTarget = 'ASWII_Data\Managed\BouncyCastle.Crypto.dll'
    },
    [pscustomobject]@{
        Name = 'winhttp.dll'
        Source = (Join-Path $projectRoot 'Doorstop\x86\winhttp.dll')
        RelativeTarget = 'winhttp.dll'
    },
    [pscustomobject]@{
        Name = 'doorstop_config.ini'
        Source = $configSource
        RelativeTarget = 'doorstop_config.ini'
    }
)

foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
        throw "Deployment source is missing: $($file.Source)"
    }
    if ($file.RelativeTarget -match '(^|\\)Assembly-CSharp\.dll$') {
        throw 'The deployment set must never overwrite the game Assembly-CSharp.dll.'
    }
}

$stamp = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$artifactRoot = Join-Path $projectRoot ("artifacts\PrecisionDoorstop\$stamp")
$packageRoot = Join-Path $artifactRoot 'package'
New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null

foreach ($file in $files) {
    $packagePath = Join-Path $packageRoot $file.RelativeTarget
    New-Item -ItemType Directory -Path (
        Split-Path -Parent $packagePath) -Force | Out-Null
    Copy-Item -LiteralPath $file.Source -Destination $packagePath -Force
}

$resolvedGameRoot = $null
$backupRoot = $null
if ($DeployToGame) {
    $resolvedGameRoot = [IO.Path]::GetFullPath($GameRoot).TrimEnd('\')
    if (-not (Test-Path -LiteralPath $resolvedGameRoot -PathType Container)) {
        throw "Game root does not exist: $resolvedGameRoot"
    }

    $gameExecutable = Join-Path $resolvedGameRoot 'ASWII.exe'
    if (-not (Test-Path -LiteralPath $gameExecutable -PathType Leaf)) {
        throw "Game executable is missing: $gameExecutable"
    }
    $machine = Get-PeMachine -Path $gameExecutable
    if ($machine -ne 0x014C) {
        throw ('ASWII.exe is not x86 (Machine=0x{0:X4}); refusing x86 Doorstop deployment.' -f $machine)
    }

    $backupRoot = Join-Path $resolvedGameRoot (
        "ASWDEBUG.DeployBackups\Precision_$stamp")
    foreach ($file in $files) {
        $target = [IO.Path]::GetFullPath(
            (Join-Path $resolvedGameRoot $file.RelativeTarget))
        if (-not $target.StartsWith(
            $resolvedGameRoot + '\',
            [StringComparison]::OrdinalIgnoreCase)) {
            throw "Deployment target escaped the game root: $target"
        }

        $targetDirectory = Split-Path -Parent $target
        New-Item -ItemType Directory -Path $targetDirectory -Force | Out-Null
        $sourceHash = (
            Get-FileHash -LiteralPath $file.Source -Algorithm SHA256
        ).Hash
        if (Test-Path -LiteralPath $target -PathType Leaf) {
            $targetHash = (
                Get-FileHash -LiteralPath $target -Algorithm SHA256
            ).Hash
            if ($targetHash -ne $sourceHash) {
                $backupPath = Join-Path $backupRoot $file.RelativeTarget
                New-Item -ItemType Directory -Path (
                    Split-Path -Parent $backupPath) -Force | Out-Null
                Copy-Item -LiteralPath $target -Destination $backupPath
            }
        }

        Copy-Item -LiteralPath $file.Source -Destination $target -Force
    }
}

$verification = foreach ($file in $files) {
    $sourceHash = (
        Get-FileHash -LiteralPath $file.Source -Algorithm SHA256
    ).Hash
    $packagePath = Join-Path $packageRoot $file.RelativeTarget
    $packageHash = (
        Get-FileHash -LiteralPath $packagePath -Algorithm SHA256
    ).Hash
    $target = if ($DeployToGame) {
        Join-Path $resolvedGameRoot $file.RelativeTarget
    }
    else {
        $null
    }
    $targetHash = if ($target -and (
        Test-Path -LiteralPath $target -PathType Leaf)) {
        (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
    }
    else {
        $null
    }

    [pscustomobject]@{
        Name = $file.Name
        Source = $file.Source
        Bytes = (Get-Item -LiteralPath $file.Source).Length
        SHA256 = $sourceHash
        PackageMatch = $sourceHash -eq $packageHash
        Target = $target
        TargetMatch = if ($DeployToGame) { $sourceHash -eq $targetHash } else { $null }
    }
}

if ($verification.PackageMatch -contains $false) {
    throw 'At least one packaged file failed SHA-256 verification.'
}
if ($DeployToGame -and ($verification.TargetMatch -contains $false)) {
    throw 'At least one deployed file failed SHA-256 verification.'
}

$manifest = [ordered]@{
    Format = 'aswdebug-precision-doorstop-v1'
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('o')
    Branch = (& git -C $projectRoot branch --show-current).Trim()
    Commit = (& git -C $projectRoot rev-parse HEAD).Trim()
    WorkingTreeDirty = [bool](& git -C $projectRoot status --porcelain)
    CoreConfiguration = 'Protected'
    Architecture = 'x86'
    DeployToGame = [bool]$DeployToGame
    GameRoot = $resolvedGameRoot
    BackupRoot = $backupRoot
    NetworkValidation = [ordered]@{
        Mode = 'none'
        SecretMaterialPackaged = $false
    }
    Files = $verification
}
$manifestPath = Join-Path $artifactRoot 'manifest.json'
$manifest | ConvertTo-Json -Depth 7 | Set-Content `
    -LiteralPath $manifestPath -Encoding UTF8

$verification | Format-Table `
    Name,Bytes,SHA256,PackageMatch,TargetMatch -AutoSize
"Package: $packageRoot"
"Manifest: $manifestPath"
if ($backupRoot) {
    "Backup: $backupRoot"
}
