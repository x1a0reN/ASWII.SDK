[CmdletBinding()]
param(
    [string]$MSBuildPath =
        'D:\Program Files\Visual Studio 2026\MSBuild\Current\Bin\MSBuild.exe',

    [string]$DotNetPath
)

Set-StrictMode -Version 2.0
$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $projectRoot 'ASWDEBUG.csproj'
$toolManifest = Join-Path $projectRoot '.config\dotnet-tools.json'
$expectedVersion = '1.0.48.0'
$obfuscarVersion = '2.2.50'
$gameAssemblyPath =
    'C:\Users\x1a0reN\AppData\LocalLow\____________II\Managed_Dump\' +
    'OFFLINE_FULL_20260806_212626\FULL_MANAGED\Assembly-CSharp.dll'
$expectedGameAssemblySha256 =
    'FE568D950B35C033946A99E930E00219D55CEDA8C033F9BD34176AC6DB5107D0'

if ([string]::IsNullOrWhiteSpace($DotNetPath)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -ne $command) {
        $DotNetPath = $command.Source
    }
    else {
        $DotNetPath = 'C:\Program Files\dotnet\dotnet.exe'
    }
}

foreach ($required in @(
    $MSBuildPath,
    $DotNetPath,
    $projectPath,
    $toolManifest,
    $gameAssemblyPath
)) {
    if (-not (Test-Path -LiteralPath $required -PathType Leaf)) {
        throw "Required protected-release tool or input not found: $required"
    }
}

$gameAssemblySha256 = (
    Get-FileHash -Algorithm SHA256 -LiteralPath $gameAssemblyPath
).Hash
if (-not [string]::Equals(
    $gameAssemblySha256,
    $expectedGameAssemblySha256,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Reviewed game assembly hash changed: $gameAssemblySha256"
}

$runId = Get-Date -Format 'yyyyMMdd_HHmmss_fff'
$runRoot = Join-Path $projectRoot ('artifacts\ProtectedRelease\' + $runId)
$rawRoot = Join-Path $runRoot 'raw'
$objRoot = Join-Path $runRoot 'obj'
$obfuscatedRoot = Join-Path $runRoot 'obfuscated'
$publishRoot = Join-Path $runRoot 'publish'
$verifyRoot = Join-Path $runRoot 'verify'

foreach ($directory in @(
    $runRoot,
    $rawRoot,
    $objRoot,
    $obfuscatedRoot,
    $publishRoot,
    $verifyRoot
)) {
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
}

& $MSBuildPath $projectPath /t:Build /p:Configuration=Protected `
    /p:Platform=AnyCPU /p:ProtectedReleaseDriver=true `
    /p:DeployToGame=false `
    "/p:OutputPath=$rawRoot\" "/p:IntermediateOutputPath=$objRoot\" `
    /m /v:minimal
if ($LASTEXITCODE -ne 0) {
    throw "Protected intermediate build failed: $LASTEXITCODE"
}

$rawAssembly = Join-Path $rawRoot 'ASWDEBUG.Runtime.dll'
if (-not (Test-Path -LiteralPath $rawAssembly -PathType Leaf)) {
    throw "Protected intermediate assembly was not produced: $rawAssembly"
}
if (Get-ChildItem -LiteralPath $rawRoot -Filter '*.pdb' -File) {
    throw "Protected intermediate output unexpectedly contains a PDB: $rawRoot"
}

Push-Location $projectRoot
try {
    & $DotNetPath tool restore --tool-manifest $toolManifest
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet tool restore failed: $LASTEXITCODE"
    }
    $toolVersion = (& $DotNetPath tool run obfuscar.console -V | Out-String)
    if ($LASTEXITCODE -ne 0 -or
        $toolVersion.IndexOf($obfuscarVersion, [StringComparison]::Ordinal) -lt 0) {
        throw "Unexpected Obfuscar version: $toolVersion"
    }
}
finally {
    Pop-Location
}

$mappingPath = Join-Path $runRoot 'obfuscation-map.xml'
$configPath = Join-Path $runRoot 'obfuscar.xml'
$xmlSettings = New-Object Xml.XmlWriterSettings
$xmlSettings.Indent = $true
$xmlSettings.Encoding = New-Object Text.UTF8Encoding($false)
$writer = [Xml.XmlWriter]::Create($configPath, $xmlSettings)
try {
    $writer.WriteStartDocument()
    $writer.WriteStartElement('Obfuscator')

    $variables = [ordered]@{
        InPath = $rawRoot
        OutPath = $obfuscatedRoot
        LogFile = $mappingPath
        XmlMapping = 'true'
        RegenerateDebugInfo = 'false'
        MarkedOnly = 'false'
        KeepPublicApi = 'true'
        HidePrivateApi = 'true'
        RenameProperties = 'false'
        RenameEvents = 'false'
        RenameFields = 'false'
        ReuseNames = 'true'
        UseUnicodeNames = 'false'
        # Obfuscar's generated string table fails to initialize on Unity 4.7's
        # legacy Mono runtime. Identifier obfuscation remains enabled.
        HideStrings = 'false'
        OptimizeMethods = 'false'
        SuppressIldasm = 'false'
        AnalyzeXaml = 'false'
        SkipSpecialName = 'true'
        SkipGenerated = 'true'
    }
    foreach ($item in $variables.GetEnumerator()) {
        $writer.WriteStartElement('Var')
        $writer.WriteAttributeString('name', [string]$item.Key)
        $writer.WriteAttributeString('value', [string]$item.Value)
        $writer.WriteEndElement()
    }

    $writer.WriteStartElement('Module')
    $writer.WriteAttributeString('file', $rawAssembly)
    foreach ($typeName in @(
        'Doorstop.Entrypoint',
        'ConsoleManager',
        'PlayerAutoNavRAIN',
        'ASWDEBUG.Main.CheatMain',
        'ASWDEBUG.Patch.HarmonyLoader',
        'ASWDEBUG.Verify.VeriGateAuthManager',
        'ASWDEBUG.Verify.RemoteNoticeCenter'
    )) {
        $writer.WriteStartElement('SkipType')
        $writer.WriteAttributeString('name', $typeName)
        $writer.WriteAttributeString('skipMethods', 'true')
        $writer.WriteAttributeString('skipFields', 'true')
        $writer.WriteAttributeString('skipProperties', 'true')
        $writer.WriteAttributeString('skipEvents', 'true')
        $writer.WriteEndElement()
    }
    $writer.WriteStartElement('SkipMethod')
    $writer.WriteAttributeString('type', '*')
    $writer.WriteAttributeString(
        'rx',
        '^(Awake|Start|Update|LateUpdate|FixedUpdate|OnGUI|OnEnable|OnDisable|OnDestroy|OnApplicationQuit|OnApplicationFocus|OnApplicationPause|OnLevelWasLoaded|OnDrawGizmos|OnDrawGizmosSelected|OnValidate|Reset|OnAnimatorIK|OnAnimatorMove|OnAudioFilterRead|OnBecameInvisible|OnBecameVisible|OnCollisionEnter|OnCollisionEnter2D|OnCollisionExit|OnCollisionExit2D|OnCollisionStay|OnCollisionStay2D|OnControllerColliderHit|OnJointBreak|OnJointBreak2D|OnMouseDown|OnMouseDrag|OnMouseEnter|OnMouseExit|OnMouseOver|OnMouseUp|OnMouseUpAsButton|OnParticleCollision|OnPostRender|OnPreCull|OnPreRender|OnRenderImage|OnRenderObject|OnServerInitialized|OnTransformChildrenChanged|OnTransformParentChanged|OnTriggerEnter|OnTriggerEnter2D|OnTriggerExit|OnTriggerExit2D|OnTriggerStay|OnTriggerStay2D|OnWillRenderObject|TargetMethod|Prefix|Postfix|Transpiler|GetCamForward|GetCamRight)$')
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndElement()
    $writer.WriteEndDocument()
}
finally {
    $writer.Dispose()
}

Push-Location $projectRoot
try {
    & $DotNetPath tool run obfuscar.console '-v:minimal' $configPath
    if ($LASTEXITCODE -ne 0) {
        throw "Obfuscar failed: $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

$obfuscatedAssembly = Join-Path $obfuscatedRoot 'ASWDEBUG.Runtime.dll'
if (-not (Test-Path -LiteralPath $obfuscatedAssembly -PathType Leaf)) {
    throw "Obfuscar did not produce the protected assembly: $obfuscatedAssembly"
}

$publishedAssembly = Join-Path $publishRoot 'ASWDEBUG.dll'
Copy-Item -LiteralPath $obfuscatedAssembly -Destination $publishedAssembly

& (Join-Path $PSScriptRoot 'Verify-ProtectedRelease.ps1') `
    -RawAssembly $rawAssembly `
    -ProtectedAssembly $publishedAssembly `
    -MappingFile $mappingPath `
    -ExpectedVersion $expectedVersion `
    -WorkRoot $verifyRoot

$stableRoot = Join-Path $projectRoot 'bin\Protected'
New-Item -ItemType Directory -Path $stableRoot -Force | Out-Null
$stableAssembly = Join-Path $stableRoot 'ASWDEBUG.dll'
if (Test-Path -LiteralPath $stableAssembly -PathType Leaf) {
    $oldHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stableAssembly).Hash
    $newHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $publishedAssembly).Hash
    if (-not [string]::Equals(
        $oldHash,
        $newHash,
        [StringComparison]::OrdinalIgnoreCase)) {
        $backupRoot = Join-Path $stableRoot ('backups\' + $runId)
        New-Item -ItemType Directory -Path $backupRoot -Force | Out-Null
        Copy-Item -LiteralPath $stableAssembly -Destination $backupRoot
    }
}
Copy-Item -LiteralPath $publishedAssembly -Destination $stableAssembly -Force

$stableHash = (Get-FileHash -Algorithm SHA256 -LiteralPath $stableAssembly).Hash
$stableItem = Get-Item -LiteralPath $stableAssembly
$manifest = [ordered]@{
    format = 'aswdebug-protected-release-v1'
    generated_at_utc = [DateTime]::UtcNow.ToString('o')
    configuration = 'Protected'
    target_framework = '.NET Framework 3.5 / CLR 2.0'
    assembly_version = $expectedVersion
    obfuscator = 'Obfuscar ' + $obfuscarVersion
    reviewed_game_assembly = [ordered]@{
        name = 'Assembly-CSharp.dll'
        bytes = (Get-Item -LiteralPath $gameAssemblyPath).Length
        sha256 = $gameAssemblySha256
        aim_report_version = 9
    }
    payload = [ordered]@{
        path = 'bin/Protected/ASWDEBUG.dll'
        bytes = $stableItem.Length
        sha256 = $stableHash
        pdb_published = $false
    }
    run = [ordered]@{
        id = $runId
        evidence = 'artifacts/ProtectedRelease/' + $runId
        mapping = 'artifacts/ProtectedRelease/' + $runId +
            '/obfuscation-map.xml'
    }
    next_step =
        'Upload ASWDEBUG.dll to VeriGate Admin, then export the server-generated VGCH ciphertext for Lanzou.'
}
$manifestPath = Join-Path $stableRoot 'ASWDEBUG.release.json'
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $manifestPath -Encoding UTF8

Write-Host "Protected release passed: $stableAssembly"
Write-Host "SHA-256: $stableHash"
Write-Host "Evidence: $runRoot"
Write-Output $stableAssembly
