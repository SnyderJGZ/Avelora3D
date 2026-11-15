using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Cámara de perspectiva fija que sigue al jugador en el plano de juego X/Z.</summary>
    [DisallowMultipleComponent]
    public sealed class CameraRig : MonoBehaviour
    {
        // Perspectiva fija, sin yaw: arriba en pantalla es +Z (Y de red).
        private const float FieldOfView = 35f;
        private const float DownwardAngle = 55f;
        private const float FollowDistance = 30f;
        private const float FixedYaw = 0f;

        [SerializeField] private Transform target;

        public Transform Target
        {
            get => target;
            set => target = value;
        }

        private void Awake()
        {
            Camera camera = GetComponent<Camera>();
            if (camera != null)
            {
                camera.fieldOfView = FieldOfView;
            }
        }

        private void LateUpdate()
        {
            if (target == null)
            {
                return;
            }

            float radians = DownwardAngle * Mathf.Deg2Rad;
            transform.position = target.position + new Vector3(
                0f,
                Mathf.Sin(radians) * FollowDistance,
                -Mathf.Cos(radians) * FollowDistance);
            transform.rotation = Quaternion.Euler(DownwardAngle, FixedYaw, 0f);
        }
    }
}
