[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Session,
  [Parameter(Mandatory)][string]$Python,
  [Parameter(Mandatory)][string]$GaleDatabase
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name valheim,gale -ErrorAction SilentlyContinue) { throw 'Close Valheim and Gale before installing the fork.' }
$record = Get-Content -LiteralPath (Join-Path $Session 'maintenance.json') -Raw | ConvertFrom-Json
$profile = [IO.Path]::GetFullPath($record.profile)
$stage = [IO.Path]::GetFullPath((Join-Path $Session 'BepInEx/plugins'))
if ([IO.Path]::GetFileName([IO.Directory]::GetParent($profile).FullName) -ne 'profiles') { throw 'Expected a Gale profile path.' }
if ($stage -ne [IO.Path]::GetFullPath((Join-Path $record.stagedBepInEx 'plugins'))) { throw 'Staged inventory identity mismatch.' }
$expected = @(Get-Content -LiteralPath (Join-Path $Session 'staged-plugins.json') -Raw | ConvertFrom-Json)
foreach ($file in $expected) {
  $path=[IO.Path]::GetFullPath((Join-Path $stage $file.path))
  if (-not $path.StartsWith($stage + [IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe inventory path.' }
  if ((Get-FileHash -LiteralPath $path).Hash -ne $file.sha256) { throw "Staged DLL changed: $path" }
}
if (@(Get-ChildItem -LiteralPath $stage -Recurse -Filter '*.dll').Count -ne $expected.Count) { throw 'Staged DLL inventory changed.' }
$bep = Join-Path $profile 'BepInEx'
$active = [IO.Path]::GetFullPath((Join-Path $bep 'plugins'))
$pending = Join-Path $bep ('plugins.pending-' + [guid]::NewGuid().ToString('N'))
$before = Join-Path $bep ('plugins.before-rewben-' + [guid]::NewGuid().ToString('N'))
# Resolved sibling targets stay inside the explicitly selected profile/BepInEx.
# Copies finish before the active directory is renamed. Old DLLs are outside plugins.
Copy-Item -LiteralPath $stage -Destination $pending -Recurse
Move-Item -LiteralPath $active -Destination $before
try {
  Move-Item -LiteralPath $pending -Destination $active
  & $Python "$PSScriptRoot/gale_profile.py" install $GaleDatabase (Join-Path $Session 'profile-metadata-before.json') $record.package ($record.package + '.metadata.json')
  if ($LASTEXITCODE -ne 0) { throw 'Gale profile registration failed' }
} catch {
  if (Test-Path -LiteralPath $active) { Move-Item -LiteralPath $active -Destination "$pending-failed" }
  Move-Item -LiteralPath $before -Destination $active
  throw
}
@{ previousPlugins=$before; installedVersion=$record.version; installedCommit=$record.commit } | ConvertTo-Json | Set-Content (Join-Path $Session 'client-installed.json') -Encoding utf8NoBOM
Write-Host "Installed fork $($record.version) and replaced upstream/companion Gale entries. Previous plugins retained at $before"
