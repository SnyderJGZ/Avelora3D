using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Anima y orienta un modelo según cómo se desplaza su padre (jugador local o remoto).</summary>
    public sealed class ModelMotion : MonoBehaviour
    {
        private const float TurnSpeed = 720f;
        private const float MinSpeed = 0.1f;

        private static readonly int SpeedId = Animator.StringToHash("Speed");

        private Animator _animator;
        private Vector3 _lastPosition;

        private void Awake()
        {
            _animator = GetComponent<Animator>();
            _lastPosition = transform.parent != null ? transform.parent.position : transform.position;
        }

        private void LateUpdate()
        {
            Transform root = transform.parent != null ? transform.parent : transform;
            Vector3 delta = root.position - _lastPosition;
            _lastPosition = root.position;
            delta.y = 0f;

            float speed = Time.deltaTime > 0f ? delta.magnitude / Time.deltaTime : 0f;
            if (_animator != null)
            {
                _animator.SetFloat(SpeedId, speed);
            }

            if (speed > MinSpeed)
            {
                Quaternion target = Quaternion.LookRotation(delta);
                transform.rotation = Quaternion.RotateTowards(transform.rotation, target, TurnSpeed * Time.deltaTime);
            }
        }
    }
}
