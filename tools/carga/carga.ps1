# Carga de la Etapa G (plan §10): un servidor de prueba y N bots, con sus salidas guardadas.
#
# Uso:
#   .\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior random -NuevaBase
#   .\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior crowd -NuevaBase
#
# Compila en Release una vez, arranca el servidor con tools\carga\server-carga.json, espera 2 s,
# lanza los bots y deja servidor.log y bots.log en tools\carga\resultados\<fecha-hora>-<behavior>-<bots>\.

[CmdletBinding()]
param(
    [int]$Bots = 50,
    [int]$Seconds = 120,
    [ValidateSet('random', 'crowd')]
    [string]$Behavior = 'random',
    [int]$RampMs = 200,
    [switch]$NuevaBase
)

$ErrorActionPreference = 'Stop'

$cargaDir = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $cargaDir)
$config = Join-Path $cargaDir 'server-carga.json'
$database = Join-Path $cargaDir 'avelora-carga.db'
$resultados = Join-Path $cargaDir 'resultados'
$serverExe = Join-Path $root 'server\Avelora.Server\bin\Release\net10.0\Avelora.Server.exe'
$botsExe = Join-Path $root 'tools\Avelora.Bots\bin\Release\net10.0\Avelora.Bots.exe'

$dotnetCommand = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -ne $dotnetCommand) {
    $dotnet = $dotnetCommand.Source
}
else {
    $dotnet = 'C:\Users\SnyderJGZ\AppData\Local\Microsoft\dotnet\dotnet.exe'
}

if (-not (Test-Path $dotnet)) {
    throw "No se encuentra dotnet. Instálalo o ajusta la ruta en este guion."
}

# El tiempo del servidor cubre la rampa completa y deja margen para que los bots terminen.
$rampTotal = [math]::Ceiling((($Bots - 1) * $RampMs) / 1000.0)
$serverSeconds = $Seconds + $rampTotal + 30

Write-Host "Compilando en Release…"
Push-Location $root
try {
    & $dotnet build Avelora.slnx -c Release -v q --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "La compilación falló (código $LASTEXITCODE)."
    }
}
finally {
    Pop-Location
}

if ($NuevaBase) {
    Remove-Item -LiteralPath $database, "$database-wal", "$database-shm" -Force -ErrorAction SilentlyContinue
    Write-Host "Base de la prueba borrada: $database"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $resultados "$stamp-$Behavior-$Bots"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$serverOut = Join-Path $runDir 'servidor.log'
$botsOut = Join-Path $runDir 'bots.log'

# Se arranca a través de cmd con la página de códigos UTF-8 para no perder los acentos al
# redirigir la salida a un archivo.
$serverCommand = 'chcp 65001 >nul & "' + $serverExe + '" "' + $config + '" --seconds ' + $serverSeconds +
    ' > "' + $serverOut + '" 2>&1'

Write-Host "Servidor: $serverSeconds s  base=$database"
$server = Start-Process -FilePath 'cmd.exe' `
    -ArgumentList @('/c', $serverCommand) `
    -WorkingDirectory $root `
    -NoNewWindow -PassThru

Start-Sleep -Seconds 2

if ($server.HasExited) {
    throw "El servidor terminó antes de lanzar los bots. Revisa $serverOut."
}

$botsCommand = 'chcp 65001 >nul & "' + $botsExe + '" --bots ' + $Bots + ' --seconds ' + $Seconds +
    ' --ramp-ms ' + $RampMs + ' --behavior ' + $Behavior + ' > "' + $botsOut + '" 2>&1'

Write-Host "Bots: $Bots durante $Seconds s, rampa $RampMs ms, comportamiento $Behavior"
$botsProcess = Start-Process -FilePath 'cmd.exe' `
    -ArgumentList @('/c', $botsCommand) `
    -WorkingDirectory $root `
    -NoNewWindow -Wait -PassThru

$botsCode = $botsProcess.ExitCode

if ($botsCode -ne 0) {
    Write-Warning "Los bots salieron con código $botsCode (revisa $botsOut)."
}

$deadline = (Get-Date).AddSeconds($serverSeconds + 60)
while (-not $server.HasExited -and (Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 500
}

if (-not $server.HasExited) {
    Write-Warning "El servidor seguía vivo; se detiene a la fuerza."
    & taskkill /PID $server.Id /T /F | Out-Null
}

Write-Host ""
Write-Host "Resultados en: $runDir"
Write-Host "  servidor : $serverOut"
Write-Host "  bots     : $botsOut"
