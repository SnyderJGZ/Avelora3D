using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace Avelora.EditorTools
{
    /// <summary>Separa los tilesets «Mapa v2» en piezas con transparencia y crea sus catálogos.</summary>
    public static class MapaV2Trocear
    {
        private static readonly string[] Fuentes = { "Ciudad.png", "afueras.png" };
        private const string CarpetaOrigen = "Assets/Avelora/Art/Mapa v2";
        private const string CarpetaSalida = "Assets/Avelora/Art/Mapa v2/Generado";
        private const int Casilla = 64;
        private const int MargenSuelo = 4;
        private const int MargenAgua = 8;
        private const int UmbralNegroBajo = 16;
        private const int UmbralNegroAlto = 48;
        private const int DilatacionPx = 0;
        private const int AreaMinima = 120;
        private const int LadoMinimo = 10;
        private const float CeldaPorDefecto = 75f;
        private const int MinCandidatosCelda = 5;
        private const int ColumnasHoja = 12;
        private const int TamanoCeldaHoja = 144;
        // Origen y caja de Afueras_objeto_005 (catálogo: 1021,5; 118×97). La parte baja es agua.
        private const int AguaPiezaX = 1021;
        private const int AguaPiezaY = 5;
        private const int AguaPiezaAncho = 118;
        private const int AguaPiezaAlto = 97;
        // Dos ventanas de agua interiores separadas para variar el patrón, respetando 8 px de margen.
        private const int AguaInteriorX1 = 1037;
        private const int AguaInteriorX2 = 1063;
        private const int AguaInteriorY = 30;
        // Recorte del borde superior hierba/agua de la misma pieza para una transición suave.
        private const int AguaOrillaX = 1048;
        private const int AguaOrillaY = 5;

        [Serializable]
        private sealed class Catalogo
        {
            public string fuente;
            public int casilla;
            public float celdaOrigen;
            public List<PiezaCatalogo> piezas;
        }

        [Serializable]
        private sealed class PiezaCatalogo
        {
            public int numero;
            public string archivo;
            public string categoria;
            public int casillasAncho;
            public int casillasAlto;
            public OrigenCaja origen;
        }

        [Serializable]
        private sealed class OrigenCaja
        {
            public int x;
            public int y;
            public int ancho;
            public int alto;
        }

        private sealed class Componente
        {
            public int etiqueta;
            public int area;
            public int x0 = int.MaxValue;
            public int y0 = int.MaxValue;
            public int x1 = int.MinValue;
            public int y1 = int.MinValue;

            public int Ancho { get { return x1 - x0 + 1; } }
            public int Alto { get { return y1 - y0 + 1; } }
        }

        private sealed class Candidato
        {
            public Componente componente;
            public bool suelo;
            public bool recorteAgua;
            public bool voltear;
            public int casillasAncho;
            public int casillasAlto;
            public int numero;
            public string archivo;
            public string categoria;
        }

        [MenuItem("Avelora/Trocear arte «Mapa v2»")]
        public static void Trocear()
        {
            string carpetaSalidaFisica = RutaFisicaAssets(CarpetaSalida);
            Directory.CreateDirectory(carpetaSalidaFisica);

            foreach (string fuente in Fuentes)
            {
                TrocearFuente(fuente, carpetaSalidaFisica);
            }

            AssetDatabase.Refresh();
            ConfigurarImportadores();
        }

        [MenuItem("Avelora/Verificar piezas «Mapa v2»")]
        public static void Verificar()
        {
            string carpetaFisica = RutaFisicaAssets(CarpetaSalida);
            int piezasCorrectas = 0;
            int errores = 0;
            int catalogosLeidos = 0;

            if (!Directory.Exists(carpetaFisica))
            {
                Debug.LogError("No existe la carpeta de piezas «Mapa v2»: " + CarpetaSalida);
                Debug.Log("0 piezas OK, 1 errores");
                return;
            }

            foreach (string rutaCatalogo in Directory.GetFiles(carpetaFisica, "*_catalogo.json"))
            {
                catalogosLeidos++;
                Catalogo catalogo = null;
                try
                {
                    catalogo = JsonUtility.FromJson<Catalogo>(File.ReadAllText(rutaCatalogo));
                }
                catch (Exception excepcion)
                {
                    RegistrarError(ref errores, "No se pudo leer " + Path.GetFileName(rutaCatalogo) + ": " + excepcion.Message);
                    continue;
                }

                if (catalogo == null || catalogo.piezas == null)
                {
                    RegistrarError(ref errores, "Catálogo inválido: " + Path.GetFileName(rutaCatalogo));
                    continue;
                }

                foreach (PiezaCatalogo pieza in catalogo.piezas)
                {
                    int erroresAntes = errores;
                    VerificarPieza(pieza, carpetaFisica, ref errores);
                    if (errores == erroresAntes)
                    {
                        piezasCorrectas++;
                    }
                }
            }

            if (catalogosLeidos == 0)
            {
                RegistrarError(ref errores, "No se encontraron catálogos en " + CarpetaSalida);
            }

            Debug.Log(piezasCorrectas + " piezas OK, " + errores + " errores");
        }

        private static void TrocearFuente(string fuente, string carpetaSalidaFisica)
        {
            string rutaFuente = Path.Combine(RutaFisicaAssets(CarpetaOrigen), fuente);
            if (!File.Exists(rutaFuente))
            {
                Debug.LogError("No se encontró la fuente: " + rutaFuente);
                return;
            }

            var texturaFuente = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!ImageConversion.LoadImage(texturaFuente, File.ReadAllBytes(rutaFuente), false))
            {
                UnityEngine.Object.DestroyImmediate(texturaFuente);
                Debug.LogError("No se pudo decodificar la fuente: " + fuente);
                return;
            }

            int ancho = texturaFuente.width;
            int alto = texturaFuente.height;
            Color32[] pixeles = texturaFuente.GetPixels32();
            UnityEngine.Object.DestroyImmediate(texturaFuente);

            byte[] alfa = new byte[ancho * alto];
            bool[] mascara = new bool[ancho * alto];
            for (int y = 0; y < alto; y++)
            {
                int fila = (alto - 1 - y) * ancho;
                for (int x = 0; x < ancho; x++)
                {
                    int indice = fila + x;
                    Color32 color = pixeles[indice];
                    int maximo = Math.Max(color.r, Math.Max(color.g, color.b));
                    float normalizado = Mathf.Clamp01((maximo - UmbralNegroBajo) /
                        (float)(UmbralNegroAlto - UmbralNegroBajo));
                    alfa[indice] = (byte)Mathf.RoundToInt(normalizado * 255f);
                    mascara[indice] = alfa[indice] >= 128;
                }
            }

            bool[] mascaraComponentes = DilatacionPx > 0
                ? Dilatar(mascara, ancho, alto, DilatacionPx)
                : mascara;
            int[] etiquetas;
            List<Componente> componentes = Etiquetar(mascaraComponentes, mascara, ancho, alto, out etiquetas);
            var validos = new List<Componente>(componentes.Count);
            int descartados = 0;
            for (int i = 0; i < componentes.Count; i++)
            {
                Componente componente = componentes[i];
                if (componente.area < AreaMinima || componente.Ancho < LadoMinimo || componente.Alto < LadoMinimo)
                {
                    descartados++;
                }
                else
                {
                    validos.Add(componente);
                }
            }

            float celda = MedirCelda(componentes);
            var suelos = new List<Candidato>();
            var objetos = new List<Candidato>();
            for (int i = 0; i < validos.Count; i++)
            {
                Componente componente = validos[i];
                float proporcionAncho = componente.Ancho / celda;
                float proporcionAlto = componente.Alto / celda;
                bool suelo = proporcionAncho >= 0.8f && proporcionAncho <= 1.2f &&
                    proporcionAlto >= 0.8f && proporcionAlto <= 1.2f;
                int casillasAncho = suelo ? 1 : Math.Max(1, Mathf.RoundToInt(componente.Ancho / celda));
                int casillasAlto = suelo ? 1 : Math.Max(1, Mathf.RoundToInt(componente.Alto / celda));
                var candidato = new Candidato
                {
                    componente = componente,
                    suelo = suelo,
                    casillasAncho = casillasAncho,
                    casillasAlto = casillasAlto
                };

                if (suelo)
                {
                    suelos.Add(candidato);
                }
                else
                {
                    objetos.Add(candidato);
                    if (componente.x0 == 0 || componente.y0 == 0 || componente.x1 == ancho - 1 || componente.y1 == alto - 1)
                    {
                        Debug.LogWarning(fuente + ": posible objeto cortado en la caja (" + componente.x0 + "," +
                            componente.y0 + ")–(" + componente.x1 + "," + componente.y1 + ").");
                    }
                }
            }

            CompararLectura(suelos, celda);
            CompararLectura(objetos, celda);
            string prefijo = Prefijo(fuente);
            var piezas = new List<Candidato>(suelos.Count + objetos.Count);
            int numeroGlobal = 0;
            AsignarNombres(suelos, prefijo, "suelo", piezas, ref numeroGlobal);
            AsignarNombres(objetos, prefijo, "objeto", piezas, ref numeroGlobal);
            if (prefijo == "Afueras")
            {
                AgregarRecorteAgua(piezas, ref numeroGlobal, "Afueras_agua_001.png", AguaInteriorX1, AguaInteriorY, true);
                AgregarRecorteAgua(piezas, ref numeroGlobal, "Afueras_agua_002.png", AguaInteriorX2, AguaInteriorY, true);
                AgregarRecorteAgua(piezas, ref numeroGlobal, "Afueras_orilla_001.png", AguaOrillaX, AguaOrillaY, false);
                // La misma orilla volteada: hierba abajo, para el borde inferior de ríos y lagos.
                AgregarRecorteAgua(piezas, ref numeroGlobal, "Afueras_orilla_002.png", AguaOrillaX, AguaOrillaY, false);
                piezas[piezas.Count - 1].voltear = true;
            }
            string archivoHoja = "_hoja_" + prefijo + ".png";
            BorrarPngObsoletos(carpetaSalidaFisica, prefijo, piezas, archivoHoja);

            var catalogo = new Catalogo
            {
                fuente = fuente,
                casilla = Casilla,
                celdaOrigen = celda,
                piezas = new List<PiezaCatalogo>(piezas.Count)
            };
            for (int i = 0; i < piezas.Count; i++)
            {
                Candidato pieza = piezas[i];
                Componente componente = pieza.componente;
                if (pieza.suelo && (componente.Ancho < 40 || componente.Alto < 40))
                {
                    Debug.LogWarning(fuente + ": suelo " + pieza.archivo + " mide menos de 40 px; no se recorta el margen.");
                }
                int salidaAncho = pieza.suelo ? Casilla : Casilla * pieza.casillasAncho;
                int salidaAlto = pieza.suelo ? Casilla : Casilla * pieza.casillasAlto;
                Color32[] pixelesPieza = Renderizar(pieza, pixeles, alfa, etiquetas, ancho, alto, salidaAncho, salidaAlto);
                EscribirPng(Path.Combine(carpetaSalidaFisica, pieza.archivo), salidaAncho, salidaAlto, pixelesPieza);
                catalogo.piezas.Add(new PiezaCatalogo
                {
                    numero = pieza.numero,
                    archivo = pieza.archivo,
                    categoria = string.IsNullOrEmpty(pieza.categoria)
                        ? (pieza.suelo ? "suelo" : "objeto") : pieza.categoria,
                    casillasAncho = pieza.casillasAncho,
                    casillasAlto = pieza.casillasAlto,
                    origen = new OrigenCaja
                    {
                        x = componente.x0,
                        y = componente.y0,
                        ancho = componente.Ancho,
                        alto = componente.Alto
                    }
                });
            }

            string rutaCatalogo = Path.Combine(carpetaSalidaFisica, prefijo + "_catalogo.json");
            File.WriteAllText(rutaCatalogo, JsonUtility.ToJson(catalogo, true));
            EscribirHoja(Path.Combine(carpetaSalidaFisica, archivoHoja), piezas, carpetaSalidaFisica);

            Debug.Log(fuente + ": celda medida " + celda.ToString("0.0") + " px; " + suelos.Count +
                " suelos, " + objetos.Count + " objetos, " + descartados + " descartados.");
        }

        private static bool[] Dilatar(bool[] mascara, int ancho, int alto, int radio)
        {
            var salida = new bool[mascara.Length];
            for (int y = 0; y < alto; y++)
            {
                for (int x = 0; x < ancho; x++)
                {
                    if (!mascara[y * ancho + x])
                    {
                        continue;
                    }

                    int y0 = Math.Max(0, y - radio);
                    int y1 = Math.Min(alto - 1, y + radio);
                    int x0 = Math.Max(0, x - radio);
                    int x1 = Math.Min(ancho - 1, x + radio);
                    for (int dy = y0; dy <= y1; dy++)
                    {
                        int fila = dy * ancho;
                        for (int dx = x0; dx <= x1; dx++)
                        {
                            salida[fila + dx] = true;
                        }
                    }
                }
            }

            return salida;
        }

        private static List<Componente> Etiquetar(bool[] mascaraComponentes, bool[] mascara,
            int ancho, int alto, out int[] etiquetas)
        {
            etiquetas = new int[mascara.Length];
            var cola = new int[mascara.Length];
            var componentes = new List<Componente>();
            int siguienteEtiqueta = 0;

            for (int inicio = 0; inicio < mascaraComponentes.Length; inicio++)
            {
                if (!mascaraComponentes[inicio] || etiquetas[inicio] != 0)
                {
                    continue;
                }

                siguienteEtiqueta++;
                componentes.Add(new Componente { etiqueta = siguienteEtiqueta });
                int cabeza = 0;
                int colaFinal = 0;
                cola[colaFinal++] = inicio;
                etiquetas[inicio] = siguienteEtiqueta;
                while (cabeza < colaFinal)
                {
                    int actual = cola[cabeza++];
                    int x = actual % ancho;
                    int y = actual / ancho;
                    if (x > 0)
                    {
                        Encolar(actual - 1, siguienteEtiqueta, mascaraComponentes, etiquetas, cola, ref colaFinal);
                    }
                    if (x + 1 < ancho)
                    {
                        Encolar(actual + 1, siguienteEtiqueta, mascaraComponentes, etiquetas, cola, ref colaFinal);
                    }
                    if (y > 0)
                    {
                        Encolar(actual - ancho, siguienteEtiqueta, mascaraComponentes, etiquetas, cola, ref colaFinal);
                    }
                    if (y + 1 < alto)
                    {
                        Encolar(actual + ancho, siguienteEtiqueta, mascaraComponentes, etiquetas, cola, ref colaFinal);
                    }
                }
            }

            for (int i = 0; i < mascara.Length; i++)
            {
                if (!mascara[i])
                {
                    continue;
                }

                int etiqueta = etiquetas[i];
                if (etiqueta <= 0)
                {
                    continue;
                }

                Componente componente = componentes[etiqueta - 1];

                int x = i % ancho;
                // GetPixels32 va de abajo arriba; las cajas se guardan de arriba abajo, como las lee Renderizar.
                int y = alto - 1 - i / ancho;
                componente.area++;
                componente.x0 = Math.Min(componente.x0, x);
                componente.y0 = Math.Min(componente.y0, y);
                componente.x1 = Math.Max(componente.x1, x);
                componente.y1 = Math.Max(componente.y1, y);
            }

            return componentes;
        }

        private static void Encolar(int indice, int etiqueta, bool[] mascara, int[] etiquetas,
            int[] cola, ref int colaFinal)
        {
            if (mascara[indice] && etiquetas[indice] == 0)
            {
                etiquetas[indice] = etiqueta;
                cola[colaFinal++] = indice;
            }
        }

        private static float MedirCelda(List<Componente> componentes)
        {
            var candidatos = new List<float>();
            for (int i = 0; i < componentes.Count; i++)
            {
                Componente componente = componentes[i];
                if (componente.Ancho >= 60 && componente.Ancho <= 95 &&
                    componente.Alto >= 60 && componente.Alto <= 95)
                {
                    candidatos.Add((componente.Ancho + componente.Alto) / 2f);
                }
            }

            if (candidatos.Count < MinCandidatosCelda)
            {
                return CeldaPorDefecto;
            }

            float[] valores = candidatos.ToArray();
            Array.Sort(valores);
            int medio = valores.Length / 2;
            return valores.Length % 2 == 1 ? valores[medio] : (valores[medio - 1] + valores[medio]) / 2f;
        }

        private static void CompararLectura(List<Candidato> candidatos, float celda)
        {
            candidatos.Sort(delegate(Candidato a, Candidato b)
            {
                int filaA = Mathf.RoundToInt(a.componente.y0 / celda);
                int filaB = Mathf.RoundToInt(b.componente.y0 / celda);
                int porFila = filaA.CompareTo(filaB);
                if (porFila != 0)
                {
                    return porFila;
                }

                int porColumna = a.componente.x0.CompareTo(b.componente.x0);
                return porColumna != 0 ? porColumna : a.componente.etiqueta.CompareTo(b.componente.etiqueta);
            });
        }

        private static void AsignarNombres(List<Candidato> categoria, string prefijo, string nombreCategoria,
            List<Candidato> destino, ref int numeroGlobal)
        {
            for (int i = 0; i < categoria.Count; i++)
            {
                Candidato candidato = categoria[i];
                numeroGlobal++;
                candidato.numero = numeroGlobal;
                candidato.archivo = prefijo + "_" + nombreCategoria + "_" + (i + 1).ToString("D3") + ".png";
                destino.Add(candidato);
            }
        }

        private static void AgregarRecorteAgua(List<Candidato> piezas, ref int numeroGlobal,
            string archivo, int x, int y, bool requiereMargen)
        {
            if (requiereMargen && (x < AguaPiezaX + MargenAgua || y < AguaPiezaY + MargenAgua ||
                x + Casilla > AguaPiezaX + AguaPiezaAncho - MargenAgua ||
                y + Casilla > AguaPiezaY + AguaPiezaAlto - MargenAgua))
            {
                throw new InvalidOperationException("El recorte de agua no conserva el margen mínimo.");
            }

            piezas.Add(new Candidato
            {
                componente = new Componente
                {
                    x0 = x,
                    y0 = y,
                    x1 = x + Casilla - 1,
                    y1 = y + Casilla - 1
                },
                recorteAgua = true,
                casillasAncho = 1,
                casillasAlto = 1,
                numero = ++numeroGlobal,
                archivo = archivo,
                categoria = "agua"
            });
        }

        private static Color32[] Renderizar(Candidato candidato, Color32[] fuente, byte[] alfa, int[] etiquetas,
            int anchoFuente, int altoFuente, int anchoSalida, int altoSalida)
        {
            if (candidato.recorteAgua)
            {
                Color32[] agua = RecortarAgua(candidato.componente, fuente, alfa, anchoFuente, altoFuente);
                if (candidato.voltear) Array.Reverse(agua);
                // Reverse gira 180°; se deshace el espejo horizontal fila a fila para quedarse solo con el volteo vertical.
                if (candidato.voltear)
                    for (int fila = 0; fila < Casilla; fila++) Array.Reverse(agua, fila * Casilla, Casilla);
                return agua;
            }

            var salida = new Color32[anchoSalida * altoSalida];
            Componente componente = candidato.componente;
            bool recortarSuelo = candidato.suelo && componente.Ancho >= 40 && componente.Alto >= 40;
            int margen = recortarSuelo ? MargenSuelo : 0;
            int xMinimo = componente.x0 + margen;
            int xMaximo = componente.x1 + 1 - margen;
            int yMinimo = componente.y0 + margen;
            int yMaximo = componente.y1 + 1 - margen;
            float sx = candidato.suelo ? Casilla / (float)(componente.Ancho - 2 * margen) : 0f;
            float sy = candidato.suelo ? Casilla / (float)(componente.Alto - 2 * margen) : 0f;
            float escala = candidato.suelo ? 0f : Math.Min(anchoSalida / (float)componente.Ancho,
                altoSalida / (float)componente.Alto);
            float ox = candidato.suelo ? 0f : (anchoSalida - componente.Ancho * escala) / 2f;
            float oy = candidato.suelo ? 0f : altoSalida - componente.Alto * escala;

            for (int y = 0; y < altoSalida; y++)
            {
                for (int x = 0; x < anchoSalida; x++)
                {
                    if (!candidato.suelo &&
                        (x + 1 <= ox || x >= ox + componente.Ancho * escala ||
                         y + 1 <= oy || y >= oy + componente.Alto * escala))
                    {
                        continue;
                    }

                    float u0 = candidato.suelo ? xMinimo + x / sx : componente.x0 + (x - ox) / escala;
                    float u1 = candidato.suelo ? xMinimo + (x + 1) / sx : componente.x0 + (x + 1 - ox) / escala;
                    float v0 = candidato.suelo ? yMinimo + y / sy : componente.y0 + (y - oy) / escala;
                    float v1 = candidato.suelo ? yMinimo + (y + 1) / sy : componente.y0 + (y + 1 - oy) / escala;
                    u0 = Mathf.Clamp(u0, xMinimo, xMaximo);
                    u1 = Mathf.Clamp(u1, xMinimo, xMaximo);
                    v0 = Mathf.Clamp(v0, yMinimo, yMaximo);
                    v1 = Mathf.Clamp(v1, yMinimo, yMaximo);

                    int x0 = Mathf.Clamp(Mathf.FloorToInt(u0), componente.x0, componente.x1);
                    int x1 = Mathf.Clamp(Mathf.CeilToInt(u1) - 1, componente.x0, componente.x1);
                    int y0 = Mathf.Clamp(Mathf.FloorToInt(v0), componente.y0, componente.y1);
                    int y1 = Mathf.Clamp(Mathf.CeilToInt(v1) - 1, componente.y0, componente.y1);
                    if (u1 <= u0 || x1 < x0)
                    {
                        x0 = x1 = Mathf.Clamp(Mathf.FloorToInt(u0), componente.x0, componente.x1);
                    }
                    if (v1 <= v0 || y1 < y0)
                    {
                        y0 = y1 = Mathf.Clamp(Mathf.FloorToInt(v0), componente.y0, componente.y1);
                    }

                    long sumaR = 0;
                    long sumaG = 0;
                    long sumaB = 0;
                    long sumaAlfa = 0;
                    int cuenta = 0;
                    for (int syFuente = y0; syFuente <= y1; syFuente++)
                    {
                        int fila = (altoFuente - 1 - syFuente) * anchoFuente;
                        for (int sxFuente = x0; sxFuente <= x1; sxFuente++)
                        {
                            int indice = fila + sxFuente;
                            cuenta++;
                            if (etiquetas[indice] == componente.etiqueta)
                            {
                                byte peso = alfa[indice];
                                if (peso > 0)
                                {
                                    Color32 color = fuente[indice];
                                    sumaR += color.r * peso;
                                    sumaG += color.g * peso;
                                    sumaB += color.b * peso;
                                    sumaAlfa += peso;
                                }
                            }
                        }
                    }

                    Color32 resultado = sumaAlfa == 0
                        ? new Color32(0, 0, 0, 0)
                        : new Color32((byte)((sumaR + sumaAlfa / 2) / sumaAlfa),
                            (byte)((sumaG + sumaAlfa / 2) / sumaAlfa),
                            (byte)((sumaB + sumaAlfa / 2) / sumaAlfa),
                            (byte)Math.Min(255, (sumaAlfa + cuenta / 2) / cuenta));
                    salida[(altoSalida - 1 - y) * anchoSalida + x] = resultado;
                }
            }

            return salida;
        }

        private static Color32[] RecortarAgua(Componente recorte, Color32[] fuente, byte[] alfa,
            int anchoFuente, int altoFuente)
        {
            var salida = new Color32[Casilla * Casilla];
            for (int y = 0; y < Casilla; y++)
            {
                int filaFuente = (altoFuente - 1 - (recorte.y0 + y)) * anchoFuente;
                int filaSalida = (Casilla - 1 - y) * Casilla;
                for (int x = 0; x < Casilla; x++)
                {
                    int indiceFuente = filaFuente + recorte.x0 + x;
                    Color32 color = fuente[indiceFuente];
                    color.a = alfa[indiceFuente];
                    salida[filaSalida + x] = color;
                }
            }

            return salida;
        }

        private static void EscribirPng(string ruta, int ancho, int alto, Color32[] pixeles)
        {
            var textura = new Texture2D(ancho, alto, TextureFormat.RGBA32, false);
            textura.SetPixels32(pixeles);
            File.WriteAllBytes(ruta, textura.EncodeToPNG());
            UnityEngine.Object.DestroyImmediate(textura);
        }

        private static void EscribirHoja(string ruta, List<Candidato> piezas, string carpetaSalidaFisica)
        {
            int filas = Math.Max(1, (piezas.Count + ColumnasHoja - 1) / ColumnasHoja);
            int ancho = ColumnasHoja * TamanoCeldaHoja;
            int alto = filas * TamanoCeldaHoja;
            var pixeles = new Color32[ancho * alto];
            Color32 gris = new Color32(96, 96, 96, 255);
            for (int i = 0; i < pixeles.Length; i++)
            {
                pixeles[i] = gris;
            }

            for (int i = 0; i < piezas.Count; i++)
            {
                Candidato pieza = piezas[i];
                string rutaPieza = Path.Combine(carpetaSalidaFisica, pieza.archivo);
                var textura = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(textura, File.ReadAllBytes(rutaPieza), false))
                {
                    UnityEngine.Object.DestroyImmediate(textura);
                    continue;
                }

                int anchoPieza = textura.width;
                int altoPieza = textura.height;
                Color32[] pixelesPieza = textura.GetPixels32();
                UnityEngine.Object.DestroyImmediate(textura);
                float escala = Math.Min(2f, Math.Min(128f / anchoPieza, 128f / altoPieza));
                int anchoDibujo = Math.Max(1, Mathf.RoundToInt(anchoPieza * escala));
                int altoDibujo = Math.Max(1, Mathf.RoundToInt(altoPieza * escala));
                int celdaX = (i % ColumnasHoja) * TamanoCeldaHoja;
                int celdaY = (i / ColumnasHoja) * TamanoCeldaHoja;
                int dibujoX = celdaX + (TamanoCeldaHoja - anchoDibujo) / 2;
                int dibujoY = celdaY + (TamanoCeldaHoja - altoDibujo) / 2;
                for (int y = 0; y < altoDibujo; y++)
                {
                    int origenY = Mathf.Clamp(Mathf.FloorToInt(y * altoPieza / (float)altoDibujo), 0, altoPieza - 1);
                    for (int x = 0; x < anchoDibujo; x++)
                    {
                        int origenX = Mathf.Clamp(Mathf.FloorToInt(x * anchoPieza / (float)anchoDibujo), 0, anchoPieza - 1);
                        Color32 color = pixelesPieza[(altoPieza - 1 - origenY) * anchoPieza + origenX];
                        int alfa = color.a;
                        int inversoAlfa = 255 - alfa;
                        color = new Color32(
                            (byte)((color.r * alfa + 96 * inversoAlfa + 127) / 255),
                            (byte)((color.g * alfa + 96 * inversoAlfa + 127) / 255),
                            (byte)((color.b * alfa + 96 * inversoAlfa + 127) / 255),
                            255);
                        int destinoX = dibujoX + x;
                        int destinoY = dibujoY + y;
                        pixeles[(alto - 1 - destinoY) * ancho + destinoX] = color;
                    }
                }

                DibujarNumero(pixeles, ancho, alto, celdaX + 2, celdaY + 2, pieza.numero);
            }

            EscribirPng(ruta, ancho, alto, pixeles);
        }

        private static void DibujarNumero(Color32[] pixeles, int ancho, int alto, int x, int y, int numero)
        {
            string texto = numero.ToString("D3");
            int cajaAncho = 26;
            int cajaAlto = 14;
            for (int dy = 0; dy < cajaAlto; dy++)
            {
                for (int dx = 0; dx < cajaAncho; dx++)
                {
                    PonerDesdeArriba(pixeles, ancho, alto, x + dx, y + dy, new Color32(0, 0, 0, 255));
                }
            }

            for (int digito = 0; digito < texto.Length; digito++)
            {
                int[] patron = Digitos[texto[digito] - '0'];
                for (int fila = 0; fila < 5; fila++)
                {
                    for (int columna = 0; columna < 3; columna++)
                    {
                        if ((patron[fila] & (1 << (2 - columna))) == 0)
                        {
                            continue;
                        }

                        for (int sy = 0; sy < 2; sy++)
                        {
                            for (int sx = 0; sx < 2; sx++)
                            {
                                int px = x + 2 + digito * 8 + columna * 2 + sx;
                                int py = y + 2 + fila * 2 + sy;
                                PonerDesdeArriba(pixeles, ancho, alto, px, py, new Color32(255, 255, 255, 255));
                            }
                        }
                    }
                }
            }
        }

        private static readonly int[][] Digitos =
        {
            new[] { 7, 5, 5, 5, 7 }, new[] { 2, 6, 2, 2, 7 },
            new[] { 7, 1, 7, 4, 7 }, new[] { 7, 1, 7, 1, 7 },
            new[] { 5, 5, 7, 1, 1 }, new[] { 7, 4, 7, 1, 7 },
            new[] { 7, 4, 7, 5, 7 }, new[] { 7, 1, 1, 1, 1 },
            new[] { 7, 5, 7, 5, 7 }, new[] { 7, 5, 7, 1, 7 }
        };

        private static void PonerDesdeArriba(Color32[] pixeles, int ancho, int alto, int x, int y, Color32 color)
        {
            if (x >= 0 && x < ancho && y >= 0 && y < alto)
            {
                pixeles[(alto - 1 - y) * ancho + x] = color;
            }
        }

        private static void BorrarPngObsoletos(string carpeta, string prefijo,
            List<Candidato> piezas, string archivoHoja)
        {
            var conservados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            conservados.Add(archivoHoja);
            for (int i = 0; i < piezas.Count; i++)
            {
                conservados.Add(piezas[i].archivo);
            }

            foreach (string ruta in Directory.GetFiles(carpeta, prefijo + "_*.png"))
            {
                if (!conservados.Contains(Path.GetFileName(ruta)))
                {
                    File.Delete(ruta);
                }
            }
            string rutaHoja = Path.Combine(carpeta, archivoHoja);
            if (!conservados.Contains(Path.GetFileName(rutaHoja)) && File.Exists(rutaHoja))
            {
                File.Delete(rutaHoja);
            }
        }

        private static void ConfigurarImportadores()
        {
            string[] guids = AssetDatabase.FindAssets("t:Texture2D", new[] { CarpetaSalida });
            foreach (string guid in guids)
            {
                string ruta = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.Equals(Path.GetExtension(ruta), ".png", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var importer = AssetImporter.GetAtPath(ruta) as TextureImporter;
                if (importer == null)
                {
                    continue;
                }

                var settings = new TextureImporterSettings();
                importer.ReadTextureSettings(settings);
                Vector2 pivote = new Vector2(0.5f, 0.5f);
                bool changed = importer.textureType != TextureImporterType.Sprite ||
                    importer.spriteImportMode != SpriteImportMode.Single ||
                    !Mathf.Approximately(importer.spritePixelsPerUnit, Casilla) ||
                    importer.filterMode != FilterMode.Point ||
                    importer.textureCompression != TextureImporterCompression.Uncompressed ||
                    importer.mipmapEnabled || importer.wrapMode != TextureWrapMode.Clamp ||
                    settings.spriteAlignment != (int)SpriteAlignment.Custom || settings.spritePivot != pivote ||
                    settings.spriteMeshType != SpriteMeshType.FullRect;
                if (!changed)
                {
                    continue;
                }

                importer.textureType = TextureImporterType.Sprite;
                importer.spriteImportMode = SpriteImportMode.Single;
                importer.spritePixelsPerUnit = Casilla;
                importer.filterMode = FilterMode.Point;
                importer.textureCompression = TextureImporterCompression.Uncompressed;
                importer.mipmapEnabled = false;
                importer.wrapMode = TextureWrapMode.Clamp;
                importer.ReadTextureSettings(settings);
                settings.spriteAlignment = (int)SpriteAlignment.Custom;
                settings.spritePivot = pivote;
                settings.spriteMeshType = SpriteMeshType.FullRect;
                importer.SetTextureSettings(settings);
                importer.SaveAndReimport();
            }
        }

        private static void VerificarPieza(PiezaCatalogo pieza, string carpetaFisica, ref int errores)
        {
            if (pieza == null || string.IsNullOrEmpty(pieza.archivo))
            {
                RegistrarError(ref errores, "Pieza inválida en el catálogo (falta archivo).");
                return;
            }

            string rutaFisica = Path.Combine(carpetaFisica, pieza.archivo);
            if (!File.Exists(rutaFisica))
            {
                RegistrarError(ref errores, "No existe la pieza: " + pieza.archivo);
                return;
            }

            string rutaAsset = CarpetaSalida + "/" + pieza.archivo;
            var textura = AssetDatabase.LoadAssetAtPath<Texture2D>(rutaAsset);
            if (textura == null)
            {
                RegistrarError(ref errores, "Unity no pudo cargar la pieza: " + pieza.archivo);
            }
            else
            {
                int anchoEsperado = Casilla * pieza.casillasAncho;
                int altoEsperado = Casilla * pieza.casillasAlto;
                if (textura.width != anchoEsperado || textura.height != altoEsperado)
                {
                    RegistrarError(ref errores, pieza.archivo + ": tamaño " + textura.width + "×" + textura.height +
                        ", esperado " + anchoEsperado + "×" + altoEsperado + ".");
                }
                if (pieza.categoria == "suelo" && (textura.width != Casilla || textura.height != Casilla))
                {
                    RegistrarError(ref errores, pieza.archivo + ": un suelo debe medir 64×64.");
                }
            }

            var importer = AssetImporter.GetAtPath(rutaAsset) as TextureImporter;
            if (importer == null)
            {
                RegistrarError(ref errores, pieza.archivo + ": falta el importador de textura.");
                return;
            }
            if (importer.filterMode != FilterMode.Point)
            {
                RegistrarError(ref errores, pieza.archivo + ": filterMode no es Point.");
            }
            if (!Mathf.Approximately(importer.spritePixelsPerUnit, Casilla))
            {
                RegistrarError(ref errores, pieza.archivo + ": PPU no es 64.");
            }
            if (importer.textureCompression != TextureImporterCompression.Uncompressed)
            {
                RegistrarError(ref errores, pieza.archivo + ": la compresión no es Uncompressed.");
            }
        }

        private static void RegistrarError(ref int errores, string mensaje)
        {
            errores++;
            Debug.LogError(mensaje);
        }

        private static string Prefijo(string nombreFuente)
        {
            string nombre = Path.GetFileNameWithoutExtension(nombreFuente);
            return char.ToUpperInvariant(nombre[0]) + nombre.Substring(1);
        }

        private static string RutaFisicaAssets(string rutaAssets)
        {
            string relativa = rutaAssets.Substring("Assets/".Length).Replace('/', Path.DirectorySeparatorChar);
            return Path.Combine(Application.dataPath, relativa);
        }
    }
}
