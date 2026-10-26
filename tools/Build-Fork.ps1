[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ProfileBepInEx,
  [Parameter(Mandatory)][string]$ManagedPath,
  [switch]$SkipBootstrap
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
$refs = Join-Path $repo '.references'
New-Item -ItemType Directory -Path $refs -Force | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($reference in (Get-Content "$PSScriptRoot/reference-lock.json" -Raw | ConvertFrom-Json)) {
  $destination = Join-Path $refs $reference.file
  if (-not $SkipBootstrap -and -not (Test-Path -LiteralPath $destination)) {
    $local = Get-ChildItem -LiteralPath "$ProfileBepInEx/plugins" -Filter $reference.file -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($local -and (Get-FileHash -LiteralPath $local.FullName).Hash -eq $reference.sha256) {
      Copy-Item -LiteralPath $local.FullName -Destination $destination
    } else {
      $archive = Join-Path $refs ($reference.package.Replace('/','-') + '.zip')
      Invoke-WebRequest -Uri "https://thunderstore.io/package/download/$($reference.package)/" -OutFile $archive
      $zip = [IO.Compression.ZipFile]::OpenRead($archive)
      try {
        $entries = @($zip.Entries | Where-Object Name -eq $reference.file)
        if ($entries.Count -ne 1) { throw "Pinned package lacks unique $($reference.file)" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($entries[0], $destination, $true)
      } finally { $zip.Dispose() }
    }
  }
  if (-not (Test-Path -LiteralPath $destination) -or (Get-FileHash -LiteralPath $destination).Hash -ne $reference.sha256) { throw "Reference hash mismatch: $destination. Remove the cached file and bootstrap again." }
}
$env:DOTNET_CLI_HOME = Join-Path $refs 'dotnet'
$env:NUGET_PACKAGES = Join-Path $refs 'nuget'
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
Push-Location $repo
try {
  $commit = (& git rev-parse HEAD).Trim()
  if ($LASTEXITCODE -ne 0) { throw 'Cannot resolve fork commit' }
  & dotnet build src/ValheimRAFT/ValheimRAFT.csproj -c Release "-p:SolutionDir=$repo\" "-p:ManagedDataPath=$ManagedPath" "-p:BepInExPath=$ProfileBepInEx\core" "-p:ForkCommit=$commit" -v minimal -p:WarningLevel=0
  if ($LASTEXITCODE -ne 0) { throw 'RAFT source build failed' }
} finally { Pop-Location }
