using System;
using UnityEngine;

namespace Avelora.Client
{
    /// <summary>Color por clase para los personajes del cliente (I-10b).</summary>
    [CreateAssetMenu(menuName = "Avelora/Class Look")]
    public sealed class ClassLook : ScriptableObject
    {
        [Serializable]
        public sealed class Entry
        {
            public byte classId;
            public Color color = Color.gray;

            public Entry(byte classId, Color color)
            {
                this.classId = classId;
                this.color = color;
            }
        }

        [SerializeField]
        private Entry[] classes =
        {
            new Entry(1, Color.red),
            new Entry(2, Color.blue),
            new Entry(3, Color.green),
            new Entry(4, Color.white),
        };

        /// <summary>Carga el asset Resources/ClassLook o crea los cuatro colores de prueba por defecto.</summary>
        public static ClassLook Load()
        {
            ClassLook look = Resources.Load<ClassLook>("ClassLook");
            return look != null ? look : CreateInstance<ClassLook>();
        }

        /// <summary>Devuelve gris para clases sin entrada de color.</summary>
        public Color ColorFor(byte classId)
        {
            if (classes != null)
            {
                for (int i = 0; i < classes.Length; i++)
                {
                    if (classes[i] != null && classes[i].classId == classId)
                    {
                        return classes[i].color;
                    }
                }
            }

            return Color.gray;
        }
    }
}
