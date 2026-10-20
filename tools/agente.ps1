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
# -m explícito: sin él, `opencode run` puede usar el modelo por defecto de la cuenta.
opencode run --agent $Agente -m openrouter/deepseek/deepseek-v4.1-flash --auto --title $Tarea `
    "Ejecuta la tarea docs/agentes/tareas/$Tarea.md y escribe el informe en docs/agentes/informes/$Tarea.md"
