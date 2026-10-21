<#
.SYNOPSIS
  Deja MultiAgente listo para copiar: un ejecutable autocontenido (no hace falta instalar .NET) más ui\, tools\ y el
  README, en dist\MultiAgente. No incluye datos\ (cuentas, sesiones): esa carpeta la crea la aplicación al arrancar.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Empaquetar.ps1
#>
param(
    [string]$Salida = (Join-Path (Split-Path $PSScriptRoot -Parent) 'dist\MultiAgente')
)

$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot -Parent

if (Test-Path $Salida) {
    # Se conserva datos\ si alguien la está usando desde aquí
    Get-ChildItem $Salida -Force | Where-Object { $_.Name -ne 'datos' } | Remove-Item -Recurse -Force
}

Write-Host 'Publicando MultiAgente (autocontenido, un solo ejecutable)...'
dotnet publish (Join-Path $raiz 'src\MultiAgente.App\MultiAgente.App.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=None -p:DebugSymbols=false -p:PublishReferencesDocumentationFiles=false `
    -o $Salida -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet publish terminó con código $LASTEXITCODE." }

$tools = Join-Path $Salida 'tools'
New-Item -ItemType Directory -Force $tools | Out-Null
Copy-Item (Join-Path $raiz 'tools\Instalar-Requisitos.ps1'), (Join-Path $raiz 'tools\Sondear-Agentes.mjs') $tools
Copy-Item (Join-Path $raiz 'README.md') $Salida

$mb = [math]::Round(((Get-ChildItem $Salida -Recurse -File | Where-Object { $_.FullName -notlike '*\datos\*' } |
        Measure-Object Length -Sum).Sum / 1MB), 1)
Write-Host "Listo: $Salida ($mb MB)"
Get-ChildItem $Salida | Select-Object Mode, Length, Name | Format-Table -AutoSize | Out-String | Write-Host
