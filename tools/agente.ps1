<#
.SYNOPSIS
    Lanza un agente de OpenCode sobre una tarea de docs/agentes/tareas/.

.EXAMPLE
    .\tools\agente.ps1 -Agente avelora-dev -Tarea F-01
    .\tools\agente.ps1 -Agente avelora-dev -Tarea F-01 -Modelo opencode/deepseek-v4.1-flash-free
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('avelora-dev', 'avelora-unity')][string]$Agente,
    [Parameter(Mandatory = $true)][string]$Tarea,
    # 2026-10-24: el cupo gratuito de OpenCode es de toda la cuenta y se agotó para todos sus
    # modelos («Free usage limit reached»); Nemotron 3 Ultra gratuito por OpenRouter sí responde.
    [string]$Modelo = 'openrouter/nvidia/nemotron-3-ultra-550b-a55b:free'
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
opencode run --standalone --agent $Agente -m $Modelo --auto --title $Tarea `
    ("Ejecuta la tarea docs/agentes/tareas/$Tarea.md y escribe el informe en docs/agentes/informes/$Tarea.md. " +
     # El shell del agente no siempre hereda el PATH (pasó al lanzarlo desde el panel Terminal de la app).
     "Si 'dotnet' no está en el PATH, usa la ruta completa: & '$dotnet\dotnet.exe'. No lo busques por el disco.")
