<#
.SYNOPSIS
  Prueba de portabilidad (F1): empaqueta, copia la carpeta a otra ruta (con espacios) y comprueba que arranca sin
  nada más, que crea datos\ dentro de la copia, que no escribe en %APPDATA% / %LOCALAPPDATA%, que la ventana pinta
  la interfaz (captura PNG) y que una segunda copia de la misma carpeta no abre otra instancia.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Probar-Portatil.ps1
  powershell -ExecutionPolicy Bypass -File tools\Probar-Portatil.ps1 -SinEmpaquetar
#>
param([switch]$SinEmpaquetar)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $raiz 'dist\MultiAgente'
$fallos = @()
function Comprobar([bool]$ok, [string]$que) {
    if ($ok) { Write-Host "  OK  $que" -ForegroundColor Green }
    else { Write-Host "  MAL $que" -ForegroundColor Red; $script:fallos += $que }
}

if (-not $SinEmpaquetar) { & (Join-Path $PSScriptRoot 'Empaquetar.ps1') }

$base = Join-Path ([IO.Path]::GetTempPath()) "multiagente_portatil_$([guid]::NewGuid().ToString('N').Substring(0,8))"
$copia = Join-Path $base 'Otro PC\Multi Agente'
New-Item -ItemType Directory -Force $copia | Out-Null
Copy-Item (Join-Path $dist '*') $copia -Recurse -Exclude 'datos'
$exe = Join-Path $copia 'MultiAgente.exe'
$antesLocal = Test-Path (Join-Path $env:LOCALAPPDATA 'MultiAgente')
$antesRoaming = Test-Path (Join-Path $env:APPDATA 'MultiAgente')
Write-Host "Copia en: $copia"

try {
    # 1. Arranque sin ventana
    $json = Join-Path $base 'comprobacion.json'
    $p = Start-Process $exe -ArgumentList '--comprobar', "`"$json`"" -PassThru -Wait
    Comprobar ($p.ExitCode -eq 0) "arranca y se comprueba (código $($p.ExitCode))"
    $r = if (Test-Path $json) { Get-Content $json -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    Comprobar ($null -ne $r -and $r.raiz -eq $copia) 'la raíz es la carpeta copiada'
    Comprobar (Test-Path (Join-Path $copia 'datos\multiagente.db')) 'crea datos\multiagente.db dentro de la copia'
    Comprobar ($null -ne $r -and $r.tablas.Count -ge 8) "esquema creado ($($r.esquema), $($r.tablas.Count) tablas)"
    Comprobar ((Get-ChildItem (Join-Path $copia 'datos\logs') -Filter *.log -ErrorAction SilentlyContinue).Count -ge 1) 'registro en datos\logs'

    # 2. Ventana con la interfaz (fuera de la pantalla) y captura
    $png = Join-Path $base 'captura.png'
    $p = Start-Process $exe -ArgumentList '--captura', "`"$png`"" -PassThru
    if (-not $p.WaitForExit(60000)) { $p.Kill(); Comprobar $false 'la ventana de captura se cierra sola' }
    Comprobar ((Test-Path $png) -and (Get-Item $png).Length -gt 10000) 'la ventana pinta la interfaz (captura)'
    Comprobar (Test-Path (Join-Path $copia 'datos\webview')) 'el perfil del navegador queda en datos\webview'

    # 3. Instancia única por carpeta: con una abierta, la segunda termina enseguida
    $abierta = Start-Process $exe -ArgumentList '--fuera' -PassThru
    Start-Sleep -Seconds 4
    $segunda = Start-Process $exe -ArgumentList '--comprobar' -PassThru -Wait
    Comprobar ($segunda.ExitCode -eq 2) "una segunda copia de la misma carpeta no abre otra (código $($segunda.ExitCode))"
    Comprobar (-not $abierta.HasExited) 'la primera sigue abierta'
    $reloj = [Diagnostics.Stopwatch]::StartNew()
    Start-Process $exe -ArgumentList '--cerrar' -Wait
    $cerro = $abierta.WaitForExit(15000)
    Comprobar $cerro "--cerrar cierra la abierta ($($reloj.ElapsedMilliseconds) ms)"
    if (-not $cerro) { $abierta.Kill() }
    $vivos = @(Get-CimInstance Win32_Process -Filter "Name='msedgewebview2.exe'" | Where-Object { $_.CommandLine -like "*$copia*" }).Count
    Comprobar ($vivos -eq 0) "al cerrarse no deja procesos del navegador ($vivos)"

    # 4. Nada fuera de la carpeta
    Comprobar ($antesLocal -or -not (Test-Path (Join-Path $env:LOCALAPPDATA 'MultiAgente'))) 'no crea %LOCALAPPDATA%\MultiAgente'
    Comprobar ($antesRoaming -or -not (Test-Path (Join-Path $env:APPDATA 'MultiAgente'))) 'no crea %APPDATA%\MultiAgente'

    # 5. Mover la carpeta ya usada a otra ruta: sigue funcionando con sus datos
    $movida = Join-Path $base 'Movida'
    Move-Item $copia $movida
    $json2 = Join-Path $base 'comprobacion2.json'
    $p = Start-Process (Join-Path $movida 'MultiAgente.exe') -ArgumentList '--comprobar', "`"$json2`"" -PassThru -Wait
    $r2 = if (Test-Path $json2) { Get-Content $json2 -Raw -Encoding UTF8 | ConvertFrom-Json } else { $null }
    Comprobar ($p.ExitCode -eq 0 -and $null -ne $r2 -and $r2.raiz -eq $movida) 'tras moverla, arranca con sus datos en la ruta nueva'

    $capturas = Join-Path $raiz 'dist\pruebas'
    New-Item -ItemType Directory -Force $capturas | Out-Null
    if (Test-Path $png) { Copy-Item $png (Join-Path $capturas 'portatil_captura.png') -Force }
}
finally {
    Start-Sleep -Seconds 1
    Remove-Item $base -Recurse -Force -ErrorAction SilentlyContinue
}

if ($fallos.Count) { Write-Host "$($fallos.Count) comprobaciones fallidas." -ForegroundColor Red; exit 1 }
Write-Host 'Portabilidad: todo correcto. Captura en dist\pruebas\portatil_captura.png' -ForegroundColor Green
