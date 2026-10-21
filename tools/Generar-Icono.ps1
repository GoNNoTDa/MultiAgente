<#
.SYNOPSIS
  Genera src\MultiAgente.App\MultiAgente.ico: un bocadillo de chat con tres puntos (Claude, Codex y Gemini en una misma
  conversación) sobre una tesela oscura, en 16, 20, 24, 32, 40, 48, 64, 128 y 256 px (PNG dentro del .ico).
#>
param([string]$Salida = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\MultiAgente.App\MultiAgente.ico'))

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function Color([int]$r, [int]$v, [int]$a) { [System.Drawing.Color]::FromArgb(255, $r, $v, $a) }

function Dibujar([int]$t) {
    $bmp = New-Object System.Drawing.Bitmap $t, $t, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)
    $g.ScaleTransform([single]($t / 256.0), [single]($t / 256.0)) # se dibuja sobre 256 x 256
    # Tesela redondeada con un degradado suave
    $ruta = New-Object System.Drawing.Drawing2D.GraphicsPath
    $ruta.AddArc(8, 8, 116, 116, 180, 90); $ruta.AddArc(132, 8, 116, 116, 270, 90)
    $ruta.AddArc(132, 132, 116, 116, 0, 90); $ruta.AddArc(8, 132, 116, 116, 90, 90)
    $ruta.CloseFigure()
    $degradado = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 8)), ([System.Drawing.PointF]::new(0, 248)), (Color 0x34 0x30 0x2c), (Color 0x1c 0x1a 0x18)
    $g.FillPath($degradado, $ruta)
    # Bocadillo: rectángulo redondeado con la cola abajo a la izquierda
    $b = New-Object System.Drawing.Drawing2D.GraphicsPath
    $b.AddArc(24, 52, 80, 80, 180, 90); $b.AddArc(152, 52, 80, 80, 270, 90); $b.AddArc(152, 108, 80, 80, 0, 90)
    $b.AddLine(192, 188, 112, 188); $b.AddLine(112, 188, 72, 220); $b.AddLine(72, 220, 72, 188)
    $b.AddArc(24, 108, 80, 80, 90, 90)
    $b.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush (Color 0xf6 0xf2 0xec)), $b)
    # Tres puntos: Claude, Codex, Gemini (más grandes en los tamaños pequeños, para que se vean)
    $r = $(if ($t -le 24) { 25 } else { 19 })
    $paso = $(if ($t -le 24) { 50 } else { 44 })
    $colores = (Color 0xd9 0x7a 0x50), (Color 0x1f 0xae 0x86), (Color 0x4a 0x8a 0xf0)
    for ($i = 0; $i -lt 3; $i++) {
        $x = 128 + ($i - 1) * $paso
        $g.FillEllipse((New-Object System.Drawing.SolidBrush $colores[$i]), [single]($x - $r), [single](120 - $r), [single](2 * $r), [single](2 * $r))
    }
    $g.Dispose()
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    return , $ms.ToArray()
}

$tamanos = 16, 20, 24, 32, 40, 48, 64, 128, 256
$imagenes = foreach ($t in $tamanos) { , (Dibujar $t) }

$fs = [System.IO.File]::Create($Salida)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$tamanos.Count)
$desplazamiento = 6 + 16 * $tamanos.Count
for ($i = 0; $i -lt $tamanos.Count; $i++) {
    $t = $tamanos[$i]; $datos = $imagenes[$i]
    $w.Write([byte]($(if ($t -ge 256) { 0 } else { $t }))); $w.Write([byte]($(if ($t -ge 256) { 0 } else { $t })))
    $w.Write([byte]0); $w.Write([byte]0); $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$datos.Length); $w.Write([uint32]$desplazamiento)
    $desplazamiento += $datos.Length
}
foreach ($d in $imagenes) { $w.Write($d) }
$w.Close()
Write-Host "Icono: $Salida"
