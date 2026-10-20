<#
.SYNOPSIS
    Lanza un agente de OpenCode (DeepSeek v4.1) sobre una tarea de docs/agentes/tareas/.

.EXAMPLE
    .\tools\agente.ps1 -Agente avelora-dev -Tarea F-01
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('avelora-dev', 'avelora-unity')][string]$Agente,
    [Parameter(Mandatory = $true)][string]$Tarea
)

$raiz = Split-Path $PSScriptRoot -Parent
$archivo = Join-Path $raiz "docs/agentes/tareas/$Tarea.md"
if (-not (Test-Path $archivo)) { throw "No existe la tarea: $archivo" }

Set-Location $raiz
# El SDK de .NET está instalado por usuario; el agente lo necesita en el PATH.
$dotnet = Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'
if (($env:Path -split ';') -notcontains $dotnet) { $env:Path = "$dotnet;$env:Path" }
$env:DOTNET_ROOT = $dotnet

# -m explícito: sin él, `opencode run` puede usar el modelo por defecto de la cuenta.
# --standalone: servidor propio con este PATH, no el servicio de fondo con el entorno viejo.
opencode run --standalone --agent $Agente -m openrouter/deepseek/deepseek-v4.1-flash --auto --title $Tarea `
    "Ejecuta la tarea docs/agentes/tareas/$Tarea.md y escribe el informe en docs/agentes/informes/$Tarea.md"
