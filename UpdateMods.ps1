# UpdateMods.ps1
# Синхронизирует <contentpackages> в config_player.xml по данным из Nyblya.xml.
# Всё остальное в config_player.xml (графика, звук, клавиши и т.д.) НЕ трогается.
#
# Размещение: папка игры Barotrauma (рядом с config_player.xml).
# Запуск:     powershell -ExecutionPolicy Bypass -File UpdateMods.ps1
#             или через UpdateMods.bat (см. инструкцию).

$ErrorActionPreference = "Stop"

# ─── Пути ─────────────────────────────────────────────────────────────────────

$gameDir      = $PSScriptRoot                             # папка, где лежит этот скрипт
$nyblyaFile   = "$gameDir\ModLists\Nyblya.xml"            # источник истины — список модов
$configFile   = "$gameDir\config_player.xml"              # файл настроек игры
$workshopBase = ($env:LOCALAPPDATA -replace '\\', '/') +  # папка с Workshop-модами
                "/Daedalic Entertainment GmbH/Barotrauma/WorkshopMods/Installed"

# ─── Чтение ───────────────────────────────────────────────────────────────────

if (-not (Test-Path $nyblyaFile))  { Write-Error "Не найден: $nyblyaFile";  exit 1 }
if (-not (Test-Path $configFile))  { Write-Error "Не найден: $configFile";  exit 1 }

[xml]$nyblya   = Get-Content -LiteralPath $nyblyaFile -Encoding UTF8
$configText    = [System.IO.File]::ReadAllText($configFile)

if ($configText -notmatch '(?s)<contentpackages>.*?</contentpackages>') {
    Write-Error "Блок <contentpackages> не найден в $configFile"
    exit 1
}

# ─── Сборка нового блока <contentpackages> ────────────────────────────────────

$lines = [System.Collections.Generic.List[string]]::new()
$lines.Add('  <contentpackages>')
$lines.Add('    <!--Vanilla-->')
$lines.Add('    <corepackage')
$lines.Add('      path="Content/ContentPackages/Vanilla.xml" />')
$lines.Add('    <regularpackages>')

$count = 0

foreach ($mod in $nyblya.mods.ChildNodes) {

    $tag = $mod.LocalName
    if ($tag -in '#comment', 'Vanilla') { continue }   # Vanilla — это corepackage, пропускаем

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

# ─── Замена только блока <contentpackages> ────────────────────────────────────

$newConfigText = $configText -replace '(?s)<contentpackages>.*?</contentpackages>', $newBlock

# ─── Сохранение (UTF-8 с BOM — как оригинальный файл) ────────────────────────

[System.IO.File]::WriteAllText(
    $configFile,
    $newConfigText,
    [System.Text.UTF8Encoding]::new($true)   # $true = с BOM
)

Write-Host "OK: contentpackages updated ($count packages)." -ForegroundColor Green
