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

# Ищем компилятор: системный dotnet.exe нередко содержит только рантайм, поэтому мало
# найти сам файл — нужно убедиться, что у него есть хотя бы один SDK.
function Find-DotnetSdk {
    foreach ($candidate in @(
        "$env:LOCALAPPDATA\dotnet-sdk\dotnet.exe",
        "$env:ProgramFiles\dotnet\dotnet.exe",
        "dotnet"
    )) {
        try {
            $sdks = & $candidate --list-sdks 2>$null
            if ($LASTEXITCODE -eq 0 -and @($sdks).Count -gt 0) { return $candidate }
        } catch { }
    }
    return $null
}

$sdk = Find-DotnetSdk

# Без SDK «dotnet publish» падает малопонятной ошибкой. Ставим его сами — но спрашиваем:
# устанавливать программы на чужой компьютер молча нельзя, даже полезные.
if (-not $sdk) {
    Write-Host ""
    Write-Host "Не найден .NET 8 SDK — это компилятор, без него собрать приложение нечем." -ForegroundColor Yellow
    Write-Host "Размер около 200 МБ, ставится один раз, права администратора не нужны."
    Write-Host ""

    $winget = Get-Command winget -ErrorAction SilentlyContinue

    if (-not $winget) {
        Write-Host "Не найден и winget, поэтому поставить автоматически не получится." -ForegroundColor Yellow
        Write-Host ""
        Write-Host "  Скачайте .NET 8 SDK вручную: https://dotnet.microsoft.com/download/dotnet/8.0"
        Write-Host "  (нужен именно SDK, а не Runtime — Runtime умеет только запускать готовое)"
        Write-Host ""
        Write-Host "После установки запустите publish.cmd снова."
        exit 1
    }

    $answer = Read-Host "Установить .NET 8 SDK сейчас? [Y/n]"
    if ($answer -and $answer -notmatch '^(y|yes|д|да)$') {
        Write-Host ""
        Write-Host "Хорошо. Поставить вручную: https://dotnet.microsoft.com/download/dotnet/8.0"
        exit 1
    }

    Write-Host ""
    Write-Host "Устанавливаю .NET 8 SDK..." -ForegroundColor Cyan
    winget install Microsoft.DotNet.SDK.8 --accept-source-agreements --accept-package-agreements

    # Установщик правит PATH, но уже запущенный процесс о нём не узнает: перечитываем сами,
    # иначе пришлось бы просить пользователя закрыть окно и начать заново.
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' +
                [Environment]::GetEnvironmentVariable('Path', 'User')

    $sdk = Find-DotnetSdk

    if (-not $sdk) {
        Write-Host ""
        Write-Host "SDK установлен, но в этом окне ещё не виден." -ForegroundColor Yellow
        Write-Host "Закройте окно и запустите publish.cmd снова — этого достаточно."
        exit 1
    }

    Write-Host ".NET 8 SDK установлен." -ForegroundColor Green
    Write-Host ""
}

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
