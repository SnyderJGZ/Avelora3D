using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Anima y orienta un modelo según cómo se desplaza su padre (jugador local o remoto).</summary>
    public sealed class ModelMotion : MonoBehaviour
    {
        // La velocidad se mide por la posición de red, que avanza a saltos por tick: se suaviza para que no parpadee.
        private const float VelocitySmoothing = 12f;
        private const float StartRunSpeed = 0.6f;
        private const float StopRunSpeed = 0.3f;
        private const float TurnSmoothing = 10f;
        // Velocidad a la que la animación de correr se ve a ritmo natural (playerSpeed del servidor).
        private const float RunReferenceSpeed = 4f;

        private static readonly int SpeedId = Animator.StringToHash("Speed");

        private Animator _animator;
        private Vector3 _lastPosition;
        private Vector3 _velocity;
        private bool _running;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _lastPosition = transform.parent != null ? transform.parent.position : transform.position;
        }

        private void LateUpdate()
        {
            float dt = Time.deltaTime;
            if (dt <= 0f)
            {
                return;
            }

            Transform root = transform.parent != null ? transform.parent : transform;
            Vector3 delta = root.position - _lastPosition;
            _lastPosition = root.position;
            delta.y = 0f;

            _velocity = Vector3.Lerp(_velocity, delta / dt, 1f - Mathf.Exp(-VelocitySmoothing * dt));
            float speed = _velocity.magnitude;
            _running = _running ? speed > StopRunSpeed : speed > StartRunSpeed;

            if (_animator != null)
            {
                _animator.SetFloat(SpeedId, _running ? speed : 0f);
                _animator.speed = _running ? Mathf.Clamp(speed / RunReferenceSpeed, 0.8f, 1.3f) : 1f;
            }

            if (_running)
            {
                Quaternion target = Quaternion.LookRotation(_velocity);
                transform.rotation = Quaternion.Slerp(transform.rotation, target, 1f - Mathf.Exp(-TurnSmoothing * dt));
            }
        }
    }
}
