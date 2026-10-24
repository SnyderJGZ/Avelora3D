using System;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>
    /// Aspecto de un tipo de PNJ (I-03c): las hojas de animación del <c>id</c> de <c>npcs.json</c>
    /// que llega en el snapshot. Cada hoja va en el orden abajo, arriba, izquierda, derecha.
    /// </summary>
    [Serializable]
    public sealed class NpcLook
    {
        public int typeId;
        public Sprite[] idle = new Sprite[0];
        public Sprite[] walk = new Sprite[0];
        public Sprite[] hurt = new Sprite[0];
        public Sprite[] death = new Sprite[0];
        public Sprite[] attack = new Sprite[0];
    }
}
