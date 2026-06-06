# UpdateMods.ps1
# Syncs the <contentpackages> block in config_player.xml using data from Nyblya.xml.
# Everything else in config_player.xml (graphics, sound, keybinds, etc.) is left untouched.
#
# Location: this script lives in the LocalMods folder of the Barotrauma game directory.
# Run:      powershell -NoProfile -ExecutionPolicy Bypass -File LocalMods\UpdateMods.ps1
#           (usually launched from update.bat via Steam launch options).

$ErrorActionPreference = "Stop"

# --- Paths -------------------------------------------------------------------

$scriptDir    = $PSScriptRoot                            # folder of this script (LocalMods)
$gameDir      = Split-Path -Parent $scriptDir            # Barotrauma game root (parent of LocalMods)
$nyblyaFile   = Join-Path $gameDir 'ModLists\Nyblya.xml' # source of truth - the mod list
$configFile   = Join-Path $gameDir 'config_player.xml'   # game settings file
$workshopBase = ($env:LOCALAPPDATA -replace '\\', '/') + # folder with Workshop mods
                "/Daedalic Entertainment GmbH/Barotrauma/WorkshopMods/Installed"

# --- Read --------------------------------------------------------------------

if (-not (Test-Path $nyblyaFile))  { Write-Error "Not found: $nyblyaFile";  exit 1 }
if (-not (Test-Path $configFile))  { Write-Error "Not found: $configFile";  exit 1 }

[xml]$nyblya   = Get-Content -LiteralPath $nyblyaFile -Encoding UTF8
$configText    = [System.IO.File]::ReadAllText($configFile)

# config_player.xml stores enabled packages in one of two forms:
#   1) a full "<contentpackages>...</contentpackages>" block  (when any mods are configured), or
#   2) a single self-closing "<core path=... />" element       (fresh config / vanilla only, no mods).
$packagesRegex = '(?s)<contentpackages>.*?</contentpackages>'
$coreRegex     = '<core\b[^>]*?/>'

if ($configText -notmatch $packagesRegex -and $configText -notmatch $coreRegex) {
    Write-Error "Neither <contentpackages> block nor <core .../> element found in $configFile"
    exit 1
}

# --- Build the new <contentpackages> block -----------------------------------

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('  <contentpackages>')
$lines.Add('    <!--Vanilla-->')
$lines.Add('    <corepackage')
$lines.Add('      path="Content/ContentPackages/Vanilla.xml" />')
$lines.Add('    <regularpackages>')

$count = 0

foreach ($mod in $nyblya.mods.ChildNodes) {

    $tag = $mod.LocalName
    if ($tag -in '#comment', 'Vanilla') { continue }   # Vanilla is the corepackage, skip it

    $modName = $mod.GetAttribute('name')

    $path = switch ($tag) {
        'Local'    { "LocalMods/$modName/filelist.xml" }
        'Workshop' { "$workshopBase/$($mod.GetAttribute('id'))/filelist.xml" }
        default    { $null }
    }
    if ($null -eq $path) { continue }

    $lines.Add("      <!--$modName-->")
    $lines.Add('      <package')
    $lines.Add("        path=""$path"" />")
    $count++
}

$lines.Add('    </regularpackages>')
$lines.Add('  </contentpackages>')

$newBlock = $lines -join "`r`n"

# --- Replace only the <contentpackages> block --------------------------------

if ($configText -match $packagesRegex) {
    # existing full block -> replace it
    $newConfigText = $configText -replace $packagesRegex, $newBlock
}
else {
    # fresh config with only "<core .../>" -> swap that single element for the full block
    $newConfigText = $configText -replace $coreRegex, $newBlock
}

# --- Save (UTF-8 with BOM, same as the original file) ------------------------

[System.IO.File]::WriteAllText(
    $configFile,
    $newConfigText,
    [System.Text.UTF8Encoding]::new($true)   # $true = with BOM
)

Write-Host "OK: contentpackages updated ($count packages)." -ForegroundColor Green