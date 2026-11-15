using Avelora.Shared;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Primitivas, material común y conversión entre coordenadas de red y mundo 3D.</summary>
    public static class EntityVisuals
    {
        public const float PlayerHeight = 1.8f;
        public const float NpcHeight = 1.4f;
        public const float ItemDiameter = 0.3f;

        private static readonly int BaseColorId = Shader.PropertyToID("_BaseColor");
        private static readonly int ColorId = Shader.PropertyToID("_Color");
        private static readonly MaterialPropertyBlock PropertyBlock = new MaterialPropertyBlock();
        private static Material _material;

        /// <summary>Proyecta las coordenadas de red X/Y sobre el plano del mundo X/Z.</summary>
        public static Vector3 ToWorld(Vec2 position) => new Vector3(position.X, 0f, position.Y);

        /// <summary>Recupera las coordenadas X/Y de red desde una posición del mundo X/Z.</summary>
        public static Vec2 ToNet(Vector3 position) => new Vec2(position.x, position.z);

        /// <summary>Crea la primitiva adecuada como hijo, apoyada sobre el plano Y=0.</summary>
        public static Renderer CreateEntity(Transform parent, EntityKind kind, ushort typeId, Color color)
        {
            PrimitiveType primitive;
            float height;
            Vector3 scale;
            string label;

            if (kind == EntityKind.Player)
            {
                primitive = PrimitiveType.Capsule;
                height = PlayerHeight;
                scale = new Vector3(0.8f, PlayerHeight * 0.5f, 0.8f);
                label = "Jugador";
            }
            else if (kind == EntityKind.Item)
            {
                primitive = PrimitiveType.Sphere;
                height = ItemDiameter;
                scale = Vector3.one * ItemDiameter;
                label = "Objeto";
            }
            else
            {
                primitive = (typeId & 1) == 0 ? PrimitiveType.Cube : PrimitiveType.Cylinder;
                height = NpcHeight;
                scale = primitive == PrimitiveType.Cube
                    ? new Vector3(0.8f, NpcHeight, 0.8f)
                    : new Vector3(0.7f, NpcHeight * 0.5f, 0.7f);
                label = "PNJ";
            }

            GameObject visual = GameObject.CreatePrimitive(primitive);
            visual.name = label + "_" + typeId;
            visual.transform.SetParent(parent, false);
            visual.transform.localPosition = new Vector3(0f, height * 0.5f, 0f);
            visual.transform.localScale = scale;
            RemoveCollider(visual);

            Renderer renderer = visual.GetComponent<Renderer>();
            renderer.sharedMaterial = SharedMaterial;
            SetColor(renderer, color);
            return renderer;
        }

        /// <summary>Obtiene la cápsula local existente o la crea si la escena no la tenía.</summary>
        public static Renderer EnsurePlayer(Transform parent)
        {
            Transform visual = parent.Find("Jugador_0");
            Renderer renderer = visual != null ? visual.GetComponent<Renderer>() : null;
            if (renderer == null)
            {
                return CreateEntity(parent, EntityKind.Player, 0, Color.gray);
            }

            renderer.sharedMaterial = SharedMaterial;
            SetColor(renderer, Color.gray);
            return renderer;
        }

        /// <summary>Crea el suelo liso ajustado a las dimensiones y el origen del mapa.</summary>
        public static void CreateGround(MapCollisionGrid grid)
        {
            GameObject ground = GameObject.CreatePrimitive(PrimitiveType.Cube);
            ground.name = "SueloAvelora";
            ground.transform.position = new Vector3(
                grid.OriginX + grid.Width * 0.5f,
                -0.05f,
                grid.OriginY + grid.Height * 0.5f);
            ground.transform.localScale = new Vector3(grid.Width, 0.1f, grid.Height);
            RemoveCollider(ground);

            Renderer renderer = ground.GetComponent<Renderer>();
            renderer.sharedMaterial = SharedMaterial;
            SetColor(renderer, new Color(0.42f, 0.48f, 0.4f, 1f));
        }

        /// <summary>Crea un cubo fino de interfaz del mundo con el material común.</summary>
        public static Renderer CreateOverlay(Transform parent, string name, Vector3 position, Vector3 scale, Color color)
        {
            GameObject overlay = GameObject.CreatePrimitive(PrimitiveType.Cube);
            overlay.name = name;
            overlay.transform.SetParent(parent, false);
            overlay.transform.localPosition = position;
            overlay.transform.localScale = scale;
            RemoveCollider(overlay);

            Renderer renderer = overlay.GetComponent<Renderer>();
            renderer.sharedMaterial = SharedMaterial;
            SetColor(renderer, color);
            renderer.enabled = false;
            return renderer;
        }

        /// <summary>Aplica el color mediante el bloque de propiedades sin crear materiales.</summary>
        public static void SetColor(Renderer renderer, Color color)
        {
            if (renderer == null)
            {
                return;
            }

            PropertyBlock.Clear();
            PropertyBlock.SetColor(BaseColorId, color);
            PropertyBlock.SetColor(ColorId, color);
            renderer.SetPropertyBlock(PropertyBlock);
        }

        /// <summary>Color estable derivado del id de tipo, sin nombrar entidades concretas.</summary>
        public static Color ColorForTypeId(ushort typeId)
        {
            float hue = Mathf.Repeat(typeId * 0.618034f, 1f);
            return Color.HSVToRGB(hue, 0.5f, 0.95f);
        }

        private static Material SharedMaterial
        {
            get
            {
                if (_material == null)
                {
                    Shader shader = Shader.Find("Universal Render Pipeline/Lit") ??
                        Shader.Find("Universal Render Pipeline/Unlit");
                    if (shader == null)
                    {
                        Debug.LogError("[Avelora] No se encontró un shader Lit/Unlit de URP.");
                        return null;
                    }

                    _material = new Material(shader) { name = "Avelora URP" };
                }

                return _material;
            }
        }

        private static void RemoveCollider(GameObject gameObject)
        {
            Collider collider = gameObject.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }
        }
    }
}
