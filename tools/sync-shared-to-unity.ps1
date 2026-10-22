<#
.SYNOPSIS
    Copia el código compartido y el cliente del repositorio al proyecto de Unity.

.DESCRIPTION
    El repositorio es la fuente de verdad. Este script sincroniza:

      shared/Avelora.Shared/**   ->  <Unity>/Assets/Avelora/Scripts/Shared/**
      shared/Avelora.Net/**      ->  <Unity>/Assets/Avelora/Scripts/Net/**
      client/Assets/Avelora/**   ->  <Unity>/Assets/Avelora/**

    Copia archivos .cs, .asmdef, .unity, .png, .asset y .dll junto con su .meta. El .meta es
    obligatorio para conservar los GUID: la escena y los componentes de Unity referencian los
    scripts y los assets por GUID, y si Unity generase GUIDs nuevos las referencias se romperían.

    Ignora bin/ y obj/. Nunca borra nada: sobrescribe lo que exista, salvo que el destino sea
    más nuevo que el origen (por ejemplo, una escena o un tile guardados en Unity después de la
    última sincronización). Esos archivos se saltan con un aviso.

    Unity recompila solo al volver al Editor (o con Assets > Refresh).

.PARAMETER ProyectoUnity
    Ruta de la carpeta del proyecto de Unity (la que contiene Assets/).

.PARAMETER Forzar
    Sobrescribe también los archivos del proyecto de Unity más nuevos que los del repositorio.

.EXAMPLE
    .\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)'

.EXAMPLE
    .\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)' -Forzar
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ProyectoUnity,

    [switch]$Forzar
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path (Join-Path $ProyectoUnity 'Assets'))) {
    throw "No parece un proyecto de Unity (no encuentro Assets\): $ProyectoUnity"
}

$raiz    = Split-Path -Parent $PSScriptRoot
$shared  = Join-Path $raiz 'shared\Avelora.Shared'
$net     = Join-Path $raiz 'shared\Avelora.Net'
$cliente = Join-Path $raiz 'client\Assets\Avelora'
$destino = Join-Path $ProyectoUnity 'Assets\Avelora'

$extensiones = @('.cs', '.asmdef', '.unity', '.png', '.asset', '.dll')

$script:archivosSaltados = 0

function Copy-Si-Procede {
    param([string]$OrigenArchivo, [string]$RutaDestino)

    New-Item -ItemType Directory -Path (Split-Path -Parent $RutaDestino) -Force | Out-Null

    if (-not $Forzar -and (Test-Path -LiteralPath $RutaDestino)) {
        $origenHora = (Get-Item -LiteralPath $OrigenArchivo).LastWriteTimeUtc
        $destinoHora = (Get-Item -LiteralPath $RutaDestino).LastWriteTimeUtc

        if ($destinoHora -gt $origenHora) {
            $script:archivosSaltados++
            Write-Warning ("{0}: más nuevo en Unity; usa -Forzar" -f $RutaDestino)
            return $false
        }
    }

    Copy-Item -LiteralPath $OrigenArchivo -Destination $RutaDestino -Force
    return $true
}

function Copy-Archivos {
    param([string]$Origen, [string]$SubcarpetaDestino)

    Get-ChildItem -Path $Origen -Recurse -File |
        Where-Object {
            $extensiones -contains $_.Extension -and
            $_.FullName -notmatch '\\(bin|obj|\.git)\\'
        } |
        ForEach-Object {
            $relativo = $_.FullName.Substring($Origen.Length).TrimStart('\')
            $salida = Join-Path $SubcarpetaDestino $relativo

            if (Copy-Si-Procede -OrigenArchivo $_.FullName -RutaDestino $salida) {
                Write-Host ("  {0}" -f $relativo)
            }

            $meta = $_.FullName + '.meta'
            if (Test-Path -LiteralPath $meta) {
                Copy-Si-Procede -OrigenArchivo $meta -RutaDestino ($salida + '.meta') | Out-Null
            }
            else {
                Write-Warning "Falta $relativo.meta en el repositorio; Unity generara un GUID nuevo."
            }
        }
}

Write-Host 'Sincronizando codigo compartido...' -ForegroundColor Cyan
Copy-Archivos -Origen $shared -SubcarpetaDestino (Join-Path $destino 'Scripts\Shared')

Write-Host 'Sincronizando transporte de red...' -ForegroundColor Cyan
Copy-Archivos -Origen $net -SubcarpetaDestino (Join-Path $destino 'Scripts\Net')

Write-Host 'Sincronizando cliente...' -ForegroundColor Cyan
Copy-Archivos -Origen $cliente -SubcarpetaDestino $destino

if ($script:archivosSaltados -gt 0) {
    Write-Host ''
    Write-Host ("Se saltaron {0} archivo(s) más nuevos en Unity; usa -Forzar para sobrescribirlos." -f $script:archivosSaltados) -ForegroundColor Yellow
}

Write-Host ''
Write-Host 'Listo. Vuelve a Unity (Assets > Refresh) para que recompile.' -ForegroundColor Green
