# Resistencia de la Etapa G (G-05): resume la memoria y el tick de un servidor.log.
#
# Uso:
#   .\tools\carga\memoria.ps1 .\tools\carga\resultados\<fecha>-...\servidor.log
#
# Lee los informes periódicos del servidor ([t=…] … jug=N … p99=… y la línea memoria: GC …)
# y saca:
#   - mem y GC del primer informe con jugadores, del de la mitad y del último con jugadores;
#   - el máximo de mem;
#   - la pendiente de mem en MB/hora (regresión lineal sobre los informes con jugadores);
#   - el p99 de tick del primer y del último tramo (primer/último 20 % de esos informes).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string]$Ruta
)

$ErrorActionPreference = 'Stop'
$invariant = [System.Globalization.CultureInfo]::InvariantCulture

if (-not (Test-Path -LiteralPath $Ruta)) {
    throw "No existe el archivo '$Ruta'."
}

$reportRegex = [regex]'^\[t=\s*([0-9.]+)s\]\s+tick=(\d+)\s+jug=(\d+)\s+tickMs media=([0-9.]+)\s+p99=([0-9.]+)'
$memoryRegex = [regex]'memoria:\s*GC\s+(\d+)/(\d+)/(\d+)\s+mem=([0-9.]+)MB'

$records = New-Object System.Collections.Generic.List[object]
$current = $null
$lineCount = 0

foreach ($line in [System.IO.File]::ReadLines((Resolve-Path -LiteralPath $Ruta))) {
    $lineCount++

    $match = $reportRegex.Match($line)
    if ($match.Success) {
        $current = [pscustomobject]@{
            Time    = [double]::Parse($match.Groups[1].Value, $invariant)
            Players = [int]$match.Groups[3].Value
            P99     = [double]::Parse($match.Groups[5].Value, $invariant)
            MemMb   = $null
            Gc0     = $null
            Gc1     = $null
            Gc2     = $null
        }

        $records.Add($current)
        continue
    }

    $match = $memoryRegex.Match($line)
    if ($match.Success -and $null -ne $current) {
        $current.Gc0 = [int]$match.Groups[1].Value
        $current.Gc1 = [int]$match.Groups[2].Value
        $current.Gc2 = [int]$match.Groups[3].Value
        $current.MemMb = [double]::Parse($match.Groups[4].Value, $invariant)
    }
}

# Solo la fase estable (todos los bots dentro): la rampa de entrada y salida sesga la pendiente.
$maxPlayers = ($records | Measure-Object -Property Players -Maximum).Maximum
$withPlayers = @($records | Where-Object { $_.Players -gt 0 -and $_.Players -eq $maxPlayers -and $null -ne $_.MemMb })

Write-Host "Avelora · resistencia"
Write-Host "  Archivo  : $Ruta"
Write-Host ("  Informes : {0} leídos, {1} con jugadores (máximo {2} jugadores)" -f `
    $records.Count, $withPlayers.Count, ($records | Measure-Object -Property Players -Maximum).Maximum)

if ($withPlayers.Count -eq 0) {
    Write-Host "  No hay ningún informe con jugadores: no se puede medir la resistencia."
    exit 1
}

function Format-Record($record, [string]$label) {
    [pscustomobject]@{
        Informe = $label
        't(s)'  = ('{0:0.0}' -f $record.Time)
        'jug'   = $record.Players
        'mem(MB)' = ('{0:0.0}' -f $record.MemMb)
        'GC 0/1/2' = ('{0}/{1}/{2}' -f $record.Gc0, $record.Gc1, $record.Gc2)
        'p99(ms)' = ('{0:0.000}' -f $record.P99)
    }
}

$first = $withPlayers[0]
$middle = $withPlayers[[int][math]::Floor($withPlayers.Count / 2)]
$last = $withPlayers[$withPlayers.Count - 1]
$maxRecord = $withPlayers | Sort-Object -Property MemMb -Descending | Select-Object -First 1

Write-Host ""
@(
    (Format-Record $first 'primero'),
    (Format-Record $middle 'medio'),
    (Format-Record $last 'último'),
    (Format-Record $maxRecord 'máx mem')
) | Format-Table -AutoSize | Out-String -Width 200 | Write-Host

# Pendiente (MB/hora) por mínimos cuadrados sobre los informes con jugadores.
$origin = $withPlayers[0].Time
$sumX = 0.0
$sumY = 0.0
$sumXx = 0.0
$sumXy = 0.0

foreach ($record in $withPlayers) {
    $x = ($record.Time - $origin) / 3600.0
    $y = $record.MemMb
    $sumX += $x
    $sumY += $y
    $sumXx += $x * $x
    $sumXy += $x * $y
}

$n = $withPlayers.Count
$denominator = ($n * $sumXx) - ($sumX * $sumX)
if ([math]::Abs($denominator) -lt 1e-12) {
    $slope = 0.0
}
else {
    $slope = (($n * $sumXy) - ($sumX * $sumY)) / $denominator
}

Write-Host ("  Máximo de mem : {0:0.0} MB a los {1:0.0} s (jug={2})" -f `
    $maxRecord.MemMb, $maxRecord.Time, $maxRecord.Players)
Write-Host ("  Pendiente     : {0:0.0000} MB/h (regresión sobre {1} informes con jugadores, " -f $slope, $n +
    ("desde t={0:0.0} s hasta t={1:0.0} s)" -f $first.Time, $last.Time))

# p99 por tramo: primer y último 20 % de los informes con jugadores (al menos uno cada uno).
$tramo = [math]::Max(1, [int][math]::Ceiling($withPlayers.Count * 0.2))
$firstTramo = @($withPlayers | Select-Object -First $tramo)
$lastTramo = @($withPlayers | Select-Object -Last $tramo)

function Get-P99Stats($tramoRecords, [string]$label) {
    $p99s = @($tramoRecords | ForEach-Object { $_.P99 })
    $mean = ($p99s | Measure-Object -Average).Average
    $max = ($p99s | Measure-Object -Maximum).Maximum

    [pscustomobject]@{
        Tramo      = $label
        Informes   = $p99s.Count
        'p99 medio' = ('{0:0.000}' -f $mean)
        'p99 máx'  = ('{0:0.000}' -f $max)
        't final'  = ('{0:0.0}' -f $tramoRecords[$tramoRecords.Count - 1].Time)
    }
}

Write-Host ""
Write-Host "  p99 por tramo (primer y último 20 % de los informes con jugadores)"
@(
    (Get-P99Stats $firstTramo 'primero'),
    (Get-P99Stats $lastTramo 'último')
) | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
