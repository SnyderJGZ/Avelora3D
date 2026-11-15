using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Prepara una escena jugable mínima sin exigir una escena editada a mano.</summary>
    public static class Bootstrap3D
    {
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void CrearEscena3D()
        {
            if (Object.FindAnyObjectByType<NetworkClient>() != null)
            {
                return;
            }

            var jugador = new GameObject("JugadorAvelora");
            AveloraMap mapa = jugador.AddComponent<AveloraMap>();
            if (!mapa.Cargar())
            {
                Object.Destroy(jugador);
                return;
            }

            jugador.transform.position = new Vector3(
                mapa.Grid.OriginX + mapa.Grid.Width * 0.5f,
                0f,
                mapa.Grid.OriginY + mapa.Grid.Height * 0.5f);

            EntityVisuals.CreateEntity(jugador.transform, Avelora.Shared.EntityKind.Player, 0, Color.gray);
            jugador.AddComponent<LocalPlayerController>();
            EntityVisuals.CreateGround(mapa.Grid);
            CrearLuz();
            CameraRig rig = PrepararCamara();
            if (rig != null)
            {
                rig.Target = jugador.transform;
            }

            jugador.AddComponent<NetworkClient>();
        }

        private static void CrearLuz()
        {
            Light[] luces = Object.FindObjectsByType<Light>();
            for (int i = 0; i < luces.Length; i++)
            {
                if (luces[i].type == LightType.Directional)
                {
                    return;
                }
            }

            var luz = new GameObject("LuzDireccionalAvelora");
            luz.transform.rotation = Quaternion.Euler(50f, -30f, 0f);
            Light directional = luz.AddComponent<Light>();
            directional.type = LightType.Directional;
            directional.shadows = LightShadows.None;
        }

        private static CameraRig PrepararCamara()
        {
            Camera camera = Camera.main;
            if (camera == null)
            {
                var gameObject = new GameObject("CameraAvelora");
                gameObject.tag = "MainCamera";
                camera = gameObject.AddComponent<Camera>();
            }

            CameraRig rig = camera.GetComponent<CameraRig>();
            return rig != null ? rig : camera.gameObject.AddComponent<CameraRig>();
        }
    }
}
