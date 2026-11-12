"""Tileset nativo de terreno para la rejilla doble (dual grid) del mapa.

Uso (con el Python de ComfyUI y ComfyUI escuchando en 127.0.0.1:8188):
  python tileset.py                  # genera las texturas que falten y rehace los atlas
  python tileset.py --regenerar agua # vuelve a pedir esa textura a ComfyUI

Para cada terreno:
1. ComfyUI genera una textura de 512 px ya repetible (nodos de ComfyUI-Universal-Seamless-Tiles) y se reduce
   a 256 px, que cubren 4×4 casillas de 64 px. El empedrado, patrón regular, se genera por código.
2. Se componen las 16 combinaciones de esquinas (bit 8 = arriba izq., 4 = arriba dcha., 2 = abajo izq.,
   1 = abajo dcha.). El borde es una curva con ruido periódico de periodo 64 px: dos casillas vecinas
   comparten exactamente el mismo borde, así que el terreno empalma siempre.
3. Atlas por terreno: 16 columnas (variante = (x & 3) + 4 * (y & 3) de la casilla visual) × 16 filas (máscara).
   Fila 0 vacía. Unity lo trocea con MapaTerreno.cs.
"""
import argparse
import math
import os
import random
import sys

from PIL import Image

# El Python embebido de ComfyUI no añade la carpeta del script al path.
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconos  # noqa: E402

CARPETA = os.path.join(iconos.RAIZ, "client", "Assets", "Avelora", "Art", "Terreno")
FUENTES = os.path.join(os.path.dirname(__file__), "texturas")
CASILLA = 64
# Orden de abajo arriba: cada terreno se pinta encima de los anteriores.
# (nombre, descripción o None si se genera por código, brillo)
TERRENOS = [
    ("agua", "stylized bright blue water with big white wave highlights and ripples, cartoon pixel art", 1.0),
    ("tierra", "packed brown dirt ground with small pebbles and cracks", 1.0),
    ("hierba", "lush green grass lawn with small tufts and a few tiny flowers", 1.0),
    # El empedrado es un patrón regular: la IA no lo da repetible, así que se genera por código.
    ("empedrado", None, 1.0),
]
ESTILO = (
    "seamless tileable top-down pixel art ground texture of {desc}, 16-bit RPG tileset, "
    "uniform even pattern across the whole image, flat lighting, no objects, no shadows, no border, no text"
)


def empedrado_procedural(semilla, lado=128):
    """Adoquines por Voronoi periódico: repetible por construcción. Se dibuja a 128 px y se dobla a 256."""
    rnd = random.Random(semilla)
    puntos = []
    for gy in range(8):
        for gx in range(8):
            # Rejilla con jitter: piedras de tamaño parecido, sin huecos ni gigantes.
            puntos.append(((gx + rnd.uniform(0.2, 0.8)) * lado / 8, (gy + rnd.uniform(0.2, 0.8)) * lado / 8,
                           rnd.randint(118, 150)))
    imagen = Image.new("RGB", (lado, lado))
    px = imagen.load()
    for y in range(lado):
        for x in range(lado):
            d1 = d2 = 1e9
            cerca = None
            for (sx, sy, tono) in puntos:
                dx = (x - sx + lado / 2) % lado - lado / 2
                dy = (y - sy + lado / 2) % lado - lado / 2
                d = dx * dx + dy * dy
                if d < d1:
                    d2, d1, cerca = d1, d, (dx, dy, tono)
                elif d < d2:
                    d2 = d
            junta = math.sqrt(d2) - math.sqrt(d1)
            dx, dy, tono = cerca
            if junta < 1.6:
                px[x, y] = (58, 56, 62)
            else:
                # Luz arriba a la izquierda: el lado opuesto de cada piedra, más oscuro.
                luz = -(dx + dy) / 12.0
                borde = -18 if junta < 3.0 else 0
                g = max(70, min(200, int(tono + luz * 6 + borde)))
                px[x, y] = (g, g - 2, g + 4)
    return imagen.resize((lado * 2, lado * 2), Image.NEAREST)


def ruido_periodico(semilla):
    """Ruido suave de periodo CASILLA en x e y: igual en los dos lados de cada borde de casilla."""
    rnd = random.Random(semilla)
    # Frecuencias bajas: contorno ondulado, sin dientes de sierra.
    ondas = [(rnd.choice((0, 1)), rnd.choice((1, 2)), rnd.random() * 2 * math.pi, rnd.uniform(0.5, 1.0))
             for _ in range(2)] + [(rnd.choice((1, 2)), rnd.choice((0, 1)), rnd.random() * 2 * math.pi, rnd.uniform(0.5, 1.0))
                                   for _ in range(2)]
    total = sum(o[3] for o in ondas)

    def n(px, py):
        return sum(a * math.sin(2 * math.pi * (kx * px + ky * py) / CASILLA + f) for kx, ky, f, a in ondas) / total
    return n


def tesela(textura, variante, mascara, ruido):
    """Casilla de 64 px del terreno para una máscara de esquinas y una variante de textura."""
    cx, cy = variante % 4, variante // 4
    # En la imagen y crece hacia abajo; en Unity la casilla y + 1 está encima.
    recorte = textura.crop((cx * CASILLA, (3 - cy) * CASILLA, (cx + 1) * CASILLA, (4 - cy) * CASILLA)).convert("RGBA")
    if mascara == 15:
        return recorte
    tl, tr, bl, br = (mascara >> 3) & 1, (mascara >> 2) & 1, (mascara >> 1) & 1, mascara & 1
    px = recorte.load()
    for y in range(CASILLA):
        v = (y + 0.5) / CASILLA
        for x in range(CASILLA):
            u = (x + 0.5) / CASILLA
            arriba = tl * (1 - u) + tr * u
            abajo = bl * (1 - u) + br * u
            valor = arriba * (1 - v) + abajo * v + 0.16 * ruido(x, y)
            r, g, b, _ = px[x, y]
            if valor < 0.5:
                px[x, y] = (0, 0, 0, 0)
            elif valor < 0.56:
                # Contorno del terreno de encima: le da relieve sobre el de debajo.
                px[x, y] = (int(r * 0.55), int(g * 0.55), int(b * 0.55), 255)
    return recorte


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--regenerar", nargs="*", default=[])
    args = parser.parse_args()
    os.makedirs(CARPETA, exist_ok=True)
    os.makedirs(FUENTES, exist_ok=True)
    for indice, (nombre, desc, brillo) in enumerate(TERRENOS):
        fuente = os.path.join(FUENTES, nombre + ".png")
        if desc is None:
            textura = empedrado_procedural(31 + indice)
        elif nombre in args.regenerar or not os.path.exists(fuente):
            semilla = random.Random(9000 + indice).randrange(2 ** 32)
            iconos.pedir(ESTILO.format(desc=desc), semilla, repetible=True).save(fuente)
            print(f"{nombre}: textura generada")
        if desc is not None:
            # La textura ya sale repetible de ComfyUI (nodos Seamless): no hace falta mezclar bordes.
            textura = Image.open(fuente).convert("RGB").point(lambda c: min(255, int(c * brillo))).resize((4 * CASILLA, 4 * CASILLA), Image.LANCZOS)
        ruido = ruido_periodico(77 + indice)
        atlas = Image.new("RGBA", (16 * CASILLA, 16 * CASILLA), (0, 0, 0, 0))
        for mascara in range(1, 16):
            for variante in range(16):
                # Fila 0 arriba en la imagen = máscara 0; Unity trocea por nombre, no por orden.
                atlas.paste(tesela(textura, variante, mascara, ruido), (variante * CASILLA, mascara * CASILLA))
        atlas.save(os.path.join(CARPETA, f"Terreno_{indice}_{nombre}.png"))
        print(f"{nombre}: atlas listo")


if __name__ == "__main__":
    main()
