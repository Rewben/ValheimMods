[CmdletBinding()]
param(
  [Parameter(Mandatory)][string]$Session,
  [Parameter(Mandatory)][string]$Python,
  [Parameter(Mandatory)][string]$GaleDatabase
)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name valheim,gale -ErrorAction SilentlyContinue) { throw 'Close Valheim and Gale before restoring the profile.' }
$record = Get-Content -LiteralPath (Join-Path $Session 'maintenance.json') -Raw | ConvertFrom-Json
$profile = [IO.Path]::GetFullPath($record.profile)
$parent = [IO.Directory]::GetParent($profile).FullName
if ([IO.Path]::GetFileName($parent) -ne 'profiles' -or [IO.Path]::GetFileName([IO.Directory]::GetParent($parent).FullName) -ne 'valheim') { throw 'Expected a Gale Valheim profile path.' }
$snapshot = [IO.Path]::GetFullPath($record.backup)
if ($snapshot -ne [IO.Path]::GetFullPath((Join-Path $Session 'profile-before.zip'))) { throw 'Snapshot does not belong to this maintenance session.' }
if ((Get-FileHash -LiteralPath $snapshot).Hash -ne (Get-Content -LiteralPath "$snapshot.sha256" -Raw).Trim()) { throw 'Profile snapshot checksum mismatch.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$restore = Join-Path $parent ('.restore-' + [guid]::NewGuid().ToString('N'))
[IO.Compression.ZipFile]::ExtractToDirectory($snapshot,$restore)
$failed = Join-Path $parent ('.before-rollback-' + [guid]::NewGuid().ToString('N'))
# Paths are resolved siblings within this named Gale profiles directory. Keep the
# failed profile as another recoverable snapshot instead of recursively deleting it.
Move-Item -LiteralPath $profile -Destination $failed
try {
  Move-Item -LiteralPath $restore -Destination $profile
  & $Python "$PSScriptRoot/gale_profile.py" restore $GaleDatabase (Join-Path $Session 'profile-metadata-before.json')
  if ($LASTEXITCODE -ne 0) { throw 'Gale metadata restore failed' }
} catch {
  if (Test-Path -LiteralPath $profile) { Move-Item -LiteralPath $profile -Destination "$restore-failed" }
  Move-Item -LiteralPath $failed -Destination $profile
  throw
}
Write-Host "Restored the exact previous profile and its Gale metadata. Test profile retained at $failed"
