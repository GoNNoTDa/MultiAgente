<#
.SYNOPSIS
  Instala lo que necesita MultiAgente y aún no está en el equipo: las CLIs de los agentes y sus adaptadores ACP
  (npm global del usuario, sin administrador). Lo que ya está instalado no se toca, salvo con -Actualizar.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tools\Instalar-Requisitos.ps1
  powershell -ExecutionPolicy Bypass -File tools\Instalar-Requisitos.ps1 -Actualizar
#>
param(
    # Lleva también a la última versión lo que ya estaba instalado.
    [switch]$Actualizar
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command npm -ErrorAction SilentlyContinue)) {
    Write-Host 'Falta Node.js. Instálalo desde https://nodejs.org (versión LTS) y vuelve a ejecutar este script.' -ForegroundColor Yellow
    exit 1
}

# Paquete npm → orden con la que se comprueba que está
$requisitos = [ordered]@{
    '@anthropic-ai/claude-code'             = 'claude'
    '@agentclientprotocol/claude-agent-acp' = 'claude-agent-acp'
    '@openai/codex'                         = 'codex'
    '@agentclientprotocol/codex-acp'        = 'codex-acp'
    '@google/gemini-cli'                    = 'gemini'
}

$instalados = @{}
$lista = npm ls -g --depth=0 --json 2>$null | ConvertFrom-Json
if ($lista.dependencies) { foreach ($p in $lista.dependencies.PSObject.Properties) { $instalados[$p.Name] = $p.Value.version } }

$aInstalar = @()
foreach ($paquete in $requisitos.Keys) {
    if ($instalados.ContainsKey($paquete)) {
        Write-Host ("  {0,-40} {1}" -f $paquete, $instalados[$paquete])
        if ($Actualizar) { $aInstalar += "$paquete@latest" }
    } elseif (Get-Command $requisitos[$paquete] -ErrorAction SilentlyContinue) {
        Write-Host ("  {0,-40} instalado por otra vía ({1})" -f $paquete, $requisitos[$paquete])
    } else {
        Write-Host ("  {0,-40} falta" -f $paquete) -ForegroundColor Yellow
        $aInstalar += $paquete
    }
}

if ($aInstalar.Count -eq 0) { Write-Host 'Todo está instalado.'; exit 0 }

Write-Host "Instalando: $($aInstalar -join ', ')"
npm install -g @aInstalar --no-fund --no-audit --loglevel=error
if ($LASTEXITCODE -ne 0) { throw "npm terminó con código $LASTEXITCODE." }
Write-Host 'Listo.'
