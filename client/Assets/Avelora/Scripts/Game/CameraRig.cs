using UnityEngine;
using UnityEngine.InputSystem;

namespace Avelora.Client
{
    /// <summary>Cámara en tercera persona que sigue al jugador y permite orbitar y acercarse.</summary>
    [DisallowMultipleComponent]
    public sealed class CameraRig : MonoBehaviour
    {
        private const float FieldOfView = 35f;
        private const float MinPitch = 20f;
        private const float MaxPitch = 70f;
        private const float MinDistance = 12f;
        private const float MaxDistance = 30f;
        private const float OrbitSensitivity = 0.15f;
        private const float ZoomStep = 0.02f;
        private const float SmoothTime = 0.12f;

        private float _yaw;
        private float _pitch = 55f;
        private float _distance = 22f;
        private float _yawSmooth;
        private float _pitchSmooth = 55f;
        private float _distanceSmooth = 22f;
        private float _yawVelocity;
        private float _pitchVelocity;
        private float _distanceVelocity;
        private bool _orbitando;

        /// <summary>Yaw actual suavizado de la cámara, en grados.</summary>
        public static float Yaw { get; private set; }

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

            Mouse mouse = Mouse.current;
            if (mouse != null)
            {
                if (mouse.rightButton.wasPressedThisFrame)
                {
                    _orbitando = !NetworkClient.PointerOverGui;
                }

                if (!mouse.rightButton.isPressed)
                {
                    _orbitando = false;
                }
                else if (_orbitando)
                {
                    Vector2 delta = mouse.delta.ReadValue();
                    _yaw += delta.x * OrbitSensitivity;
                    _pitch = Mathf.Clamp(_pitch - delta.y * OrbitSensitivity, MinPitch, MaxPitch);
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f && !NetworkClient.PointerOverGui)
                {
                    _distance = Mathf.Clamp(_distance - scroll * ZoomStep, MinDistance, MaxDistance);
                }
            }
            else
            {
                _orbitando = false;
            }

            _yawSmooth = Mathf.SmoothDampAngle(_yawSmooth, _yaw, ref _yawVelocity, SmoothTime);
            _pitchSmooth = Mathf.SmoothDamp(_pitchSmooth, _pitch, ref _pitchVelocity, SmoothTime);
            _distanceSmooth = Mathf.SmoothDamp(_distanceSmooth, _distance, ref _distanceVelocity, SmoothTime);
            Yaw = _yawSmooth;

            Quaternion rotation = Quaternion.Euler(_pitchSmooth, _yawSmooth, 0f);
            transform.position = target.position - rotation * Vector3.forward * _distanceSmooth;
            transform.rotation = rotation;
        }
    }
}
