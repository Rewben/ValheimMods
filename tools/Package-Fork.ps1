[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$ProfileBepInEx,
  [Parameter(Mandatory)][string]$ManagedPath,
  [Parameter(Mandatory)][string]$Python,
  [string]$PrivacyValuesFile
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
  $runtimeVersion = [string]$props.Project.PropertyGroup.Version
  $packageVersion = [string]$props.Project.PropertyGroup.PackageVersion
  $destination = Join-Path $repo 'dist'
  New-Item -ItemType Directory -Path $destination -Force | Out-Null
  $stage = Join-Path $repo ('.references/package-' + [guid]::NewGuid().ToString('N'))
  New-Item -ItemType Directory -Path $stage | Out-Null
  $pluginStage = Join-Path $stage 'plugins'
  New-Item -ItemType Directory -Path $pluginStage | Out-Null
  $dlls = @('ValheimRAFT.dll','ValheimVehicles.dll','ZdoWatcher.dll','DynamicLocations.dll','Zolantris.Shared.dll','ServerSync.dll')
  foreach ($name in $dlls) { Copy-Item -LiteralPath "Build/bin/ValheimRAFT/Release/$name" -Destination $pluginStage }
  & $Python "$PSScriptRoot/release_archive.py" redact-codeview "$pluginStage/ServerSync.dll" --report "$stage/../serversync-redaction-$packageVersion.json"
  if ($LASTEXITCODE -ne 0) { throw 'ServerSync debug-path redaction failed' }
  Copy-Item -LiteralPath src/ValheimRAFT/Assets -Destination $pluginStage -Recurse
  Copy-Item -LiteralPath LICENSE -Destination $stage
  Copy-Item -LiteralPath dependencies/ServerSync.LICENSE.txt -Destination $stage
  Copy-Item -LiteralPath release/README.md,release/CHANGELOG.md,release/ServerSync.PROVENANCE.md -Destination $stage
  Copy-Item -LiteralPath src/ValheimRAFT/Thunderstore/icon.png -Destination $stage
  @{ name='ValheimRAFT_Rewben'; version_number=$packageVersion; website_url='https://github.com/Rewben/ValheimMods'; description='Rewben ValheimRAFT fork: native portal synchronization repairs and RaftRebalance behavior.'; dependencies=@('ValheimModding-Jotunn-2.30.2') } | ConvertTo-Json | Set-Content "$stage/manifest.json" -Encoding utf8NoBOM
  $files = [ordered]@{}
  foreach ($file in Get-ChildItem -LiteralPath $stage -Recurse -File) {
    $files[[IO.Path]::GetRelativePath($stage,$file.FullName).Replace('\','/')] = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
  }
  @{ version=$runtimeVersion; runtimeVersion=$runtimeVersion; packageVersion=$packageVersion; upstream=('ValheimRAFT-v' + $props.Project.PropertyGroup.ForkUpstreamVersion); forkRevision=[int]$props.Project.PropertyGroup.ForkRevision; gameVersion=[string]$props.Project.PropertyGroup.ForkExpectedGameVersion; commit=$commit; validation='Runtime 5.1.102 server startup verified; multiplayer gameplay acceptance pending'; files=$files } | ConvertTo-Json -Depth 6 | Set-Content "$stage/release-metadata.json" -Encoding utf8NoBOM
  $archive = Join-Path $destination "Rewben-ValheimRAFT-$packageVersion.zip"
  if (Test-Path -LiteralPath $archive) { throw "Package already exists: $archive. Preserve it or remove that exact candidate before rebuilding." }
  [IO.Compression.ZipFile]::CreateFromDirectory($stage, $archive)
  $auditArgs = @("$PSScriptRoot/release_archive.py", 'validate', $archive, '--report', "$archive.audit.json")
  if ($PrivacyValuesFile) { $auditArgs += @('--private-values-file', $PrivacyValuesFile) }
  & $Python @auditArgs
  if ($LASTEXITCODE -ne 0) {
    # Reject the candidate by moving it to its owned staging directory; never
    # leave a failed privacy/layout check looking like an upload-ready release.
    Move-Item -LiteralPath $archive -Destination (Join-Path $stage 'rejected.zip')
    throw 'Public archive validation failed; rejected candidate retained only in .references staging.'
  }
  $hash = (Get-FileHash -LiteralPath $archive).Hash.ToLowerInvariant()
  "$hash  $([IO.Path]::GetFileName($archive))" | Set-Content "$archive.sha256" -Encoding ascii
  Copy-Item -LiteralPath "$stage/release-metadata.json" -Destination "$archive.metadata.json"
  Write-Host "Packaged $archive`nSHA256 $hash`nNo plugins were installed and Valheim was not launched."
} finally { Pop-Location }
