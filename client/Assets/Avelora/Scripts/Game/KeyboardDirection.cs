using UnityEngine;
using UnityEngine.InputSystem;

namespace Avelora.Client
{
    /// <summary>Lectura compartida de teclado, relativa al yaw de la cámara.</summary>
    /// <remarks>
    /// Es la única costura que conoce el Input System. Cuando haya un InputManager con rebindings,
    /// se cambia esta clase y ni la predicción ni el controlador se enteran.
    /// </remarks>
    public static class KeyboardDirection
    {
        /// <summary>Lee WASD o flechas y devuelve la dirección discreta relativa a la cámara.</summary>
        public static void Read(out sbyte directionX, out sbyte directionY)
        {
            int x = 0;
            int y = 0;

            Keyboard keyboard = Keyboard.current;
            if (keyboard != null)
            {
                if (keyboard.aKey.isPressed || keyboard.leftArrowKey.isPressed)
                {
                    x -= 1;
                }

                if (keyboard.dKey.isPressed || keyboard.rightArrowKey.isPressed)
                {
                    x += 1;
                }

                if (keyboard.sKey.isPressed || keyboard.downArrowKey.isPressed)
                {
                    y -= 1;
                }

                if (keyboard.wKey.isPressed || keyboard.upArrowKey.isPressed)
                {
                    y += 1;
                }
            }

            if (x == 0 && y == 0)
            {
                directionX = 0;
                directionY = 0;
                return;
            }

            float rad = CameraRig.Yaw * Mathf.Deg2Rad;
            float c = Mathf.Cos(rad);
            float s = Mathf.Sin(rad);
            // Adelante de la cámara = (sin yaw, cos yaw); derecha = (cos yaw, -sin yaw).
            float wx = x * c + y * s;
            float wy = -x * s + y * c;
            directionX = (sbyte)Mathf.RoundToInt(wx);
            directionY = (sbyte)Mathf.RoundToInt(wy);
        }
    }
}
