# Собирает иконки приложения из ico.png.
#
# Результат:
#   src/MeetMemo.App/Assets/app.ico            — иконка приложения (многоразмерная)
#   src/MeetMemo.App/Assets/tray-idle.ico      — значок в трее: готов
#   src/MeetMemo.App/Assets/tray-recording.ico — идёт запись
#   src/MeetMemo.App/Assets/tray-paused.ico    — пауза
#
# Значки трея — тот же логотип с цветной точкой состояния в углу: так значок остаётся
# узнаваемым, а состояние читается с одного взгляда.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $root "ico.png"
$assets = Join-Path $root "src\MeetMemo.App\Assets"

if (-not (Test-Path $source)) { throw "Не найден исходный файл: $source" }
New-Item -ItemType Directory -Force $assets | Out-Null

$original = [System.Drawing.Image]::FromFile($source)

function New-Resized([System.Drawing.Image]$image, [int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.DrawImage($image, 0, 0, $size, $size)
    $g.Dispose()
    return $bmp
}

# Точка состояния в правом нижнем углу: белая обводка отделяет её от тёмного логотипа
function Add-StatusDot([System.Drawing.Bitmap]$bmp, [System.Drawing.Color]$color) {
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # Точка занимает чуть больше трети значка: заметна даже в 16 пикселях,
    # но не закрывает собой логотип.
    $d = [int]($bmp.Width * 0.36)
    $x = $bmp.Width - $d
    $y = $bmp.Height - $d

    $ring = [Math]::Max(1, [int]($bmp.Width * 0.05))
    $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    $g.FillEllipse($white, $x - $ring, $y - $ring, $d + $ring * 2, $d + $ring * 2)

    $brush = New-Object System.Drawing.SolidBrush($color)
    $g.FillEllipse($brush, $x, $y, $d, $d)

    $white.Dispose(); $brush.Dispose(); $g.Dispose()
    return $bmp
}

# .ico со встроенными PNG: формат понимают все версии Windows начиная с Vista,
# а собирать его вручную проще, чем BMP-варианты с масками прозрачности.
function Save-Ico([int[]]$sizes, [string]$path, [scriptblock]$decorate) {
    $images = @()
    foreach ($size in $sizes) {
        $bmp = New-Resized $original $size
        if ($decorate) { $bmp = & $decorate $bmp }

        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $images += , @{ Size = $size; Bytes = $ms.ToArray() }
        $ms.Dispose(); $bmp.Dispose()
    }

    $fs = [System.IO.File]::Create($path)
    $w = New-Object System.IO.BinaryWriter($fs)

    $w.Write([uint16]0)                  # reserved
    $w.Write([uint16]1)                  # type: icon
    $w.Write([uint16]$images.Count)

    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        # 256 в заголовке записывается нулём — так устроен формат
        $dim = if ($img.Size -ge 256) { 0 } else { $img.Size }
        $w.Write([byte]$dim)             # ширина
        $w.Write([byte]$dim)             # высота
        $w.Write([byte]0)                # палитра не используется
        $w.Write([byte]0)                # reserved
        $w.Write([uint16]1)              # плоскости
        $w.Write([uint16]32)             # бит на пиксель
        $w.Write([uint32]$img.Bytes.Length)
        $w.Write([uint32]$offset)
        $offset += $img.Bytes.Length
    }

    foreach ($img in $images) { $w.Write($img.Bytes) }

    $w.Flush(); $w.Dispose(); $fs.Dispose()
    Write-Host "  $([System.IO.Path]::GetFileName($path)) — $([math]::Round((Get-Item $path).Length/1KB,1)) КБ, размеры: $($sizes -join ', ')"
}

Write-Host "Собираю иконки из $source"

# Иконка приложения: полный набор размеров для панели задач, проводника и alt-tab
Save-Ico @(16, 24, 32, 48, 64, 128, 256) (Join-Path $assets "app.ico") $null

# Значки трея: мельче, зато с индикатором состояния
$traySizes = @(16, 20, 24, 32, 40, 48)

Save-Ico $traySizes (Join-Path $assets "tray-idle.ico") $null

Save-Ico $traySizes (Join-Path $assets "tray-recording.ico") {
    param($bmp)
    Add-StatusDot $bmp ([System.Drawing.Color]::FromArgb(255, 229, 57, 53))
}

Save-Ico $traySizes (Join-Path $assets "tray-paused.ico") {
    param($bmp)
    Add-StatusDot $bmp ([System.Drawing.Color]::FromArgb(255, 251, 192, 45))
}

$original.Dispose()
Write-Host "Готово."
