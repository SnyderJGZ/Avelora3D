"""Genera iconos de objetos con ComfyUI (FLUX.2 Klein) y los deja listos para Unity.

Uso (con el Python de ComfyUI, que ya trae Pillow):
  python iconos.py                 # todos los objetos sin icono
  python iconos.py --ids 3102 3306 # solo esos
  python iconos.py --forzar        # rehace también los que ya existen

Necesita ComfyUI escuchando en 127.0.0.1:8188. Los prompts están en iconos.json (id -> descripción
en inglés). Cada icono se pide sobre fondo magenta, se le quita el fondo, se recorta y se reduce a 64 px.
"""
import argparse
import json
import os
import random
import time
import urllib.parse
import urllib.request
from io import BytesIO

from PIL import Image

RAIZ = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
API = "http://127.0.0.1:8188"
LADO_GENERADO = 512
LADO_ICONO = 64
ESTILO = (
    "pixel art game icon of {desc}, single object, centered, fills most of the frame, "
    "16-bit fantasy RPG inventory icon, crisp dark outline, rich shading, "
    "isolated on a flat solid pure magenta background (#FF00FF), no text, no border, no shadow on the background"
)
# Habilidades: icono cuadrado a sangre (sin fondo transparente), como los de las barras de hechizos.
ESTILO_HABILIDAD = (
    "pixel art square spell icon of {desc}, 16-bit fantasy RPG skill button icon, fills the whole square, "
    "dramatic lighting, dark vignette at the edges, no text, no letters, no frame"
)
# Personajes: cuerpo entero visto de frente y algo desde arriba, como los PNJ del mapa.
ESTILO_PERSONAJE = (
    "pixel art RPG character sprite of {desc}, full body standing, facing the viewer, top-down three-quarter view, "
    "chibi proportions, 16-bit style, crisp dark outline, centered, "
    "isolated on a flat solid pure magenta background (#FF00FF), no text, no shadow on the background"
)
RECURSOS = os.path.join(RAIZ, "client", "Assets", "Avelora", "Resources")
# tipo -> (prompts, carpeta de salida, estilo, se quita el fondo magenta)
TIPOS = {
    "objetos": ("iconos.json", os.path.join(RECURSOS, "GUI", "Items"), ESTILO, True),
    "habilidades": ("habilidades.json", os.path.join(RECURSOS, "GUI", "Skills"), ESTILO_HABILIDAD, False),
    "personajes": ("personajes.json", os.path.join(RECURSOS, "Characters"), ESTILO_PERSONAJE, True),
    "pnj": ("pnj.json", os.path.join(RECURSOS, "Npcs"), ESTILO_PERSONAJE, True),
}


def flujo(prompt, semilla, repetible=False):
    """Grafo mínimo de FLUX.2 Klein: texto -> latente -> imagen.

    Con repetible=True usa los nodos de ComfyUI-Universal-Seamless-Tiles (latente rodado y VAE circular):
    la imagen sale repetible de origen. Klein es de pocos pasos, así que se sube a 16 para cerrar la junta.
    """
    pasos = 16 if repetible else 4
    grafo = {
        "1": {"class_type": "UNETLoader",
              "inputs": {"unet_name": "flux-2-klein-4b-fp8.safetensors", "weight_dtype": "default"}},
        "2": {"class_type": "CLIPLoader",
              "inputs": {"clip_name": "qwen_3_4b_fp4_flux2.safetensors", "type": "flux2"}},
        "3": {"class_type": "VAELoader", "inputs": {"vae_name": "flux2-vae.safetensors"}},
        "4": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["2", 0]}},
        "5": {"class_type": "EmptyFlux2LatentImage",
              "inputs": {"width": LADO_GENERADO, "height": LADO_GENERADO, "batch_size": 1}},
        "6": {"class_type": "Flux2Scheduler",
              "inputs": {"steps": pasos, "width": LADO_GENERADO, "height": LADO_GENERADO}},
        "7": {"class_type": "KSamplerSelect", "inputs": {"sampler_name": "euler"}},
        "8": {"class_type": "RandomNoise", "inputs": {"noise_seed": semilla}},
        "9": {"class_type": "BasicGuider", "inputs": {"model": ["1", 0], "conditioning": ["4", 0]}},
        "10": {"class_type": "SamplerCustomAdvanced",
               "inputs": {"noise": ["8", 0], "guider": ["9", 0], "sampler": ["7", 0],
                          "sigmas": ["6", 0], "latent_image": ["5", 0]}},
        "11": {"class_type": "VAEDecode", "inputs": {"samples": ["10", 0], "vae": ["3", 0]}},
        "12": {"class_type": "SaveImage", "inputs": {"images": ["11", 0], "filename_prefix": "avelora_icono"}},
    }
    if repetible:
        grafo["13"] = {"class_type": "SeamlessTileModelDiT", "inputs": {"model": ["1", 0], "tiling": "enable", "seed": semilla}}
        grafo["14"] = {"class_type": "MakeCircularVAEDiT",
                       "inputs": {"vae": ["3", 0], "tiling": "enable", "copy_vae": "Make a copy"}}
        grafo["9"]["inputs"]["model"] = ["13", 0]
        grafo["11"]["inputs"]["vae"] = ["14", 0]
    return grafo


def pedir(prompt, semilla, repetible=False):
    datos = json.dumps({"prompt": flujo(prompt, semilla, repetible)}).encode("utf-8")
    req = urllib.request.Request(API + "/prompt", data=datos, headers={"Content-Type": "application/json"})
    id_trabajo = json.load(urllib.request.urlopen(req))["prompt_id"]
    limite = time.time() + 300
    while time.time() < limite:
        historia = json.load(urllib.request.urlopen(API + "/history/" + id_trabajo))
        if id_trabajo in historia:
            salida = historia[id_trabajo]
            if salida.get("status", {}).get("status_str") == "error":
                raise RuntimeError("ComfyUI falló: " + json.dumps(salida["status"])[:400])
            imagen = salida["outputs"]["12"]["images"][0]
            consulta = urllib.parse.urlencode(imagen)
            return Image.open(BytesIO(urllib.request.urlopen(API + "/view?" + consulta).read())).convert("RGB")
        time.sleep(0.5)
    raise TimeoutError("ComfyUI no terminó en 300 s")


def quitar_magenta(imagen):
    """Vuelve transparente el fondo magenta y limpia el halo rosa de los bordes."""
    rgba = imagen.convert("RGBA")
    px = rgba.load()
    for y in range(rgba.height):
        for x in range(rgba.width):
            r, g, b, _ = px[x, y]
            # Cuánto se parece a magenta: rojo y azul altos, verde bajo.
            magenta = min(r, b) - g
            if magenta > 90:
                px[x, y] = (0, 0, 0, 0)
            elif magenta > 40:
                # Borde mezclado: semitransparente y sin el tinte rosa.
                alfa = int(255 * (90 - magenta) / 50)
                px[x, y] = (min(r, g + 40), g, min(b, g + 40), alfa)
    return rgba


def a_icono(rgba):
    """Recorta al contenido, lo encaja en 60 px y lo centra en un lienzo de 64."""
    caja = rgba.getchannel("A").point(lambda a: 255 if a > 32 else 0).getbbox()
    if caja is None:
        raise ValueError("la imagen quedó vacía tras quitar el fondo")
    recorte = rgba.crop(caja)
    escala = (LADO_ICONO - 4) / max(recorte.width, recorte.height)
    tam = (max(1, round(recorte.width * escala)), max(1, round(recorte.height * escala)))
    pequeno = recorte.resize(tam, Image.LANCZOS)
    # Alfa binario: el pixel art no lleva bordes semitransparentes.
    pequeno.putalpha(pequeno.getchannel("A").point(lambda a: 255 if a >= 128 else 0))
    lienzo = Image.new("RGBA", (LADO_ICONO, LADO_ICONO), (0, 0, 0, 0))
    lienzo.paste(pequeno, ((LADO_ICONO - tam[0]) // 2, (LADO_ICONO - tam[1]) // 2), pequeno)
    return lienzo


def hoja(rutas, destino):
    """Hoja de contacto 4× para revisar los iconos de un vistazo."""
    lado = LADO_ICONO * 4
    columnas = 8
    filas = (len(rutas) + columnas - 1) // columnas
    hoja = Image.new("RGBA", (columnas * lado, filas * lado), (60, 60, 60, 255))
    for i, ruta in enumerate(rutas):
        icono = Image.open(ruta).resize((lado, lado), Image.NEAREST)
        hoja.paste(icono, ((i % columnas) * lado, (i // columnas) * lado), icono)
    hoja.save(destino)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--tipo", choices=sorted(TIPOS), default="objetos")
    parser.add_argument("--ids", nargs="*", type=int)
    parser.add_argument("--forzar", action="store_true")
    parser.add_argument("--semilla", type=int, default=2611)
    parser.add_argument("--hoja", default=os.path.join(os.path.dirname(__file__), "_hoja_iconos.png"))
    args = parser.parse_args()

    archivo_prompts, salida, estilo, quitar_fondo = TIPOS[args.tipo]
    with open(os.path.join(os.path.dirname(__file__), archivo_prompts), encoding="utf-8") as f:
        prompts = {int(k): v for k, v in json.load(f).items()}
    ids = args.ids or sorted(prompts)
    os.makedirs(salida, exist_ok=True)
    hechos = []
    for item_id in ids:
        if item_id not in prompts:
            print(f"{item_id}: sin descripción en {archivo_prompts}; se salta")
            continue
        ruta = os.path.join(salida, f"{item_id}.png")
        if os.path.exists(ruta) and not args.forzar:
            print(f"{item_id}: ya tiene icono; se salta (usa --forzar)")
            continue
        # Semilla fija por objeto: repetir el comando da el mismo icono.
        semilla = random.Random(args.semilla * 100003 + item_id).randrange(2 ** 32)
        inicio = time.time()
        imagen = pedir(estilo.format(desc=prompts[item_id]), semilla)
        if quitar_fondo:
            icono = a_icono(quitar_magenta(imagen))
        else:
            icono = imagen.resize((LADO_ICONO, LADO_ICONO), Image.LANCZOS).convert("RGBA")
        icono.save(ruta)
        hechos.append(ruta)
        print(f"{item_id}: listo en {time.time() - inicio:.1f} s")
    if hechos:
        hoja(hechos, args.hoja)
        print(f"hoja de revisión: {args.hoja}")


if __name__ == "__main__":
    main()
