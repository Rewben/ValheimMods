[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Package,
  [Parameter(Mandatory)][string]$Profile,
  [Parameter(Mandatory)][string]$Python,
  [Parameter(Mandatory)][string]$GaleDatabase
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name valheim -ErrorAction SilentlyContinue) { throw 'Quit Valheim before preparing the profile snapshot.' }
$repo = Split-Path $PSScriptRoot -Parent
$profilePath = (Resolve-Path -LiteralPath $Profile).Path
$archive = (Resolve-Path -LiteralPath $Package).Path
$metadata = Get-Content -LiteralPath "$archive.metadata.json" -Raw | ConvertFrom-Json
$expected = ((Get-Content -LiteralPath "$archive.sha256" -Raw).Trim() -split '\s+')[0]
if ((Get-FileHash -LiteralPath $archive).Hash -ne $expected) { throw 'Release checksum mismatch' }
$session = Join-Path $repo ('.references/maintenance-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ'))
New-Item -ItemType Directory -Path $session | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
# ZIP reads file contents through Gale's links; the snapshot remains usable if the cache changes.
$snapshot = Join-Path $session 'profile-before.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($profilePath, $snapshot)
$zip = [IO.Compression.ZipFile]::OpenRead($snapshot)
try { foreach ($entry in $zip.Entries) { $stream=$entry.Open(); try { $stream.CopyTo([IO.Stream]::Null) } finally { $stream.Dispose() } } } finally { $zip.Dispose() }
(Get-FileHash -LiteralPath $snapshot).Hash | Set-Content "$snapshot.sha256" -Encoding ascii
& $Python "$PSScriptRoot/gale_profile.py" backup $GaleDatabase ([IO.Path]::GetFileName($profilePath)) "$session/profile-metadata-before.json"
if ($LASTEXITCODE -ne 0) { throw 'Gale profile metadata snapshot failed; active profile was not changed.' }
$stage = Join-Path $session 'BepInEx'
New-Item -ItemType Directory -Path $stage | Out-Null
foreach ($name in @('plugins','patchers','config')) {
  $source = Join-Path $profilePath "BepInEx/$name"
  $target = Join-Path $stage $name
  New-Item -ItemType Directory -Path $target | Out-Null
  if (Test-Path -LiteralPath $source) { Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse -Force }
}
$plugins = Join-Path $stage 'plugins'
$old = @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter ValheimRAFT.dll)
if ($old.Count -ne 1) { throw 'Expected one installed upstream RAFT plugin in the staged profile.' }
$oldFolder = $old[0].Directory.FullName
$newFolder = Join-Path $plugins 'ValheimRAFT_Rewben'
[IO.Compression.ZipFile]::ExtractToDirectory($archive,$newFolder)
if (Test-Path -LiteralPath "$oldFolder/Assets") {
  Get-ChildItem -LiteralPath "$oldFolder/Assets" -Force | Copy-Item -Destination "$newFolder/Assets" -Recurse -Force
}
foreach ($path in @($oldFolder) + @(Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter RaftRebalance.dll | ForEach-Object { $_.Directory.FullName } | Select-Object -Unique)) {
  $resolved = [IO.Path]::GetFullPath($path)
  if (-not $resolved.StartsWith($plugins + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or $resolved -eq $newFolder) { throw 'Unsafe staging cleanup target' }
  Remove-Item -LiteralPath $resolved -Recurse -Force
}
foreach ($property in $metadata.files.PSObject.Properties | Where-Object Name -Like '*.dll') {
  if ((Get-FileHash -LiteralPath (Join-Path $newFolder $property.Name)).Hash -ne $property.Value) { throw "Staged fork DLL mismatch: $($property.Name)" }
}
if (@(Get-ChildItem -LiteralPath $plugins -Recurse -Filter ValheimRAFT.dll).Count -ne 1 -or (Get-ChildItem -LiteralPath $plugins -Recurse -Filter RaftRebalance.dll)) { throw 'Staged plugin identity failure' }
$inventory = Get-ChildItem -LiteralPath $plugins -Recurse -File -Filter '*.dll' | ForEach-Object {
  [ordered]@{ path=[IO.Path]::GetRelativePath($plugins,$_.FullName); sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant() }
}
$inventory | ConvertTo-Json | Set-Content "$session/staged-plugins.json" -Encoding utf8NoBOM
@{ session=$session; profile=$profilePath; stagedBepInEx=$stage; package=$archive; version=$metadata.version; commit=$metadata.commit; backup=$snapshot } | ConvertTo-Json | Set-Content "$session/maintenance.json" -Encoding utf8NoBOM
Write-Host "Verified profile snapshot and staged plugin inventory: $session`nThe active profile and server have not changed."
