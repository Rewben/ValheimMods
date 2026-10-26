[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ProfileBepInEx,
  [Parameter(Mandatory)][string]$ManagedPath,
  [Parameter(Mandatory)][string]$Python
)
$ErrorActionPreference = 'Stop'
$repo = Split-Path $PSScriptRoot -Parent
Push-Location $repo
try {
  if (& git status --porcelain --untracked-files=normal) { throw 'Commit the source before packaging so the embedded commit identifies the complete release.' }
  & dotnet test tests/Fork/Fork.Tests.csproj -c Release --logger 'trx;LogFileName=regression.trx'
  if ($LASTEXITCODE -ne 0) { throw 'Regression checks failed' }
  & $Python -m unittest discover -s tests/Fork -p 'test_*.py' -v
  if ($LASTEXITCODE -ne 0) { throw 'Deployment regression checks failed' }
  & "$PSScriptRoot/Build-Fork.ps1" -ProfileBepInEx $ProfileBepInEx -ManagedPath $ManagedPath
  $commit = (& git rev-parse HEAD).Trim()
  [xml]$props = Get-Content build/valheimraft_version.props
  $version = [string]$props.Project.PropertyGroup.Version
  $destination = Join-Path $repo 'dist'
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  $stage = Join-Path $repo ('.references/package-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $stage | Out-Null
  $dlls = @('ValheimRAFT.dll','ValheimVehicles.dll','ZdoWatcher.dll','DynamicLocations.dll','Zolantris.Shared.dll','ServerSync.dll')
  foreach ($name in $dlls) { Copy-Item -LiteralPath "Build/bin/ValheimRAFT/Release/$name" -Destination $stage }
  Copy-Item -LiteralPath src/ValheimRAFT/Assets -Destination $stage -Recurse
  Copy-Item -LiteralPath LICENSE -Destination $stage
  Copy-Item -LiteralPath dependencies/ServerSync.LICENSE.txt -Destination $stage
  Copy-Item -LiteralPath FORK.md -Destination "$stage/README.md"
  Copy-Item -LiteralPath src/ValheimRAFT/Thunderstore/icon.png -Destination $stage
  @{ name='ValheimRAFT_Rewben'; version_number=$version; website_url='https://github.com/Rewben/ValheimMods'; description='Rewben ValheimRAFT fork: native portal synchronization repairs and RaftRebalance behavior.'; dependencies=@('ValheimModding-Jotunn-2.30.2') } | ConvertTo-Json | Set-Content "$stage/manifest.json" -Encoding utf8NoBOM
  $files = [ordered]@{}
  foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    $files[[IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
  }
  @{ version=$version; upstream=('ValheimRAFT-v' + $props.Project.PropertyGroup.ForkUpstreamVersion); forkRevision=[int]$props.Project.PropertyGroup.ForkRevision; gameVersion=[string]$props.Project.PropertyGroup.ForkExpectedGameVersion; commit=$commit; gameReference=(Get-FileHash -LiteralPath "$ManagedPath/assembly_valheim.dll").Hash.ToLowerInvariant(); validation='Headless regressions passed; main-server validation pending'; files=$files } | ConvertTo-Json -Depth 6 | Set-Content "$stage/release-metadata.json" -Encoding utf8NoBOM
  $archive = Join-Path $destination "Rewben-ValheimRAFT-$version.zip"
  if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive. Preserve it or remove that exact candidate before rebuilding." }
  [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
  $zip = [IO.Compression.ZipFile]::OpenRead($archive)
  try {
    $actualDlls = @($zip.Entries | Where-Object FullName -Like '*.dll' | ForEach-Object FullName | Sort-Object)
    if (Compare-Object ($dlls | Sort-Object) $actualDlls) { throw 'Package DLL allowlist mismatch' }
    foreach ($entry in $zip.Entries) { $stream=$entry.Open(); try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() } }
  } finally { $zip.Dispose() }
  $hash = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
  "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
  Copy-Item -LiteralPath "$stage/release-metadata.json" -Destination "$archive.metadata.json"
  Write-Host "Packaged $archive`nSHA256 $hash`nNo plugins were installed and Valheim was not launched."
} finally { Pop-Location }
