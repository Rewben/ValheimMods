[CmdletBinding()]
param(
  [string]$ProfileBepInEx = 'C:\Users\dillo\AppData\Roaming\com.kesomannen.gale\valheim\profiles\VeryHard-Ripfaceoff\BepInEx',
  [string]$ManagedPath = 'E:\SteamLibrary\steamapps\common\Valheim\valheim_Data\Managed',
  [switch]$SkipBootstrap
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$refs = Join-Path $repo '.references'
New-Item -ItemType Directory -Path $refs -Force | Out-Null
if (-not $SkipBootstrap) {
  foreach ($pair in @(
    @('ValheimModding-Jotunn\Jotunn.dll','Jotunn.dll'),
    @('MathiasDecrock-PlanBuild\PlanBuild\PlanBuild.dll','PlanBuild.dll'),
    @('Advize-PlantEasily\Advize_PlantEasily.dll','Advize_PlantEasily.dll')
  )) {
    Copy-Item -LiteralPath (Join-Path "$ProfileBepInEx\plugins" $pair[0]) -Destination (Join-Path $refs $pair[1])
  }
  if (-not (Test-Path -LiteralPath "$refs\ComfyGizmo.dll")) {
    Invoke-WebRequest -Uri 'https://thunderstore.io/package/download/ComfyMods/Gizmo/1.16.0/' -OutFile "$refs\Gizmo-1.16.0.zip"
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead("$refs\Gizmo-1.16.0.zip")
    try {
      $entry = $zip.Entries | Where-Object Name -eq 'ComfyGizmo.dll' | Select-Object -First 1
      if (-not $entry) { throw 'Pinned Gizmo package lacks ComfyGizmo.dll' }
      [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, "$refs\ComfyGizmo.dll", $true)
    } finally { $zip.Dispose() }
  }
}
$env:DOTNET_CLI_HOME = Join-Path $refs 'dotnet'
$env:NUGET_PACKAGES = Join-Path $refs 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $repo
try {
  & dotnet build src/ValheimRAFT/ValheimRAFT.csproj -c Release "-p:SolutionDir=$repo\" "-p:ManagedDataPath=$ManagedPath" "-p:BepInExPath=$ProfileBepInEx\core" -v minimal -p:WarningLevel=0
  if ($LASTEXITCODE -ne 0) { throw 'RAFT source build failed' }
} finally { Pop-Location }

