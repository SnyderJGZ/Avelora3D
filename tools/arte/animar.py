"""Anima un sprite con Wan 2.2 (5B, imagen a vídeo) en ComfyUI y lo convierte en una tira de fotogramas.

Uso (con el Python de ComfyUI y ComfyUI escuchando en 127.0.0.1:8188):
  python animar.py --sprite ../../client/Assets/Avelora/Resources/Characters/1.png \
                   --desc "warrior" --accion "walking" --salida guerrero_andar.png

Pasos: el sprite se pone sobre magenta y se amplía; Wan genera ~2 s de vídeo de la acción; se eligen
fotogramas espaciados, se quita el magenta y se recortan todos con la misma caja (el personaje no
«baila» dentro de la casilla). Sale una tira horizontal de fotogramas de 64 px y un GIF para revisarla.
"""
import argparse
import json
import os
import sys
import time
import urllib.parse
import urllib.request
import uuid
from io import BytesIO

from PIL import Image

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import iconos  # noqa: E402

LADO = 512
FOTOGRAMAS_VIDEO = 33  # Wan pide 4n + 1
NEGATIVO = ("camera movement, zoom, panning, background change, blurry, deformed, extra limbs, "
            "text, watermark, realistic, 3d render")


def subir(imagen):
    """Sube una imagen a ComfyUI (/upload/image) y devuelve el nombre con que la guarda."""
    frontera = uuid.uuid4().hex
    datos = BytesIO()
    imagen.save(datos, format="PNG")
    cuerpo = (f"--{frontera}\r\nContent-Disposition: form-data; name=\"image\"; filename=\"avelora_anim.png\"\r\n"
              "Content-Type: image/png\r\n\r\n").encode() + datos.getvalue() + \
        f"\r\n--{frontera}\r\nContent-Disposition: form-data; name=\"overwrite\"\r\n\r\ntrue\r\n--{frontera}--\r\n".encode()
    req = urllib.request.Request(iconos.API + "/upload/image", data=cuerpo,
                                 headers={"Content-Type": f"multipart/form-data; boundary={frontera}"})
    return json.load(urllib.request.urlopen(req))["name"]


def flujo(nombre_imagen, prompt, semilla):
    return {
        "1": {"class_type": "UNETLoader",
              "inputs": {"unet_name": "wan2.2_ti2v_5B_fp16.safetensors", "weight_dtype": "default"}},
        "2": {"class_type": "CLIPLoader",
              "inputs": {"clip_name": "umt5_xxl_fp8_e4m3fn_scaled.safetensors", "type": "wan"}},
        "3": {"class_type": "VAELoader", "inputs": {"vae_name": "wan2.2_vae.safetensors"}},
        "4": {"class_type": "ModelSamplingSD3", "inputs": {"model": ["1", 0], "shift": 8.0}},
        "5": {"class_type": "CLIPTextEncode", "inputs": {"text": prompt, "clip": ["2", 0]}},
        "6": {"class_type": "CLIPTextEncode", "inputs": {"text": NEGATIVO, "clip": ["2", 0]}},
        "7": {"class_type": "LoadImage", "inputs": {"image": nombre_imagen}},
        "8": {"class_type": "Wan22ImageToVideoLatent",
              "inputs": {"vae": ["3", 0], "width": LADO, "height": LADO, "length": FOTOGRAMAS_VIDEO,
                         "batch_size": 1, "start_image": ["7", 0]}},
        "9": {"class_type": "KSampler",
              "inputs": {"model": ["4", 0], "seed": semilla, "steps": 20, "cfg": 5.0, "sampler_name": "uni_pc",
                         "scheduler": "simple", "positive": ["5", 0], "negative": ["6", 0],
                         "latent_image": ["8", 0], "denoise": 1.0}},
        "10": {"class_type": "VAEDecode", "inputs": {"samples": ["9", 0], "vae": ["3", 0]}},
        "11": {"class_type": "SaveImage", "inputs": {"images": ["10", 0], "filename_prefix": "avelora_anim"}},
    }


def generar(nombre_imagen, prompt, semilla):
    datos = json.dumps({"prompt": flujo(nombre_imagen, prompt, semilla)}).encode("utf-8")
    req = urllib.request.Request(iconos.API + "/prompt", data=datos, headers={"Content-Type": "application/json"})
    id_trabajo = json.load(urllib.request.urlopen(req))["prompt_id"]
    limite = time.time() + 1800
    while time.time() < limite:
        historia = json.load(urllib.request.urlopen(iconos.API + "/history/" + id_trabajo))
        if id_trabajo in historia:
            salida = historia[id_trabajo]
            if salida.get("status", {}).get("status_str") == "error":
                raise RuntimeError("ComfyUI falló: " + json.dumps(salida["status"])[:600])
            imagenes = salida["outputs"]["11"]["images"]
            return [Image.open(BytesIO(urllib.request.urlopen(iconos.API + "/view?" + urllib.parse.urlencode(i)).read()))
                    .convert("RGB") for i in imagenes]
        time.sleep(2)
    raise TimeoutError("Wan no terminó en 30 min")


def a_tira(fotogramas, cuantos):
    """Elige fotogramas espaciados, quita el fondo y los recorta todos con la misma caja."""
    paso = (len(fotogramas) - 1) / max(1, cuantos)
    elegidos = [iconos.quitar_magenta(fotogramas[round(i * paso)]) for i in range(cuantos)]
    cajas = [f.getchannel("A").point(lambda a: 255 if a > 32 else 0).getbbox() for f in elegidos]
    cajas = [c for c in cajas if c]
    caja = (min(c[0] for c in cajas), min(c[1] for c in cajas), max(c[2] for c in cajas), max(c[3] for c in cajas))
    lado = max(caja[2] - caja[0], caja[3] - caja[1])
    escala = (iconos.LADO_ICONO - 4) / lado
    tira = Image.new("RGBA", (iconos.LADO_ICONO * cuantos, iconos.LADO_ICONO), (0, 0, 0, 0))
    resultado = []
    for i, f in enumerate(elegidos):
        recorte = f.crop(caja)
        tam = (max(1, round(recorte.width * escala)), max(1, round(recorte.height * escala)))
        pequeno = recorte.resize(tam, Image.LANCZOS)
        pequeno.putalpha(pequeno.getchannel("A").point(lambda a: 255 if a >= 128 else 0))
        celda = Image.new("RGBA", (iconos.LADO_ICONO, iconos.LADO_ICONO), (0, 0, 0, 0))
        # Pies abajo y centrado en horizontal: la misma posición en todos los fotogramas.
        celda.paste(pequeno, ((iconos.LADO_ICONO - tam[0]) // 2, iconos.LADO_ICONO - 2 - tam[1]), pequeno)
        tira.paste(celda, (i * iconos.LADO_ICONO, 0))
        resultado.append(celda)
    return tira, resultado


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--sprite", required=True)
    parser.add_argument("--desc", required=True, help="qué es, en inglés (p. ej. 'warrior')")
    parser.add_argument("--accion", default="walking", help="en inglés: walking, attacking with the sword, idle breathing…")
    parser.add_argument("--movimiento", default="legs stepping up and down alternately, arms swinging",
                        help="qué se mueve, en inglés")
    parser.add_argument("--salida", required=True)
    parser.add_argument("--fotogramas", type=int, default=8)
    parser.add_argument("--semilla", type=int, default=7)
    args = parser.parse_args()

    sprite = Image.open(args.sprite).convert("RGBA")
    # Ampliado sin suavizar (el modelo ve los píxeles grandes y conserva el estilo) y con margen alrededor:
    # si el personaje llena el cuadro, el modelo apenas lo mueve.
    lado_sprite = int(LADO * 0.7)
    grande = sprite.resize((lado_sprite, lado_sprite), Image.NEAREST)
    entrada = Image.new("RGBA", (LADO, LADO), (255, 0, 255, 255))
    entrada.alpha_composite(grande, ((LADO - lado_sprite) // 2, (LADO - lado_sprite) // 2))
    entrada = entrada.convert("RGB")
    prompt = (f"pixel art {args.desc} {args.accion} in place with big clearly visible movement: {args.movimiento}. "
              "Game sprite animation loop, the character stays centered and facing the viewer, static camera, "
              "flat solid magenta background, crisp pixel art")
    inicio = time.time()
    fotogramas = generar(subir(entrada), prompt, args.semilla)
    tira, celdas = a_tira(fotogramas, args.fotogramas)
    tira.save(args.salida)
    gif = [c.resize((256, 256), Image.NEAREST) for c in celdas]
    gif[0].save(os.path.splitext(args.salida)[0] + ".gif", save_all=True, append_images=gif[1:], duration=120,
                loop=0, disposal=2)
    print(f"{len(fotogramas)} fotogramas de vídeo -> {args.fotogramas} en {time.time() - inicio:.0f} s: {args.salida}")


if __name__ == "__main__":
    main()
