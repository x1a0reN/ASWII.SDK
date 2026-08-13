[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RawAssembly,

    [Parameter(Mandatory = $true)]
    [string]$ProtectedAssembly,

    [Parameter(Mandatory = $true)]
    [string]$MappingFile,

    [string]$ExpectedVersion = '1.0.53.0',

    [string]$WorkRoot
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$cecil = Join-Path $projectRoot 'Lib\Mono.Cecil.dll'
$source = Join-Path $PSScriptRoot 'ProtectedReleaseVerifier.cs'

foreach ($required in @(
    $compiler,
    $cecil,
    $source,
    $RawAssembly,
    $ProtectedAssembly,
    $MappingFile
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required protected-release input not found: $required"
    }
}

if ([string]::IsNullOrWhiteSpace($WorkRoot)) {
    $WorkRoot = Join-Path $projectRoot (
        'artifacts\ProtectedReleaseVerify\' +
        (Get-Date -Format 'yyyyMMdd_HHmmss_fff'))
}
New-Item -ItemType Directory -Path $WorkRoot -Force | Out-Null

$verifier = Join-Path $WorkRoot 'ProtectedReleaseVerifier.exe'
$runtimeCecil = Join-Path $WorkRoot 'Mono.Cecil.dll'
Copy-Item -LiteralPath $cecil -Destination $runtimeCecil -Force

& $compiler /nologo /target:exe /optimize+ "/out:$verifier" "/reference:$cecil" $source
if ($LASTEXITCODE -ne 0) {
    throw "Protected release verifier compilation failed: $LASTEXITCODE"
}

& $verifier $RawAssembly $ProtectedAssembly $MappingFile $ExpectedVersion
if ($LASTEXITCODE -ne 0) {
    throw "Protected release verification failed: $LASTEXITCODE"
}

$protectedPdb = [IO.Path]::ChangeExtension($ProtectedAssembly, '.pdb')
if (Test-Path -LiteralPath $protectedPdb -PathType Leaf) {
    throw "Protected release must not publish a PDB: $protectedPdb"
}

Write-Output $WorkRoot
