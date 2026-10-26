[CmdletBinding()]
param(
    [string]$BepInExPath,
    [string]$ConfigOverridesPath,
    [switch]$ReplaceConfigs,
    [Parameter(Mandatory)][string]$Server,
    [string]$User = 'root',
    [Parameter(Mandatory)][string]$Container,
    [Parameter(Mandatory)][string]$ServerRoot,
    [string]$ReleaseLabel = 'Rewben RAFT',
    [string]$RollbackDeployment,
    [switch]$FullRollback,
    [string]$ForkMetadataPath,
    [switch]$PortalDiagnostics,
    [switch]$Preview
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($ConfigOverridesPath -and -not $ReplaceConfigs) {
    throw 'Config overrides require -ReplaceConfigs. Omit overrides to preserve existing server settings.'
}
if ($RollbackDeployment -and $ReplaceConfigs) { throw '-ReplaceConfigs is not used with rollback.' }

function Invoke-Native([string]$Program, [string[]]$Arguments) {
    & $Program @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$Program failed (exit $LASTEXITCODE)." }
}
function Invoke-BinaryTransfer([string]$Program, [string[]]$Arguments, [string]$InputFile) {
    # Raw bytes avoid PowerShell text-pipeline corruption of compressed archives.
    # stdout/stderr stay attached to the console, including SSH's password prompt.
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = (Get-Command $Program -ErrorAction Stop).Source
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardInput = $true
    foreach ($argument in $Arguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $inputStream = $null
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw "Unable to start $Program." }
        $inputStream = [IO.File]::OpenRead($InputFile)
        $inputStream.CopyTo($process.StandardInput.BaseStream)
        $process.StandardInput.Close()
        $process.WaitForExit()
        if ($process.ExitCode -ne 0) { throw "$Program failed (exit $($process.ExitCode))." }
    } finally {
        if ($inputStream) { $inputStream.Dispose() }
        if ($started -and -not $process.HasExited) { $process.Kill($true); $process.WaitForExit() }
        $process.Dispose()
    }
}
if ($Server -notmatch '^[a-zA-Z0-9.-]+$' -or $User -notmatch '^[a-zA-Z0-9_-]+$' -or
    $Container -notmatch '^[a-zA-Z0-9_-]+$' -or $ServerRoot -notmatch '^/srv/games/valheim/[a-zA-Z0-9_-]+$') {
    throw 'Invalid SSH target, container name, or server root.'
}
if ($RollbackDeployment -and $RollbackDeployment -notmatch '^[0-9]{8}T[0-9]{6}Z-[a-f0-9]{8}$') {
    throw 'Invalid rollback deployment ID.'
}
foreach ($command in @('tar','ssh')) { Get-Command $command -ErrorAction Stop | Out-Null }
$target = "${User}@${Server}"
$id = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ') + '-' + [guid]::NewGuid().ToString('N').Substring(0,8)
$remote = "/tmp/relicheim-$id"
$temp = Join-Path ([IO.Path]::GetTempPath()) "relicheim-$id"
New-Item -ItemType Directory -Path $temp | Out-Null
try {
    if (-not $RollbackDeployment) {
        if (-not $BepInExPath) { throw 'Supply -BepInExPath pointing to the prepared Gale profile BepInEx folder.' }
        $source = (Resolve-Path -LiteralPath $BepInExPath).Path
        $payload = Join-Path $temp 'payload'
        New-Item -ItemType Directory -Path $payload | Out-Null
        foreach ($folder in @('plugins','patchers','config')) {
            $destination = Join-Path $payload $folder
            New-Item -ItemType Directory -Path $destination | Out-Null
            $origin = Join-Path $source $folder
            if ($folder -ne 'patchers' -and -not (Test-Path -LiteralPath $origin -PathType Container)) {
                throw "Missing $origin. Launch the prepared Gale profile once, then quit."
            }
            if (Test-Path -LiteralPath $origin) {
                $entries = @(Get-ChildItem -LiteralPath $origin -Force -Recurse)
                if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
                    throw "Links/reparse points are not supported in $origin."
                }
                Get-ChildItem -LiteralPath $origin -Force | Copy-Item -Destination $destination -Recurse -Force
            }
        }
        if ($ConfigOverridesPath) {
            $overrides = (Resolve-Path -LiteralPath $ConfigOverridesPath).Path
            if (@(Get-ChildItem -LiteralPath $overrides -Force -Recurse | Where-Object {
                $_.Attributes -band [IO.FileAttributes]::ReparsePoint
            }).Count) { throw 'Config overrides cannot contain links.' }
            Get-ChildItem -LiteralPath $overrides -Force | Copy-Item -Destination (Join-Path $payload 'config') -Recurse -Force
        }
        $dlls = @(Get-ChildItem -LiteralPath (Join-Path $payload 'plugins') -Recurse -Filter '*.dll')
        if (-not $dlls.Count) { throw 'No plugin DLLs found.' }
        $files = @{}
        foreach ($file in Get-ChildItem -LiteralPath $payload -Force -Recurse -File) {
            $relative = [IO.Path]::GetRelativePath($payload, $file.FullName).Replace('\','/')
            if ($relative -match '(^|/)\.\.?(/|$)' -or $relative.Contains(':')) { throw "Invalid path: $relative" }
            $files[$relative] = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
        $configMode = if ($ReplaceConfigs) { 'replace' } else { 'preserve' }
        $manifest = @{ deployment = $id; label = $ReleaseLabel; files = $files; configMode = $configMode }
        if ($ForkMetadataPath) { $metadata = Get-Content -LiteralPath $ForkMetadataPath -Raw | ConvertFrom-Json; $manifest.fork = @{version=$metadata.version;commit=$metadata.commit;gameVersion=$metadata.gameVersion;diagnostics=[bool]$PortalDiagnostics} }
        $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $payload 'manifest.json') -Encoding utf8NoBOM
        Write-Host "Prepared $($files.Count) files, $($dlls.Count) plugin DLLs."
        Write-Host "Source profile: $source"
        Write-Host "Target: $target / $Container / $ServerRoot"
        Write-Host 'This mirrors server plugins and patchers, removing mods absent from the profile.'
        if ($ReplaceConfigs) {
            Write-Host 'Config mode: REPLACE with local profile plus overrides.'
        } else {
            Write-Host 'Config mode: PRESERVE existing server files; copy only missing local config files.'
        }
        if ($Preview) { $manifest | ConvertTo-Json -Depth 5; return }
        $archive = Join-Path $temp 'payload.tar.gz'
        Invoke-Native 'tar' @('-czf', $archive, '-C', $payload, '.')
    } elseif ($Preview) {
        Write-Host "Would restore deployment $RollbackDeployment on $target ($Container). Full world rollback: $FullRollback"
        return
    }
    $transport = Join-Path $temp 'transport'
    New-Item -ItemType Directory -Path $transport | Out-Null
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'deploy_remote.py') -Destination $transport
    if ($RollbackDeployment) {
        $rollbackMode = if ($FullRollback) { 'rollback-full' } else { 'rollback' }
        $deployCommand = "python3 '$remote/deploy_remote.py' $rollbackMode '$ServerRoot' '$Container' '$RollbackDeployment'"
    } else {
        Copy-Item -LiteralPath $archive -Destination $transport
        $deployCommand = "python3 '$remote/deploy_remote.py' deploy '$ServerRoot' '$Container' '$id' '$remote/payload.tar.gz'"
    }
    $transferArchive = Join-Path $temp 'transfer.tar.gz'
    Invoke-Native 'tar' @('-czf', $transferArchive, '-C', $transport, '.')
    # Upload, deployment and cleanup share ONE SSH session. No password is captured.
    # Deploy only after tar confirms the upload is complete; cleanup preserves exit status.
    $remoteCommand = @'
set -eu
umask 077
mkdir '__REMOTE__'
trap 'rm -f "__REMOTE__/deploy_remote.py" "__REMOTE__/payload.tar.gz"; rmdir "__REMOTE__" 2>/dev/null || true' EXIT
tar -xzf - -C '__REMOTE__'
__DEPLOY__
'@
    $remoteCommand = $remoteCommand.Replace('__REMOTE__', $remote).Replace('__DEPLOY__', $deployCommand).Replace("`r`n", "`n")
    Write-Host 'Connecting once. Enter your SSH password when prompted; upload and deployment follow automatically.'
    Invoke-BinaryTransfer 'ssh' @('-T', '-o', 'NumberOfPasswordPrompts=1', $target, $remoteCommand) $transferArchive
} finally {
    # The local target is generated under TEMP and checked before recursive deletion.
    $resolvedTemp = [IO.Path]::GetFullPath($temp)
    $tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    if ($resolvedTemp.StartsWith($tempRoot, [StringComparison]::OrdinalIgnoreCase) -and
        [IO.Path]::GetFileName($resolvedTemp) -eq "relicheim-$id") {
        Remove-Item -LiteralPath $resolvedTemp -Recurse -Force
    }
}

