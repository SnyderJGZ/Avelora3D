# Caídas del servidor (Etapa G, G-05): matar el servidor en pleno juego, reiniciarlo y medir
# cuánto se pierde respecto al último guardado.
#
# Uso:
#   .\tools\carga\caida.ps1 -Bots 20 -MatarA 150 -NuevaBase
#
# Pasos:
#   1. Arranca el servidor con tools\carga\server-carga.json y los bots (random) con traza.
#   2. A los MatarA segundos mata el servidor con Stop-Process -Force y apunta el instante UTC.
#   3. Comprueba la base con --check-db (debe salir 0).
#   4. Reinicia el servidor y lanza los mismos bots en crowd con --report-welcome.
#   5. Compara la posición del Welcome con la traza (margen 1/64 de tile): la pérdida de cada
#      cuenta es lo que separa la última coincidencia del instante de muerte.
#   6. Deja tabla, resumen y logs en tools\carga\resultados\<fecha>-caida-<bots>\.

[CmdletBinding()]
param(
    [int]$Bots = 20,
    [int]$MatarA = 150,
    [int]$RampMs = 200,
    [switch]$NuevaBase
)

$ErrorActionPreference = 'Stop'

if ($Bots -lt 1) {
    throw "El número de bots debe ser >= 1."
}

if ($MatarA -lt 5) {
    throw "MatarA debe ser al menos 5 s para que los bots lleguen a jugar."
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

$settings = Get-Content -LiteralPath $config -Raw | ConvertFrom-Json
$saveInterval = [double]$settings.saveIntervalSeconds
$maxLoss = $saveInterval + 5

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

if (-not (Test-Path $serverExe)) {
    throw "No existe $serverExe."
}

if (-not (Test-Path $botsExe)) {
    throw "No existe $botsExe."
}

if ($NuevaBase) {
    Remove-Item -LiteralPath $database, "$database-wal", "$database-shm" -Force -ErrorAction SilentlyContinue
    Write-Host "Base de la prueba borrada: $database"
}

$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$runDir = Join-Path $resultados "$stamp-caida-$Bots"
New-Item -ItemType Directory -Force -Path $runDir | Out-Null

$serverOut = Join-Path $runDir 'servidor-antes.log'
$botsOut = Join-Path $runDir 'bots-antes.log'
$server2Out = Join-Path $runDir 'servidor-despues.log'
$bots2Out = Join-Path $runDir 'bots-despues.log'
$tracePath = Join-Path $runDir 'traza.csv'
$welcomePath = Join-Path $runDir 'welcome.csv'
$comparePath = Join-Path $runDir 'comparacion.log'
$checkPath = Join-Path $runDir 'check-db.log'
$checkAfterPath = Join-Path $runDir 'check-db-final.log'

Remove-Item -LiteralPath $tracePath, $welcomePath -Force -ErrorAction SilentlyContinue

function Start-ServerProcess([string]$logPath) {
    return Start-Process -FilePath $serverExe `
        -ArgumentList ('"' + $config + '"') `
        -WorkingDirectory $root `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $logPath `
        -RedirectStandardError ($logPath + '.err')
}

function Start-BotsProcess([string]$arguments, [string]$logPath) {
    return Start-Process -FilePath $botsExe `
        -ArgumentList $arguments `
        -WorkingDirectory $root `
        -NoNewWindow -PassThru `
        -RedirectStandardOutput $logPath `
        -RedirectStandardError ($logPath + '.err')
}

Write-Host ""
Write-Host "Caída: $Bots bots, muerte a los $MatarA s, base $database"

$clock = [System.Diagnostics.Stopwatch]::StartNew()
$server = Start-ServerProcess $serverOut
Start-Sleep -Seconds 2

if ($server.HasExited) {
    throw "El servidor terminó antes de lanzar los bots. Revisa $serverOut."
}

$botSeconds = $MatarA + 20
$botArguments = '--bots ' + $Bots +
    ' --seconds ' + $botSeconds +
    ' --ramp-ms ' + $RampMs +
    ' --behavior random' +
    ' --trace-positions "' + $tracePath + '"'

Write-Host "Bots antes: $Bots durante $botSeconds s, traza $tracePath"
$botsProcess = Start-BotsProcess $botArguments $botsOut

$remaining = $MatarA - $clock.Elapsed.TotalSeconds
if ($remaining -gt 0) {
    Start-Sleep -Milliseconds ([int][math]::Ceiling($remaining * 1000))
}

$diedUtc = [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
Stop-Process -Id $server.Id -Force
$server.WaitForExit()

Write-Host ("Servidor muerto a los {0:0.00} s (instante UTC {1} ms)." -f $clock.Elapsed.TotalSeconds, $diedUtc)

# Instante real del último guardado: la marca de tiempo más reciente de los archivos de la
# base (con WAL, el commit toca sobre todo el -wal). Sirve para medir la pérdida sin depender
# de que la traza de 1 Hz caiga justo en la posición guardada.
$savedUtcMs = $null
foreach ($file in @($database, "$database-wal")) {
    if (Test-Path -LiteralPath $file) {
        $written = (Get-Item -LiteralPath $file).LastWriteTimeUtc
        $writtenMs = [DateTimeOffset]::new($written).ToUnixTimeMilliseconds()
        if ($null -eq $savedUtcMs -or $writtenMs -gt $savedUtcMs) {
            $savedUtcMs = $writtenMs
        }
    }
}

if ($null -ne $savedUtcMs) {
    $realLoss = ($diedUtc - $savedUtcMs) / 1000
    Write-Host ("Último guardado (mtime de la base): pérdida real {0:0.0} s." -f $realLoss)
}

# Los bots notan la desconexión y terminan solos; hay que dejarlos acabar.
$botsProcess.WaitForExit()
Write-Host "Bots antes terminados."

Write-Host ""
Write-Host "Comprobando la base con --check-db…"
$checkText = (& $serverExe $config --check-db 2>&1 | Out-String)
$checkCode = $LASTEXITCODE
Set-Content -LiteralPath $checkPath -Value $checkText -Encoding utf8
Write-Host $checkText

if ($checkCode -ne 0) {
    Write-Warning "La base no pasó --check-db (código $checkCode). La comparación sigue, pero el resultado no es válido."
}

Write-Host "Reiniciando el servidor y leyendo las posiciones guardadas…"
$server2 = Start-ServerProcess $server2Out
Start-Sleep -Seconds 2

if ($server2.HasExited) {
    throw "El servidor no arrancó tras la caída. Revisa $server2Out."
}

$welcomeArguments = '--bots ' + $Bots +
    ' --seconds 10' +
    ' --ramp-ms ' + $RampMs +
    ' --behavior crowd' +
    ' --report-welcome "' + $welcomePath + '"'

$bots2Process = Start-BotsProcess $welcomeArguments $bots2Out
$bots2Process.WaitForExit()
Write-Host "Bots después terminados."

Stop-Process -Id $server2.Id -Force
$server2.WaitForExit()

Write-Host ""
Write-Host "Comparando traza y Welcome (tope $maxLoss s = guardado $saveInterval s + 5 s)…"

$compareText = (& $botsExe --compare-trace --trace $tracePath --welcome $welcomePath --death-ms $diedUtc --max-loss $maxLoss 2>&1 | Out-String)
$compareCode = $LASTEXITCODE
Set-Content -LiteralPath $comparePath -Value $compareText -Encoding utf8
Write-Host $compareText

# Comprobación extra después del segundo apagado: el reinicio no debe dejarla peor.
$checkAfterText = (& $serverExe $config --check-db 2>&1 | Out-String)
$checkAfterCode = $LASTEXITCODE
Set-Content -LiteralPath $checkAfterPath -Value $checkAfterText -Encoding utf8

@(
    "bots=$Bots",
    "matarA=$MatarA",
    "muerte_utc_ms=$diedUtc",
    "ultimo_guardado_utc_ms=$(if ($null -eq $savedUtcMs) { 'desconocido' } else { $savedUtcMs })",
    "perdida_real_s=$(if ($null -eq $savedUtcMs) { 'desconocida' } else { (($diedUtc - $savedUtcMs) / 1000).ToString('0.000', [System.Globalization.CultureInfo]::InvariantCulture) })",
    "check_db=$checkCode",
    "check_db_final=$checkAfterCode",
    "comparacion_exit=$compareCode",
    "tope_perdida_s=$maxLoss",
    "base=$database"
) | Set-Content -LiteralPath (Join-Path $runDir 'parametros.txt') -Encoding utf8

Write-Host ""
Write-Host "Resumen de la caída"
Write-Host "  Bots            : $Bots"
Write-Host ("  Muerte          : {0:0.00} s tras arrancar (UTC {1} ms)" -f $clock.Elapsed.TotalSeconds, $diedUtc)
if ($null -ne $savedUtcMs) {
    Write-Host ("  Pérdida real    : {0:0.0} s desde el último guardado (mtime de la base)" -f (($diedUtc - $savedUtcMs) / 1000))
}
Write-Host "  --check-db      : $checkCode (final: $checkAfterCode)"
Write-Host "  Comparación     : $compareCode (0 = todas las cuentas OK)"
Write-Host "  Resultados en   : $runDir"
Write-Host "    traza         : $tracePath"
Write-Host "    welcome       : $welcomePath"
Write-Host "    comparación   : $comparePath"

if ($checkCode -ne 0 -or $compareCode -ne 0) {
    exit 1
}
