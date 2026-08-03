# Сборка готового к запуску MeetMemo в папку dist\.
#
#   .\publish.ps1                 — автономная сборка (не требует установленного .NET)
#   .\publish.ps1 -Portable       — лёгкая сборка (нужен .NET 8 Desktop Runtime)
#
# Автономная весит больше, зато запускается на любой Windows 11 без предварительной установки —
# это важно для тех, кто скачает приложение с GitHub.

param(
    [switch]$Portable
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# SDK установлен в профиль пользователя: системный dotnet.exe содержит только рантайм.
$sdk = "$env:LOCALAPPDATA\dotnet-sdk\dotnet.exe"
if (-not (Test-Path $sdk)) { $sdk = "dotnet" }

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'

$outDir = Join-Path $root "dist\MeetMemo"
$project = Join-Path $root "src\MeetMemo.App\MeetMemo.App.csproj"

# Работающее приложение держит свой exe и не даст его перезаписать.
Get-Process -Name MeetMemo -ErrorAction SilentlyContinue | ForEach-Object {
    Write-Host "Останавливаю запущенный MeetMemo (PID $($_.Id))..."
    $_ | Stop-Process -Force
    Start-Sleep -Seconds 2
}

if (Test-Path $outDir) { Remove-Item -Recurse -Force $outDir }
New-Item -ItemType Directory -Force $outDir | Out-Null

$args = @(
    'publish', $project,
    '-c', 'Release',
    '-r', 'win-x64',
    '-o', $outDir,
    '--nologo'
)

if ($Portable) {
    Write-Host "Сборка: лёгкая (нужен .NET 8 Desktop Runtime)" -ForegroundColor Cyan
    $args += @('--self-contained', 'false')
} else {
    Write-Host "Сборка: автономная (.NET внутри)" -ForegroundColor Cyan
    $args += @('--self-contained', 'true', '-p:PublishSingleFile=false')
}

& $sdk @args
if ($LASTEXITCODE -ne 0) { throw "Сборка завершилась с ошибкой" }

# Ярлык рядом с приложением: запускать exe из глубины папки неудобно.
$exePath = Join-Path $outDir "MeetMemo.exe"
$shortcut = Join-Path $root "dist\MeetMemo.lnk"
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($shortcut)
$link.TargetPath = $exePath
$link.WorkingDirectory = $outDir
$link.IconLocation = "$exePath,0"
$link.Description = "MeetMemo — локальная стенография встреч"
$link.Save()

$size = (Get-ChildItem -Recurse $outDir | Measure-Object -Property Length -Sum).Sum / 1MB

Write-Host ""
Write-Host "Готово." -ForegroundColor Green
Write-Host "  Приложение: $exePath"
Write-Host "  Ярлык:      $shortcut"
Write-Host "  Размер:     $([math]::Round($size,1)) МБ"
Write-Host ""
Write-Host "Запуск: двойной щелчок по dist\MeetMemo.lnk или по MeetMemo.exe"
