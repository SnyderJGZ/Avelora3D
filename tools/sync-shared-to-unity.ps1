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

    Ignora bin/ y obj/. Nunca borra nada: sobrescribe lo que exista.

    Unity recompila solo al volver al Editor (o con Assets > Refresh).

.PARAMETER ProyectoUnity
    Ruta de la carpeta del proyecto de Unity (la que contiene Assets/).

.EXAMPLE
    .\tools\sync-shared-to-unity.ps1 -ProyectoUnity 'E:\Unity\Nueva carpeta\My project (1)'
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$ProyectoUnity
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
            New-Item -ItemType Directory -Path (Split-Path -Parent $salida) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $salida -Force
            Write-Host ("  {0}" -f $relativo)

            $meta = $_.FullName + '.meta'
            if (Test-Path -LiteralPath $meta) {
                Copy-Item -LiteralPath $meta -Destination ($salida + '.meta') -Force
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

Write-Host ''
Write-Host 'Listo. Vuelve a Unity (Assets > Refresh) para que recompile.' -ForegroundColor Green
