# ApplyLuaCsPatch.ps1
# Applies a FIXED LuaCsForBarotrauma client patch from LocalMods\patch.zip directly into the game
# folder, replicating Luatrauma.AutoUpdater's file-replacement logic WITHOUT the AutoUpdater exe or
# any internet download. This makes the LuaCs install deterministic for everyone (the exact bytes
# committed to the repo), instead of relying on the AutoUpdater succeeding on each machine.
#
# MAINTAINER: when LuaCs is updated for a new game version, replace LocalMods\patch.zip with the new
#             luacsforbarotrauma_patch_windows_client.zip and commit it.
#
# Run: powershell -NoProfile -ExecutionPolicy Bypass -File LocalMods\ApplyLuaCsPatch.ps1
#      (launched from UpdateMods.bat via the Steam launch options, BEFORE the game starts).

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot                       # LocalMods
$gameDir   = Split-Path -Parent $scriptDir       # Barotrauma game root (parent of LocalMods)
$patchZip  = Join-Path $scriptDir 'patch.zip'
$tempRoot  = Join-Path $gameDir 'LuaCsPatch.Temp'
$extract   = Join-Path $tempRoot 'Extracted'

function Fail($msg) {
    Write-Host ""
    Write-Host "LuaCs patch NOT applied: $msg" -ForegroundColor Red
    exit 1
}

if (-not (Test-Path -LiteralPath $patchZip)) {
    Fail "patch.zip not found at `"$patchZip`". Put the LuaCs client patch zip there (and commit it)."
}

# --- Extract patch.zip into a clean temp folder ------------------------------
try {
    if (Test-Path -LiteralPath $tempRoot) { Remove-Item -LiteralPath $tempRoot -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $extract | Out-Null
    Expand-Archive -LiteralPath $patchZip -DestinationPath $extract -Force
} catch {
    Fail "could not extract patch.zip ($($_.Exception.Message))."
}

# --- Find the patch root (files at zip root, or inside a single wrapper folder)
$root = $extract
if (-not (Test-Path -LiteralPath (Join-Path $root 'Barotrauma.dll'))) {
    $subDirs = @(Get-ChildItem -LiteralPath $root -Directory)
    if ($subDirs.Count -eq 1 -and (Test-Path -LiteralPath (Join-Path $subDirs[0].FullName 'Barotrauma.dll'))) {
        $root = $subDirs[0].FullName
    }
}

$patchDll = Join-Path $root 'Barotrauma.dll'
$gameDll  = Join-Path $gameDir 'Barotrauma.dll'
if (-not (Test-Path -LiteralPath $patchDll)) {
    Fail "Barotrauma.dll not found inside patch.zip - is it the LuaCs *client* patch zip?"
}
if (-not (Test-Path -LiteralPath $gameDll)) {
    Fail "current Barotrauma.dll not found in `"$gameDir`"."
}

# --- Version safety check (same as AutoUpdater): patch must match the installed game version ---
$gameVer  = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($gameDll).FileVersion
# $patchVer = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($patchDll).FileVersion
# if ($gameVer -ne $patchVer) {
#    Fail "patch is for game version $patchVer but your game is $gameVer. The game probably updated on Steam - update LocalMods\patch.zip to the matching LuaCs version (or roll the game back). The game will launch WITHOUT LuaCs until then."
# }

# --- Copy every patch file into the game folder, overwriting (create dirs as needed) ---
try {
    $rootFull = (Resolve-Path -LiteralPath $root).Path.TrimEnd('\')
    Get-ChildItem -LiteralPath $root -Recurse -File | ForEach-Object {
        $rel     = $_.FullName.Substring($rootFull.Length).TrimStart('\', '/')
        $dest    = Join-Path $gameDir $rel
        $destDir = Split-Path -Parent $dest
        if (-not (Test-Path -LiteralPath $destDir)) { New-Item -ItemType Directory -Force -Path $destDir | Out-Null }
        Copy-Item -LiteralPath $_.FullName -Destination $dest -Force
    }
} catch {
    Fail "failed to copy patch files ($($_.Exception.Message)). Make sure the GAME IS FULLY CLOSED, and run as administrator if the game is in Program Files."
}

# --- Workshop interference cleanup (same as AutoUpdater) ---
$luacsVer = Join-Path $gameDir 'luacsversion.txt'
if (Test-Path -LiteralPath $luacsVer) { Remove-Item -LiteralPath $luacsVer -Force }

# --- Clean up temp ---
try { Remove-Item -LiteralPath $tempRoot -Recurse -Force } catch { }

Write-Host "OK: LuaCs patch applied (game version $gameVer)." -ForegroundColor Green
exit 0
