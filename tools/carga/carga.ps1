# Carga de la Etapa G (plan §10): un servidor de prueba y N bots, con sus salidas guardadas.
#
# Uso:
#   .\tools\carga\carga.ps1 -Bots 50 -Seconds 120 -Behavior random -NuevaBase
#   .\tools\carga\carga.ps1 -Bots 20 -Seconds 60 -LatencyMs 80 -JitterMs 20 -Loss 0.02 -NuevaBase
#   .\tools\carga\carga.ps1 -Bots 20 -Seconds 60 -LatencyMs 80 -JitterMs 20 -Loss 0.02 -InputRedundancy 1 -NuevaBase
#   .\tools\carga\carga.ps1 -Bots 20 -Seconds 60 -Abusivos 5 -Abuso flood -NuevaBase
#   .\tools\carga\carga.ps1 -Bots 20 -Seconds 60 -LogCorrections -NuevaBase
#
# Compila en Release una vez, arranca el servidor con tools\carga\server-carga.json, espera 2 s,
# lanza los bots (y, si se pide, un segundo proceso de bots abusivos en paralelo) y deja
# servidor.log, bots.log y abusivos.log en tools\carga\resultados\<fecha-hora>-...\.

[CmdletBinding()]
param(
    [int]$Bots = 50,
    [int]$Seconds = 120,
    [ValidateSet('random', 'crowd', 'fight', 'chat', 'inventory')]
    [string]$Behavior = 'random',
    [int]$RampMs = 200,
    [int]$LatencyMs = 0,
    [int]$JitterMs = 0,
    [double]$Loss = 0,
    [ValidateRange(1, 4)]
    [int]$InputRedundancy = 4,
    [int]$Abusivos = 0,
    [ValidateSet('flood', 'garbage', 'speed', 'replay', 'badlogin')]
    [string]$Abuso = 'flood',
    [switch]$LogCorrections,
    [switch]$NuevaBase
)

$ErrorActionPreference = 'Stop'

if ($LatencyMs -lt 0 -or $JitterMs -lt 0) {
    throw "El retardo y el jitter no pueden ser negativos."
}

if ($Loss -lt 0 -or $Loss -ge 1) {
    throw "La pérdida debe cumplir 0 <= P < 1."
}

if ($Abusivos -lt 0) {
    throw "El número de bots abusivos no puede ser negativo."
}

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
$maxBots = [math]::Max($Bots, $Abusivos)
$rampTotal = [math]::Ceiling((($maxBots - 1) * $RampMs) / 1000.0)
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

# La «mala red» se pasa tal cual a los bots; el proxy UDP se arranca dentro del proceso de bots.
$invariant = [System.Globalization.CultureInfo]::InvariantCulture
$lossText = $Loss.ToString($invariant)
$networkArguments = ''
$networkSuffix = ''
if ($LatencyMs -gt 0 -or $JitterMs -gt 0 -or $Loss -gt 0) {
    $networkArguments = ' --latency-ms ' + $LatencyMs + ' --jitter-ms ' + $JitterMs + ' --loss ' + $lossText
    $networkSuffix = '-red' + $LatencyMs + '+' + $JitterMs + '-loss' + $lossText
}

$abuseSuffix = ''
if ($Abusivos -gt 0) {
    $abuseSuffix = '-abuso' + $Abuso + $Abusivos
}

$redundancySuffix = '-redund' + $InputRedundancy
$correctionsSuffix = ''
if ($LogCorrections) {
    $correctionsSuffix = '-corr'
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $resultados "$stamp-$Behavior-$Bots$networkSuffix$redundancySuffix$correctionsSuffix$abuseSuffix"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$serverOut = Join-Path $runDir 'servidor.log'
$botsOut = Join-Path $runDir 'bots.log'
$abuseOut = Join-Path $runDir 'abusivos.log'

# Se arranca a través de cmd con la página de códigos UTF-8 para no perder los acentos al
# redirigir la salida a un archivo. ProcessStartInfo (y no Start-Process) para poder leer el
# código de salida de dos procesos que corren en paralelo.
function Start-LoggedCommand([string]$commandLine) {
    $startInfo = New-Object System.Diagnostics.ProcessStartInfo
    $startInfo.FileName = 'cmd.exe'
    $startInfo.Arguments = '/c ' + $commandLine
    $startInfo.WorkingDirectory = $root
    $startInfo.UseShellExecute = $false
    return [System.Diagnostics.Process]::Start($startInfo)
}

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

$commonArguments = ' --seconds ' + $Seconds + ' --ramp-ms ' + $RampMs + $networkArguments

$correctionsArgument = ''
if ($LogCorrections) {
    $correctionsArgument = ' --log-corrections'
}

$botsCommand = 'chcp 65001 >nul & "' + $botsExe + '" --bots ' + $Bots + $commonArguments +
    ' --behavior ' + $Behavior + ' --input-redundancy ' + $InputRedundancy + $correctionsArgument +
    ' > "' + $botsOut + '" 2>&1'

Write-Host "Bots: $Bots durante $Seconds s, rampa $RampMs ms, comportamiento $Behavior, redundancia $InputRedundancy"
$botsProcess = Start-LoggedCommand $botsCommand

$abuseProcess = $null
if ($Abusivos -gt 0) {
    $abuseCommand = 'chcp 65001 >nul & "' + $botsExe + '" --bots ' + $Abusivos + $commonArguments +
        ' --behavior ' + $Abuso + ' --account-prefix abuso > "' + $abuseOut + '" 2>&1'

    Write-Host "Abusivos: $Abusivos bots '$Abuso' en paralelo (cuentas abuso*)"
    $abuseProcess = Start-LoggedCommand $abuseCommand
}

$botsProcess.WaitForExit()
$botsCode = $botsProcess.ExitCode

if ($botsCode -ne 0) {
    Write-Warning "Los bots salieron con código $botsCode (revisa $botsOut)."
}

if ($null -ne $abuseProcess) {
    $abuseProcess.WaitForExit()
    Write-Host "Bots abusivos terminados con código $($abuseProcess.ExitCode) (informativo)."
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
if ($Abusivos -gt 0) {
    Write-Host "  abusivos : $abuseOut"
}
