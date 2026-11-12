using System;
using System.Collections.Generic;

namespace Avelora.EditorTools
{
    /// <summary>Diseño lógico determinista de la Ciudad; no depende de Unity.</summary>
    public static class MapaCiudadLayout
    {
        public const int Ancho = 160;
        public const int Alto = 120;
        public const int OrigenX = -80;
        public const int OrigenY = -60;
        private const int Semilla = 40404;

        public static readonly Punto Aparicion = new Punto(0.5f, -4.5f);
        public static readonly Rectangulo CiudadRect = new Rectangulo(-30, -22, 60, 44);
        public static readonly Rectangulo ArenaRect = new Rectangulo(44, 28, 14, 10);
        // Centro de cada hueco de cuatro casillas de la muralla.
        public static readonly Punto[] Puertas =
        {
            new Punto(0.5f, 21.5f), new Punto(0.5f, -21.5f),
            new Punto(29.5f, 0.5f), new Punto(-29.5f, 0.5f)
        };

        public static readonly Claro[] Claros =
        {
            new Claro(2001, -48.5f, 6.5f), new Claro(2001, -46.5f, -18.5f),
            new Claro(2001, -20.5f, 40.5f), new Claro(2001, 14.5f, -47.5f),
            new Claro(2001, 30.5f, 38.5f), new Claro(2001, -38.5f, -28.5f),
            new Claro(2002, 52.5f, -8.5f), new Claro(2002, 64.5f, -6.5f),
            new Claro(2002, 60.5f, 8.5f), new Claro(2002, 66.5f, 10.5f)
        };

        /// <summary>Tabla explícita de arte. Los números son los «numero» de los catálogos J-03.</summary>
        public static class Piezas
        {
            public static readonly Pieza[] Hierba =
            {
                new Pieza("Afueras_suelo_001.png", 1, 1, 1),
                new Pieza("Afueras_suelo_007.png", 1, 1, 7),
                new Pieza("Afueras_suelo_002.png", 1, 1, 2),
                new Pieza("Afueras_suelo_006.png", 1, 1, 6)
            };

            public static readonly Pieza[] Tierra =
            {
                new Pieza("Afueras_suelo_009.png", 1, 1, 9),
                new Pieza("Afueras_suelo_010.png", 1, 1, 10),
                new Pieza("Afueras_suelo_011.png", 1, 1, 11),
                new Pieza("Afueras_suelo_012.png", 1, 1, 12)
            };

            public static readonly Pieza[] Empedrado =
            {
                new Pieza("Ciudad_suelo_001.png", 1, 1, 1),
                new Pieza("Ciudad_suelo_002.png", 1, 1, 2)
            };

            public static readonly Pieza[] PlazaBase =
            {
                new Pieza("Ciudad_suelo_001.png", 1, 1, 1),
                new Pieza("Ciudad_suelo_002.png", 1, 1, 2)
            };

            public static readonly Pieza[] Acento =
            {
                new Pieza("Ciudad_suelo_005.png", 1, 1, 5),
                new Pieza("Ciudad_suelo_006.png", 1, 1, 6),
                new Pieza("Ciudad_suelo_007.png", 1, 1, 7),
                new Pieza("Ciudad_suelo_013.png", 1, 1, 13)
            };

            public static readonly Pieza AguaMar = new Pieza("Afueras_agua_001.png", 1, 1, 123);
            public static readonly Pieza AguaMar2 = new Pieza("Afueras_agua_002.png", 1, 1, 124);
            public static readonly Pieza OrillaAgua = new Pieza("Afueras_orilla_001.png", 1, 1, 125);
            public static readonly Pieza OrillaAguaAbajo = new Pieza("Afueras_orilla_002.png", 1, 1, 126);
            // «Water» solo marca internamente las casillas antes de pintar las orillas.
            public static readonly Pieza Agua = new Pieza("Water", 1, 1, 0);

            public static readonly Pieza[] Muro =
            {
                new Pieza("Afueras_objeto_032.png", 1, 2, 65),
                new Pieza("Afueras_objeto_033.png", 1, 3, 66)
            };
            public static readonly Pieza Puerta = new Pieza("Afueras_objeto_031.png", 3, 2, 64);
            public static readonly Pieza Fuente = new Pieza("Ciudad_objeto_087.png", 2, 2, 134);
            public static readonly Pieza[] Puesto =
            {
                new Pieza("Afueras_objeto_036.png", 2, 2, 69),
                new Pieza("Afueras_objeto_037.png", 2, 2, 70),
                new Pieza("Afueras_objeto_038.png", 2, 2, 71)
            };
            public static readonly Pieza[] CasaFachada =
            {
                new Pieza("Ciudad_objeto_015.png", 1, 1, 62),
                new Pieza("Ciudad_objeto_019.png", 1, 1, 66),
                new Pieza("Ciudad_objeto_023.png", 1, 1, 70),
                new Pieza("Ciudad_objeto_027.png", 1, 1, 74)
            };
            public static readonly Pieza CasaPuerta = new Pieza("Ciudad_objeto_031.png", 1, 1, 78);
            public static readonly Pieza[] CasaTejado =
            {
                new Pieza("Ciudad_objeto_052.png", 2, 1, 99),
                new Pieza("Ciudad_objeto_053.png", 2, 1, 100),
                new Pieza("Ciudad_objeto_054.png", 1, 1, 101),
                new Pieza("Ciudad_objeto_055.png", 1, 1, 102)
            };
            public static readonly Pieza[] Farola =
            {
                new Pieza("Afueras_objeto_048.png", 1, 1, 81),
                new Pieza("Afueras_objeto_049.png", 1, 1, 82)
            };
            public static readonly Pieza Estandarte = new Pieza("Afueras_objeto_051.png", 1, 1, 84);
            public static readonly Pieza[] Arbol =
            {
                new Pieza("Afueras_objeto_039.png", 2, 2, 72),
                new Pieza("Afueras_objeto_040.png", 1, 2, 73),
                new Pieza("Afueras_objeto_041.png", 1, 2, 74)
            };
            public static readonly Pieza Roca = new Pieza("Afueras_objeto_079.png", 1, 1, 112);
            public static readonly Pieza[] Valla =
            {
                new Pieza("Afueras_objeto_086.png", 2, 1, 119),
                new Pieza("Afueras_objeto_087.png", 2, 1, 120),
                new Pieza("Afueras_objeto_088.png", 1, 1, 121)
            };
            public static readonly Pieza[] Puente =
            {
                new Pieza("Afueras_objeto_083.png", 1, 2, 116),
                new Pieza("Afueras_objeto_084.png", 2, 2, 117),
                new Pieza("Afueras_objeto_085.png", 3, 2, 118)
            };
            public static readonly Pieza[] Granja =
            {
                new Pieza("Afueras_objeto_028.png", 1, 1, 61),
                new Pieza("Afueras_objeto_029.png", 2, 3, 62)
            };
            public static readonly Pieza[] Acantilado =
            {
                new Pieza("Afueras_objeto_001.png", 2, 1, 34),
                new Pieza("Afueras_objeto_002.png", 2, 1, 35)
            };
        }

        public sealed class Resultado
        {
            public int Ancho { get; private set; }
            public int Alto { get; private set; }
            public int OrigenX { get; private set; }
            public int OrigenY { get; private set; }
            public bool[] Bloqueado { get; private set; }
            public string[] Suelo { get; private set; }
            public string[] Camino { get; private set; }
            public List<Objeto> Objetos { get; private set; }
            public Claro[] Claros { get; private set; }
            public int CasillasAlcanzables { get; internal set; }

            internal Resultado()
            {
                Ancho = MapaCiudadLayout.Ancho;
                Alto = MapaCiudadLayout.Alto;
                OrigenX = MapaCiudadLayout.OrigenX;
                OrigenY = MapaCiudadLayout.OrigenY;
                Bloqueado = new bool[Ancho * Alto];
                Suelo = new string[Ancho * Alto];
                Camino = new string[Ancho * Alto];
                Objetos = new List<Objeto>();
                Claros = (Claro[])MapaCiudadLayout.Claros.Clone();
            }

            public int Indice(int x, int y)
            {
                if (x < OrigenX || x >= OrigenX + Ancho || y < OrigenY || y >= OrigenY + Alto)
                {
                    return -1;
                }

                return (y - OrigenY) * Ancho + (x - OrigenX);
            }

            public bool EstaBloqueado(int x, int y)
            {
                int i = Indice(x, y);
                return i < 0 || Bloqueado[i];
            }
        }

        public sealed class Objeto
        {
            public string Archivo { get; private set; }
            public int X { get; private set; }
            public int Y { get; private set; }
            public int Ancho { get; private set; }
            public int Alto { get; private set; }
            public bool Solido { get; private set; }
            public bool Rotada { get; private set; }
            public bool Tejado { get; private set; }
            public int AnchoSprite { get; private set; }
            public int AltoSprite { get; private set; }

            internal Objeto(Pieza pieza, int x, int y, bool solido, bool rotada, bool tejado)
            {
                Archivo = pieza.Archivo;
                X = x;
                Y = y;
                AnchoSprite = pieza.Ancho;
                AltoSprite = pieza.Alto;
                Ancho = rotada ? pieza.Alto : pieza.Ancho;
                Alto = rotada ? pieza.Ancho : pieza.Alto;
                Solido = solido;
                Rotada = rotada;
                Tejado = tejado;
            }
        }

        public struct Pieza
        {
            public readonly string Archivo;
            public readonly int Ancho;
            public readonly int Alto;
            public readonly int Numero;

            public Pieza(string archivo, int ancho, int alto, int numero)
            {
                Archivo = archivo;
                Ancho = ancho;
                Alto = alto;
                Numero = numero;
            }
        }

        public struct Punto
        {
            public readonly float X;
            public readonly float Y;

            public Punto(float x, float y) { X = x; Y = y; }
            public int CasillaX { get { return (int)Math.Floor(X); } }
            public int CasillaY { get { return (int)Math.Floor(Y); } }
        }

        public struct Rectangulo
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Ancho;
            public readonly int Alto;

            public Rectangulo(int x, int y, int ancho, int alto)
            {
                X = x; Y = y; Ancho = ancho; Alto = alto;
            }

            public int CentroX { get { return X + (Ancho / 2); } }
            public int CentroY { get { return Y + (Alto / 2); } }
        }

        public struct Claro
        {
            public readonly int TypeId;
            public readonly float X;
            public readonly float Y;

            public Claro(int typeId, float x, float y) { TypeId = typeId; X = x; Y = y; }
            public int CasillaX { get { return (int)Math.Floor(X); } }
            public int CasillaY { get { return (int)Math.Floor(Y); } }
        }

        public static Resultado Generar()
        {
            var resultado = new Resultado();
            var reservado = new bool[Ancho * Alto];
            var rng = new Random(Semilla);
            RellenarSuelos(resultado);
            CrearCaminos(resultado);
            CrearAguaYRelieve(resultado);
            CrearMurallaYArena(resultado);
            CrearClaros(resultado);
            ColocarMuralla(resultado, reservado);
            ColocarCiudad(resultado, reservado);
            ColocarGranjas(resultado, reservado);
            ColocarArena(resultado, reservado);
            ColocarPuentes(resultado, reservado);
            ColocarVegetacion(resultado, reservado, rng);
            AsegurarPasos(resultado);
            resultado.CasillasAlcanzables = BuscarAlcanzables(resultado);
            VerificarAccesos(resultado);
            return resultado;
        }

        private static void RellenarSuelos(Resultado r)
        {
            for (int y = OrigenY; y < OrigenY + Alto; y++)
            {
                for (int x = OrigenX; x < OrigenX + Ancho; x++)
                {
                    int i = r.Indice(x, y);
                    r.Suelo[i] = Seleccionar(Piezas.Hierba, Hash(x, y, 1));
                    if (DentroCiudad(x, y))
                    {
                        r.Suelo[i] = ElegirBase(Piezas.PlazaBase, Hash(x, y, 2));
                    }
                    else if (Hash(x, y, 2) < 0.12f)
                    {
                        r.Suelo[i] = Seleccionar(Piezas.Tierra, Hash(x, y, 3));
                    }
                }
            }
        }

        private static void CrearCaminos(Resultado r)
        {
            // Calles interiores de cuatro casillas, incluida la plaza.
            PintarRect(r, -2, -22, 1, 44, true);
            PintarRect(r, -30, -2, 60, 4, true);
            PintarRect(r, -8, -6, 16, 12, false, true);
            // La fuente ocupa el centro de la plaza; se conserva el cruce por sus dos lados.
            for (int y = -1; y <= 0; y++)
            {
                for (int x = -1; x <= 0; x++)
                {
                    int i = r.Indice(x, y);
                    r.Camino[i] = null!;
                    r.Suelo[i] = ElegirBase(Piezas.PlazaBase, Hash(x, y, 5));
                }
            }
            PintarAcentos(r);
            // Exterior: granjas al norte, bosque al oeste, puente sur, meseta/arena al este.
            PintarLinea(r, 0, 22, 0, 34, 3);
            PintarLinea(r, -31, 0, -51, 6, 3);
            PintarLinea(r, 30, 0, 44, 0, 3);
            PintarLinea(r, 44, 0, 42, 0, 1);
            PintarLinea(r, 42, 0, 42, 32, 1);
            PintarLinea(r, 42, 32, 47, 32, 1);
            PintarLinea(r, 0, -23, 0, -45, 3);
            PintarLinea(r, 43, 0, 43, -17, 1);
            PintarLinea(r, 43, -17, 58, -17, 1);
            PintarLinea(r, 58, -17, 58, -45, 1);
        }

        private static void CrearAguaYRelieve(Resultado r)
        {
            for (int x = OrigenX; x < OrigenX + Ancho; x++)
            {
                int centro = CentroRio(x);
                for (int dy = -1; dy <= 1; dy++)
                {
                    PonerAgua(r, x, centro + dy);
                }
            }

            for (int y = -54; y <= -38; y++)
            {
                for (int x = -70; x <= -50; x++)
                {
                    float dx = (x + 60) / 10f;
                    float dy = (y + 46) / 8f;
                    if ((dx * dx) + (dy * dy) <= 1f)
                    {
                        PonerAgua(r, x, y);
                    }
                }
            }

            PintarOrillasAgua(r);

            // Meseta: perímetro de acantilado de una casilla; rampa oeste abierta (y -2..1).
            for (int y = -14; y <= 16; y++)
            {
                if (y < -2 || y > 1) Bloquear(r, 44, y);
                Bloquear(r, 72, y);
            }
            for (int x = 44; x <= 72; x++)
            {
                Bloquear(r, x, -14);
                Bloquear(r, x, 16);
            }
        }

        private static void CrearMurallaYArena(Resultado r)
        {
            for (int y = CiudadRect.Y; y < CiudadRect.Y + CiudadRect.Alto; y++)
            {
                for (int x = CiudadRect.X; x < CiudadRect.X + CiudadRect.Ancho; x++)
                {
                    bool muro = x < CiudadRect.X + 2 || x >= CiudadRect.X + CiudadRect.Ancho - 2 ||
                        y < CiudadRect.Y + 2 || y >= CiudadRect.Y + CiudadRect.Alto - 2;
                    if (!muro) continue;
                    if (EsHuecoPuerta(x, y)) continue;
                    Bloquear(r, x, y);
                }
            }

            // Arena despejada, valla con hueco oeste y otro al sur.
            for (int x = ArenaRect.X - 1; x <= ArenaRect.X + ArenaRect.Ancho; x++)
            {
                if (x != ArenaRect.CentroX) { Bloquear(r, x, ArenaRect.Y - 1); }
                if (x != ArenaRect.CentroX) { Bloquear(r, x, ArenaRect.Y + ArenaRect.Alto); }
            }
            for (int y = ArenaRect.Y; y < ArenaRect.Y + ArenaRect.Alto; y++)
            {
                if (y < 31 || y > 32) Bloquear(r, ArenaRect.X - 1, y);
                Bloquear(r, ArenaRect.X + ArenaRect.Ancho, y);
            }

            PintarRect(r, ArenaRect.X, ArenaRect.Y, ArenaRect.Ancho, ArenaRect.Alto, true);
        }

        private static void CrearClaros(Resultado r)
        {
            foreach (Claro claro in Claros)
            {
                // Claro exacto 7×7; se reserva un margen adicional para evitar que se planten objetos.
                for (int y = claro.CasillaY - 3; y <= claro.CasillaY + 3; y++)
                {
                    for (int x = claro.CasillaX - 3; x <= claro.CasillaX + 3; x++)
                    {
                        int i = r.Indice(x, y);
                        if (i >= 0)
                        {
                            r.Bloqueado[i] = false;
                            r.Camino[i] = null!;
                            r.Suelo[i] = Seleccionar(Piezas.Hierba, Hash(x, y, claro.TypeId));
                        }
                    }
                }
            }
        }

        private static void ColocarMuralla(Resultado r, bool[] reservado)
        {
            // Cada módulo ocupa dos casillas; las aberturas coinciden con las cuatro puertas.
            Pieza tramo = Piezas.Muro[0];
            for (int x = -30; x <= -29; x++)
            {
                for (int y = -22; y <= -4; y += 2)
                    Añadir(r, reservado, tramo, x, y, false, true);
                for (int y = 2; y <= 20; y += 2)
                    Añadir(r, reservado, tramo, x, y, false, true);
            }
            for (int x = 28; x <= 29; x++)
            {
                for (int y = -22; y <= -4; y += 2)
                    Añadir(r, reservado, tramo, x, y, false, true);
                for (int y = 2; y <= 20; y += 2)
                    Añadir(r, reservado, tramo, x, y, false, true);
            }

            for (int x = -30; x <= -4; x += 2)
            {
                AñadirRotado(r, reservado, tramo, x, -22, false, true, false);
                AñadirRotado(r, reservado, tramo, x, -21, false, true, false);
            }
            for (int x = 2; x <= 26; x += 2)
            {
                AñadirRotado(r, reservado, tramo, x, -22, false, true, false);
                AñadirRotado(r, reservado, tramo, x, -21, false, true, false);
            }
            for (int x = -30; x <= -4; x += 2)
            {
                AñadirRotado(r, reservado, tramo, x, 20, false, true, false);
                AñadirRotado(r, reservado, tramo, x, 21, false, true, false);
            }
            for (int x = 2; x <= 26; x += 2)
            {
                AñadirRotado(r, reservado, tramo, x, 20, false, true, false);
                AñadirRotado(r, reservado, tramo, x, 21, false, true, false);
            }

            Añadir(r, reservado, Piezas.Puerta, -2, -22, false, true);
            Añadir(r, reservado, Piezas.Puerta, -2, 20, false, true);
            AñadirRotado(r, reservado, Piezas.Puerta, -30, -2, false, true, false);
            AñadirRotado(r, reservado, Piezas.Puerta, 28, -2, false, true, false);
        }

        private static void ColocarCiudad(Resultado r, bool[] reservado)
        {
            Añadir(r, reservado, Piezas.Fuente, -1, -1, false, false);

            // Cuatro manzanas contiguas, una por cuadrante; cada fila comparte fachada y tejado.
            ColocarCasa(r, reservado, -27, 5, 1, 0);
            ColocarCasa(r, reservado, -23, 5, 1, 0);
            ColocarCasa(r, reservado, -19, 5, 1, 0);
            ColocarCasa(r, reservado, -15, 5, 1, 0);
            ColocarCasa(r, reservado, 9, 3, 1, 1);
            ColocarCasa(r, reservado, 13, 3, 1, 1);
            ColocarCasa(r, reservado, 17, 3, 1, 1);
            ColocarCasa(r, reservado, 21, 3, 1, 1);
            ColocarCasa(r, reservado, -27, -15, -2, 2);
            ColocarCasa(r, reservado, -23, -15, -2, 2);
            ColocarCasa(r, reservado, -19, -15, -2, 2);
            ColocarCasa(r, reservado, -15, -15, -2, 2);
            ColocarCasa(r, reservado, 9, -15, -2, 3);
            ColocarCasa(r, reservado, 13, -15, -2, 3);
            ColocarCasa(r, reservado, 17, -15, -2, 3);
            ColocarCasa(r, reservado, 21, -15, -2, 3);

            // Mercado al este de la plaza, en dos filas con un pasillo de tres casillas.
            for (int fila = 0; fila < 2; fila++)
            {
                int y = 7 + fila * 5;
                for (int columna = 0; columna < 3; columna++)
                {
                    Añadir(r, reservado, Piezas.Puesto[columna], 9 + columna * 4, y, true, false);
                }
            }

            // Farolas aproximadamente cada seis casillas junto a las calles.
            for (int y = -19; y <= 17; y += 6)
            {
                int izquierda = -4;
                int derecha = 3;
                if (!CercaDeCasa(r, izquierda, y, 1, 1))
                    Añadir(r, reservado, Piezas.Farola[(y + 19) % Piezas.Farola.Length], izquierda, y, false, false);
                if (!CercaDeCasa(r, derecha, y, 1, 1))
                    Añadir(r, reservado, Piezas.Farola[(y + 20) % Piezas.Farola.Length], derecha, y, false, false);
            }
            for (int x = -26; x <= 26; x += 6)
            {
                if (!CercaDeCasa(r, x, -4, 1, 1))
                    Añadir(r, reservado, Piezas.Farola[(x + 26) % Piezas.Farola.Length], x, -4, false, false);
                if (!CercaDeCasa(r, x, 3, 1, 1))
                    Añadir(r, reservado, Piezas.Farola[(x + 27) % Piezas.Farola.Length], x, 3, false, false);
            }

            // Estandartes en la plaza y a ambos lados de cada puerta, sin tapar el paso.
            Añadir(r, reservado, Piezas.Estandarte, -6, -5, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 5, -5, false, false);
            Añadir(r, reservado, Piezas.Estandarte, -6, 4, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 5, 4, false, false);
            Añadir(r, reservado, Piezas.Estandarte, -4, 18, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 2, 18, false, false);
            Añadir(r, reservado, Piezas.Estandarte, -4, -20, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 2, -20, false, false);
            Añadir(r, reservado, Piezas.Estandarte, -28, -3, false, false);
            Añadir(r, reservado, Piezas.Estandarte, -28, 2, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 26, -3, false, false);
            Añadir(r, reservado, Piezas.Estandarte, 26, 2, false, false);
        }

        private static void ColocarCasa(Resultado r, bool[] reservado, int x, int y, int calleY, int variante)
        {
            const int anchoFachada = 4;
            const int altoFachada = 2;
            int centroX = x + (anchoFachada / 2);
            bool calleAlSur = calleY < y;

            Pieza fachada = Piezas.CasaFachada[variante % Piezas.CasaFachada.Length];
            for (int fila = 0; fila < altoFachada; fila++)
            {
                for (int columna = 0; columna < anchoFachada; columna++)
                {
                    Pieza pieza = fila == (calleAlSur ? 0 : altoFachada - 1) &&
                        columna == anchoFachada / 2
                        ? Piezas.CasaPuerta : fachada;
                    Añadir(r, reservado, pieza, x + columna, y + fila, fila == 0, false);
                }
            }

            Pieza tejado = Piezas.CasaTejado[variante % Piezas.CasaTejado.Length];
            for (int columna = 0; columna < anchoFachada; columna += tejado.Ancho)
            {
                AñadirTejado(r, reservado, tejado, x + columna, y + altoFachada);
            }

            if (calleAlSur)
            {
                for (int aceraY = y - 1; aceraY >= calleY; aceraY--)
                    PonerAcera(r, centroX, aceraY);
            }
            else
            {
                for (int aceraY = y + altoFachada; aceraY <= calleY; aceraY++)
                    PonerAcera(r, centroX, aceraY);
            }
        }

        private static void ColocarGranjas(Resultado r, bool[] reservado)
        {
            int[] xs = { -23, -3, 17 };
            int[] ys = { 38, 43, 38 };
            for (int n = 0; n < xs.Length; n++)
            {
                int x0 = xs[n];
                int y0 = ys[n];
                PintarRect(r, x0, y0, 12, 8, false);
                for (int y = y0; y < y0 + 8; y++)
                {
                    for (int x = x0; x < x0 + 12; x++)
                    {
                        int i = r.Indice(x, y);
                        r.Suelo[i] = Seleccionar(Piezas.Tierra, Hash(x, y, 12 + n));
                    }
                }

                // Vallado discontinuo con hueco de dos casillas en el lado sur.
                for (int x = x0; x < x0 + 12; x += 2)
                {
                    if (x < x0 + 5 || x >= x0 + 7)
                    {
                        Añadir(r, reservado, Piezas.Valla[(x / 2) & 1], x, y0 - 1, false, true);
                        Añadir(r, reservado, Piezas.Valla[(x / 2) & 1], x, y0 + 8, false, true);
                    }
                }
                for (int y = y0 + 1; y < y0 + 8; y += 2)
                {
                    Añadir(r, reservado, Piezas.Valla[(y / 2) & 1], x0 - 2, y, false, true);
                    Añadir(r, reservado, Piezas.Valla[(y / 2) & 1], x0 + 12, y, false, true);
                }

                // Cobertizo/cultivo central, sin bloquear los caminos de acceso.
                Añadir(r, reservado, Piezas.Granja[n % Piezas.Granja.Length], x0 + 5, y0 + 3, false, true);
            }
        }

        private static void ColocarArena(Resultado r, bool[] reservado)
        {
            // Valla visible en los lados norte y sur, dejando la apertura central de dos casillas.
            for (int x = ArenaRect.X; x < ArenaRect.X + ArenaRect.Ancho; x += 2)
            {
                if (x == ArenaRect.CentroX - 1) continue;
                Añadir(r, reservado, Piezas.Valla[(x / 2) & 1], x, ArenaRect.Y - 1, false, false);
                Añadir(r, reservado, Piezas.Valla[(x / 2) & 1], x, ArenaRect.Y + ArenaRect.Alto, false, false);
            }
            for (int y = ArenaRect.Y; y < ArenaRect.Y + ArenaRect.Alto; y++)
            {
                if (y == 31 || y == 32) continue;
                Añadir(r, reservado, Piezas.Valla[2], ArenaRect.X - 1, y, false, false);
                Añadir(r, reservado, Piezas.Valla[2], ArenaRect.X + ArenaRect.Ancho, y, false, false);
            }
        }

        private static void ColocarPuentes(Resultado r, bool[] reservado)
        {
            ColocarPuente(r, reservado, 0);
            ColocarPuente(r, reservado, 58);
        }

        private static void ColocarPuente(Resultado r, bool[] reservado, int x)
        {
            int centro = CentroRio(x);
            // La calzada cubre el cauce completo en ese punto; el resto del río sigue bloqueado.
            for (int dx = -1; dx <= 1; dx++)
            {
                for (int dy = -2; dy <= 2; dy++)
                {
                    int i = r.Indice(x + dx, centro + dy);
                    if (i >= 0)
                    {
                        r.Bloqueado[i] = false;
                        r.Suelo[i] = Seleccionar(Piezas.Tierra, Hash(x + dx, centro + dy, 55));
                    }
                }
            }
            // La pieza es decorativa; las casillas transitables las define el layout.
            Añadir(r, reservado, Piezas.Puente[x == 0 ? 1 : 2], x, centro - 1, false, true);
        }

        private static void ColocarVegetacion(Resultado r, bool[] reservado, Random rng)
        {
            // Anillo arbolado; su colisión ya está fijada, no depende del tamaño del sprite.
            for (int x = OrigenX; x < OrigenX + Ancho - 1; x += 2)
            {
                Añadir(r, reservado, Piezas.Arbol[0], x, OrigenY, false, true);
                Añadir(r, reservado, Piezas.Arbol[0], x, OrigenY + Alto - 2, false, true);
            }
            for (int y = OrigenY + 4; y < OrigenY + Alto - 4; y += 2)
            {
                Añadir(r, reservado, Piezas.Arbol[0], OrigenX, y, false, true);
                Añadir(r, reservado, Piezas.Arbol[0], OrigenX + Ancho - 2, y, false, true);
            }

            // Bosque al oeste y arboledas aisladas, sin caminos ni claros.
            for (int y = -54; y <= 54; y += 2)
            {
                for (int x = -76; x <= -36; x += 2)
                {
                    if (Hash(x, y, 31) < 0.34f)
                    {
                        Añadir(r, reservado, Piezas.Arbol[rng.Next(Piezas.Arbol.Length)], x, y, true, false);
                    }
                }
            }
            for (int y = -52; y <= 53; y += 3)
            {
                for (int x = -34; x <= 70; x += 4)
                {
                    if (!DentroCiudad(x, y) && Hash(x, y, 32) < 0.018f)
                    {
                        Añadir(r, reservado, Piezas.Arbol[rng.Next(Piezas.Arbol.Length)], x, y, true, false);
                    }
                }
            }

            // Rocas alrededor de la meseta y en las orillas; la rampa queda libre.
            for (int y = -13; y <= 15; y += 3)
            {
                if (y < -2 || y > 1)
                {
                    Añadir(r, reservado, Piezas.Roca, 44, y, true, true);
                }
                Añadir(r, reservado, Piezas.Roca, 72, y, true, true);
            }
            for (int x = 46; x < 72; x += 2)
            {
                Añadir(r, reservado, Piezas.Acantilado[(x / 2) % Piezas.Acantilado.Length], x, -14, false, true);
                Añadir(r, reservado, Piezas.Acantilado[(x / 2 + 1) % Piezas.Acantilado.Length], x, 16, false, true);
            }
            for (int y = -52; y < -40; y += 3)
            {
                Añadir(r, reservado, Piezas.Roca, -73, y, false, true);
            }
        }

        private static void AsegurarPasos(Resultado r)
        {
            // Salidas y caminos prevalecen sobre ruido/vegetación y sobre las paredes.
            for (int i = 0; i < r.Camino.Length; i++)
            {
                if (r.Camino[i] != null) r.Bloqueado[i] = false;
            }
            foreach (Punto puerta in Puertas)
            {
                Desbloquear(r, puerta.CasillaX, puerta.CasillaY);
            }
            for (int y = -2; y <= 1; y++)
            {
                Desbloquear(r, -30, y); Desbloquear(r, -29, y);
                Desbloquear(r, 28, y); Desbloquear(r, 29, y);
            }
            for (int x = -2; x <= 1; x++)
            {
                Desbloquear(r, x, -22); Desbloquear(r, x, -21);
                Desbloquear(r, x, 20); Desbloquear(r, x, 21);
            }
            for (int y = ArenaRect.Y; y < ArenaRect.Y + ArenaRect.Alto; y++)
            {
                if (y >= 31 && y <= 32) Desbloquear(r, ArenaRect.X - 1, y);
            }
            Desbloquear(r, ArenaRect.CentroX, ArenaRect.Y - 1);
            Desbloquear(r, ArenaRect.CentroX, ArenaRect.Y + ArenaRect.Alto);
            for (int y = OrigenY; y < OrigenY + 4; y++)
            {
                for (int x = OrigenX; x < OrigenX + Ancho; x++) Bloquear(r, x, y);
            }
            for (int y = OrigenY + Alto - 4; y < OrigenY + Alto; y++)
            {
                for (int x = OrigenX; x < OrigenX + Ancho; x++) Bloquear(r, x, y);
            }
            for (int x = OrigenX; x < OrigenX + 4; x++)
            {
                for (int y = OrigenY; y < OrigenY + Alto; y++) Bloquear(r, x, y);
            }
            for (int x = OrigenX + Ancho - 4; x < OrigenX + Ancho; x++)
            {
                for (int y = OrigenY; y < OrigenY + Alto; y++) Bloquear(r, x, y);
            }
        }

        private static int BuscarAlcanzables(Resultado r)
        {
            var visitado = new bool[Ancho * Alto];
            var cola = new Queue<int>();
            int inicio = r.Indice(Aparicion.CasillaX, Aparicion.CasillaY);
            if (inicio < 0 || r.Bloqueado[inicio]) return 0;
            visitado[inicio] = true;
            cola.Enqueue(inicio);
            int total = 0;
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { 1, -1, 0, 0 };
            while (cola.Count > 0)
            {
                int actual = cola.Dequeue();
                total++;
                int x = (actual % Ancho) + OrigenX;
                int y = (actual / Ancho) + OrigenY;
                for (int n = 0; n < 4; n++)
                {
                    int siguiente = r.Indice(x + dx[n], y + dy[n]);
                    if (siguiente >= 0 && !visitado[siguiente] && !r.Bloqueado[siguiente])
                    {
                        visitado[siguiente] = true;
                        cola.Enqueue(siguiente);
                    }
                }
            }
            return total;
        }

        private static void VerificarAccesos(Resultado r)
        {
            var alcanzable = CalcularMascaraAlcanzable(r);
            var pendientes = new List<string>();
            foreach (Punto puerta in Puertas)
            {
                ComprobarAlcanzable(r, alcanzable, puerta.CasillaX, puerta.CasillaY, "puerta", pendientes);
            }
            foreach (Claro claro in Claros)
            {
                ComprobarAlcanzable(r, alcanzable, claro.CasillaX, claro.CasillaY, "claro", pendientes);
            }
            ComprobarAlcanzable(r, alcanzable, ArenaRect.CentroX, ArenaRect.CentroY, "arena", pendientes);
            if (pendientes.Count > 0)
            {
                throw new InvalidOperationException("El layout de Ciudad tiene destinos sin acceso: " +
                    string.Join(", ", pendientes.ToArray()));
            }
        }

        private static bool[] CalcularMascaraAlcanzable(Resultado r)
        {
            var visitado = new bool[Ancho * Alto];
            var cola = new Queue<int>();
            int inicio = r.Indice(Aparicion.CasillaX, Aparicion.CasillaY);
            if (inicio < 0 || r.Bloqueado[inicio]) return visitado;
            visitado[inicio] = true; cola.Enqueue(inicio);
            int[] dx = { 0, 0, -1, 1 };
            int[] dy = { 1, -1, 0, 0 };
            while (cola.Count > 0)
            {
                int actual = cola.Dequeue();
                int x = actual % Ancho + OrigenX;
                int y = actual / Ancho + OrigenY;
                for (int n = 0; n < 4; n++)
                {
                    int siguiente = r.Indice(x + dx[n], y + dy[n]);
                    if (siguiente >= 0 && !visitado[siguiente] && !r.Bloqueado[siguiente])
                    {
                        visitado[siguiente] = true; cola.Enqueue(siguiente);
                    }
                }
            }
            return visitado;
        }

        private static void ComprobarAlcanzable(Resultado r, bool[] visitado, int x, int y,
            string tipo, List<string> pendientes)
        {
            int i = r.Indice(x, y);
            if (i < 0 || !visitado[i]) pendientes.Add(tipo + " " + x + "," + y);
        }

        private static bool Añadir(Resultado r, bool[] reservado, Pieza pieza, int x, int y,
            bool solido, bool permitirCamino)
        {
            return AñadirOrientado(r, reservado, pieza, x, y, solido, permitirCamino, false, false);
        }

        private static bool AñadirRotado(Resultado r, bool[] reservado, Pieza pieza, int x, int y,
            bool solido, bool permitirCamino, bool tejado)
        {
            return AñadirOrientado(r, reservado, pieza, x, y, solido, permitirCamino, true, tejado);
        }

        private static bool AñadirTejado(Resultado r, bool[] reservado, Pieza pieza, int x, int y)
        {
            return AñadirOrientado(r, reservado, pieza, x, y, false, false, false, true);
        }

        private static bool AñadirOrientado(Resultado r, bool[] reservado, Pieza pieza, int x, int y,
            bool solido, bool permitirCamino, bool rotada, bool tejado)
        {
            int ancho = rotada ? pieza.Alto : pieza.Ancho;
            int alto = rotada ? pieza.Ancho : pieza.Alto;
            bool esPuerta = pieza.Archivo == Piezas.Puerta.Archivo;
            for (int oy = 0; oy < alto; oy++)
            {
                for (int ox = 0; ox < ancho; ox++)
                {
                    int i = r.Indice(x + ox, y + oy);
                    if (i < 0 || reservado[i] || (!esPuerta && EsZonaProtegida(x + ox, y + oy))) return false;
                    if (!permitirCamino && r.Camino[i] != null) return false;
                }
            }

            var objeto = new Objeto(pieza, x, y, solido, rotada, tejado);
            r.Objetos.Add(objeto);
            for (int oy = 0; oy < alto; oy++)
            {
                for (int ox = 0; ox < ancho; ox++) reservado[r.Indice(x + ox, y + oy)] = true;
            }
            if (solido)
            {
                for (int ox = 0; ox < ancho; ox++)
                {
                    int i = r.Indice(x + ox, y);
                    if (i >= 0) r.Bloqueado[i] = true;
                }
            }
            return true;
        }

        private static bool EsZonaProtegida(int x, int y)
        {
            if (x == Aparicion.CasillaX && y == Aparicion.CasillaY) return true;
            if (x >= ArenaRect.X && x < ArenaRect.X + ArenaRect.Ancho &&
                y >= ArenaRect.Y && y < ArenaRect.Y + ArenaRect.Alto) return true;
            foreach (Punto puerta in Puertas)
            {
                if (x == puerta.CasillaX && y == puerta.CasillaY) return true;
            }
            foreach (Claro claro in Claros)
            {
                if (Math.Abs(x - claro.CasillaX) <= 5 && Math.Abs(y - claro.CasillaY) <= 5) return true;
            }
            return false;
        }

        private static bool CercaDeCasa(Resultado r, int x, int y, int ancho, int alto)
        {
            foreach (Objeto objeto in r.Objetos)
            {
                bool esCasa = false;
                foreach (Pieza casa in Piezas.CasaFachada)
                {
                    if (casa.Archivo == objeto.Archivo) { esCasa = true; break; }
                }
                if (!esCasa && objeto.Archivo == Piezas.CasaPuerta.Archivo) esCasa = true;
                if (!esCasa) continue;
                if (x <= objeto.X + objeto.Ancho && x + ancho - 1 >= objeto.X - 1 &&
                    y <= objeto.Y + objeto.Alto && y + alto - 1 >= objeto.Y - 1) return true;
            }
            return false;
        }

        private static void PintarLinea(Resultado r, int x0, int y0, int x1, int y1, int ancho)
        {
            // Enrutado ortogonal determinista, con grosor impar centrado.
            PintarSegmento(r, x0, y0, x1, y0, ancho);
            PintarSegmento(r, x1, y0, x1, y1, ancho);
        }

        private static void PintarSegmento(Resultado r, int x0, int y0, int x1, int y1, int ancho)
        {
            int minimo = -(ancho / 2);
            int maximo = minimo + ancho - 1;
            if (y0 == y1)
            {
                for (int x = Math.Min(x0, x1); x <= Math.Max(x0, x1); x++)
                    for (int dy = minimo; dy <= maximo; dy++) PonerCamino(r, x, y0 + dy);
            }
            else
            {
                for (int y = Math.Min(y0, y1); y <= Math.Max(y0, y1); y++)
                    for (int dx = minimo; dx <= maximo; dx++) PonerCamino(r, x0 + dx, y);
            }
        }

        private static void PintarRect(Resultado r, int x, int y, int ancho, int alto,
            bool calle, bool plaza = false)
        {
            for (int cy = y; cy < y + alto; cy++)
            {
                for (int cx = x; cx < x + ancho; cx++)
                {
                    int i = r.Indice(cx, cy);
                    if (i < 0) continue;
                    if (calle)
                    {
                        r.Camino[i] = ElegirBase(Piezas.Empedrado, Hash(cx, cy, 4));
                        r.Suelo[i] = r.Camino[i];
                    }
                    else if (plaza)
                    {
                        r.Suelo[i] = ElegirBase(Piezas.PlazaBase, Hash(cx, cy, 5));
                    }
                }
            }
        }

        private static void PonerCamino(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            if (i < 0) return;
            r.Camino[i] = DentroCiudad(x, y)
                ? ElegirBase(Piezas.Empedrado, Hash(x, y, 4))
                : Seleccionar(Piezas.Tierra, Hash(x, y, 6));
            r.Suelo[i] = r.Camino[i];
            r.Bloqueado[i] = false;
        }

        private static void PonerAcera(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            if (i < 0) return;
            string empedrado = ElegirBase(Piezas.Empedrado, Hash(x, y, 4));
            r.Camino[i] = empedrado;
            r.Suelo[i] = empedrado;
            r.Bloqueado[i] = false;
        }

        private static void PonerAgua(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            if (i < 0) return;
            r.Suelo[i] = Piezas.Agua.Archivo;
            r.Bloqueado[i] = true;
        }

        private static void PintarAcentos(Resultado r)
        {
            // Un parche de 6x4 en torno a la fuente y el cruce principal de 4x4.
            PintarAcento(r, -3, -2, 6, 4, 51);
            PintarAcento(r, -3, -2, 4, 4, 52);
        }

        private static void PintarAcento(Resultado r, int x, int y, int ancho, int alto, int canal)
        {
            for (int cy = y; cy < y + alto; cy++)
            {
                for (int cx = x; cx < x + ancho; cx++)
                {
                    int i = r.Indice(cx, cy);
                    if (i < 0) continue;
                    string acento = Seleccionar(Piezas.Acento, Hash(cx, cy, canal));
                    r.Suelo[i] = acento;
                    if (r.Camino[i] != null) r.Camino[i] = acento;
                }
            }
        }

        private static void PintarOrillasAgua(Resultado r)
        {
            for (int y = OrigenY; y < OrigenY + Alto; y++)
            {
                for (int x = OrigenX; x < OrigenX + Ancho; x++)
                {
                    int i = r.Indice(x, y);
                    if (!EsAgua(r.Suelo[i])) continue;
                    // La orilla lleva la hierba en un lado: arriba si la tierra está encima, abajo si está debajo.
                    // Con tierra solo a los lados se deja agua para no pintar hierba dentro del cauce.
                    if (EsTierraAdyacente(r, x, y + 1)) r.Suelo[i] = Piezas.OrillaAgua.Archivo;
                    else if (EsTierraAdyacente(r, x, y - 1)) r.Suelo[i] = Piezas.OrillaAguaAbajo.Archivo;
                    else r.Suelo[i] = Hash(x, y, 53) < 0.15f ? Piezas.AguaMar2.Archivo : Piezas.AguaMar.Archivo;
                }
            }
        }

        private static bool EsTierraAdyacente(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            return i < 0 || !EsAgua(r.Suelo[i]);
        }

        private static bool EsAgua(string suelo)
        {
            return suelo == Piezas.Agua.Archivo || suelo == Piezas.AguaMar.Archivo ||
                suelo == Piezas.AguaMar2.Archivo ||
                suelo == Piezas.OrillaAgua.Archivo || suelo == Piezas.OrillaAguaAbajo.Archivo;
        }

        private static void Bloquear(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            if (i >= 0) r.Bloqueado[i] = true;
        }

        private static void Desbloquear(Resultado r, int x, int y)
        {
            int i = r.Indice(x, y);
            if (i >= 0) r.Bloqueado[i] = false;
        }

        private static bool EsHuecoPuerta(int x, int y)
        {
            return ((x >= -2 && x <= 1) && (y < CiudadRect.Y + 2 || y >= CiudadRect.Y + CiudadRect.Alto - 2)) ||
                ((y >= -2 && y <= 1) && (x < CiudadRect.X + 2 || x >= CiudadRect.X + CiudadRect.Ancho - 2));
        }

        private static bool DentroCiudad(int x, int y)
        {
            return x >= CiudadRect.X && x < CiudadRect.X + CiudadRect.Ancho &&
                y >= CiudadRect.Y && y < CiudadRect.Y + CiudadRect.Alto;
        }

        private static int CentroRio(int x)
        {
            return -35 + (int)Math.Round((Math.Sin(x / 10.0) * 2.0) + (Math.Sin(x / 4.0) * 0.8));
        }

        private static string Seleccionar(Pieza[] piezas, float valor)
        {
            int indice = Math.Min(piezas.Length - 1, (int)(valor * piezas.Length));
            return piezas[indice].Archivo;
        }

        private static string ElegirBase(Pieza[] piezas, float valor)
        {
            return piezas[valor < 0.85f ? 0 : 1].Archivo;
        }

        private static float Hash(int x, int y, int canal)
        {
            unchecked
            {
                uint h = (uint)(x * 374761393 + y * 668265263 + canal * 2147483647 + Semilla * 144665);
                h = (h ^ (h >> 13)) * 1274126177u;
                h ^= h >> 16;
                return (h & 0xFFFFFF) / (float)0x1000000;
            }
        }
    }
}
