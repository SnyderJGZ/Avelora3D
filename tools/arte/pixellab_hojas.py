"""Convierte el ZIP de un personaje de PixelLab en las hojas que usa el cliente.

Uso (cualquier Python con Pillow, p. ej. el de ComfyUI):
  python pixellab_hojas.py --zip guerrero.zip --clase 1

El ZIP es el de get_character(...) -> «download». Salen en Resources/Characters:
  <clase>_quieto.png  1 columna  (las rotaciones)
  <clase>_<animación>.png  N columnas (los fotogramas de cada animación, p. ej. «andar»)
Filas de arriba abajo: sur, norte, oeste, este (= abajo, arriba, izquierda, derecha de RemotePlayerView).
"""
import argparse
import os
import zipfile
from io import BytesIO

from PIL import Image

FILAS = ["south", "north", "west", "east"]
SALIDA = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "..",
                      "client", "Assets", "Avelora", "Resources", "Characters")


def hoja(zf, rutas_por_fila):
    """rutas_por_fila: 4 listas de rutas dentro del ZIP, una por fila, mismo número de fotogramas."""
    imagenes = [[Image.open(BytesIO(zf.read(r))).convert("RGBA") for r in fila] for fila in rutas_por_fila]
    columnas = len(imagenes[0])
    if any(len(fila) != columnas for fila in imagenes):
        raise ValueError("las direcciones no tienen el mismo número de fotogramas")
    ancho, alto = imagenes[0][0].size
    resultado = Image.new("RGBA", (ancho * columnas, alto * 4), (0, 0, 0, 0))
    for f, fila in enumerate(imagenes):
        for c, img in enumerate(fila):
            resultado.paste(img, (c * ancho, f * alto))
    return resultado


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--zip", required=True)
    parser.add_argument("--clase", required=True, type=int)
    args = parser.parse_args()
    os.makedirs(SALIDA, exist_ok=True)
    with zipfile.ZipFile(args.zip) as zf:
        nombres = zf.namelist()
        # El primer estado del grupo (normalmente «Idle») es el personaje base.
        prefijo = sorted({n.split("/")[0] for n in nombres if "/rotations/" in n})[0]
        quieto = hoja(zf, [[f"{prefijo}/rotations/{d}.png"] for d in FILAS])
        quieto.save(os.path.join(SALIDA, f"{args.clase}_quieto.png"))
        print("quieto:", quieto.size)
        animaciones = sorted({n.split("/")[2] for n in nombres if n.startswith(prefijo + "/animations/")})
        for anim in animaciones:
            filas = [sorted(n for n in nombres if n.startswith(f"{prefijo}/animations/{anim}/{d}/")) for d in FILAS]
            if any(not f for f in filas):
                print(f"{anim}: faltan direcciones; se salta")
                continue
            img = hoja(zf, filas)
            img.save(os.path.join(SALIDA, f"{args.clase}_{anim}.png"))
            print(f"{anim}:", img.size)


if __name__ == "__main__":
    main()
