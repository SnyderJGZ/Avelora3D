using UnityEngine.InputSystem;

namespace Avelora.Client
{
    /// <summary>
    /// Lectura de teclado compartida por el controlador local y el cliente de red.
    /// </summary>
    /// <remarks>
    /// Es la única costura que conoce el Input System. Cuando haya un InputManager con rebindings,
    /// se cambia esta clase y ni la predicción ni el controlador se enteran.
    /// </remarks>
    public static class KeyboardDirection
    {
        /// <summary>Lee WASD o flechas y devuelve una dirección discreta (-1, 0 o 1 por eje).</summary>
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

            directionX = (sbyte)x;
            directionY = (sbyte)y;
        }
    }
}
