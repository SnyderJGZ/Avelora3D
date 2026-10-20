#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using Avelora.Shared;

namespace Avelora.Server.Entities
{
    /// <summary>
    /// Almacén de entidades: búsqueda por Id en O(1) y recorrido denso para el tick
    /// (guía del núcleo §3.2).
    /// </summary>
    /// <remarks>
    /// Mantiene dos estructuras sincronizadas:
    /// <list type="bullet">
    /// <item>un <see cref="Dictionary{TKey,TValue}"/> de Id → índice, para encontrar a alguien sin recorrer nada;</item>
    /// <item>una lista densa, para pasar por todas las entidades sin saltos ni reservas.</item>
    /// </list>
    /// Al borrar se trae la última al hueco (<i>swap-remove</i>), que sigue siendo O(1) y no deja
    /// huecos: recorrer la lista siempre es recorrer entidades vivas.
    ///
    /// <para><b>Ojo:</b> añadir o quitar entidades mientras se recorre con <see cref="this[int]"/>
    /// es un error. En el tick se recorre y, si hace falta borrar, se anota y se aplica al final.</para>
    ///
    /// <para><b>Sin reservas en el tick:</b> los sistemas iteran por índice con
    /// <see cref="Count"/> y <see cref="this[int]"/> en vez de con <c>foreach</c>, porque recorrer
    /// una lista a través de una interfaz (<c>IReadOnlyList</c>) acabaría reservando memoria en el
    /// heap en cada pasada (plan del servidor §9).</para>
    /// </remarks>
    public sealed class EntityStore
    {
        private readonly Dictionary<int, int> _indexById = new Dictionary<int, int>();
        private readonly List<Entity> _dense = new List<Entity>();

        /// <summary>Número de entidades vivas.</summary>
        public int Count => _dense.Count;

        /// <summary>Vista de solo lectura de las entidades vivas. Cómoda fuera del tick.</summary>
        public IReadOnlyList<Entity> All => _dense;

        /// <summary>Entidad por posición en la lista densa (para el recorrido del tick).</summary>
        public Entity this[int index]
        {
            get
            {
                if ((uint)index >= (uint)_dense.Count)
                {
                    throw new ArgumentOutOfRangeException(nameof(index), index, "Índice fuera del almacén.");
                }

                return _dense[index];
            }
        }

        /// <summary>Añade una entidad. Su Id debe ser único.</summary>
        public void Add(Entity entity)
        {
            if (entity is null)
            {
                throw new ArgumentNullException(nameof(entity));
            }

            if (_indexById.ContainsKey(entity.Id))
            {
                throw new InvalidOperationException("Ya existe una entidad con Id " + entity.Id + ".");
            }

            _indexById.Add(entity.Id, _dense.Count);
            _dense.Add(entity);
        }

        /// <summary>Busca por Id.</summary>
        public bool TryGet(int id, [NotNullWhen(true)] out Entity? entity)
        {
            if (_indexById.TryGetValue(id, out int index))
            {
                entity = _dense[index];
                return true;
            }

            entity = null;
            return false;
        }

        /// <summary>Busca por Id, o <c>null</c> si no existe.</summary>
        public Entity? Get(int id) => _indexById.TryGetValue(id, out int index) ? _dense[index] : null;

        /// <summary>Quita la entidad con ese Id. Devuelve <c>true</c> si existía.</summary>
        public bool Remove(int id)
        {
            if (!_indexById.TryGetValue(id, out int index))
            {
                return false;
            }

            int last = _dense.Count - 1;
            Entity moved = _dense[last];
            _dense[index] = moved;
            _indexById[moved.Id] = index;
            _dense.RemoveAt(last);
            _indexById.Remove(id);
            return true;
        }

        /// <summary>Vacía el almacén.</summary>
        public void Clear()
        {
            _dense.Clear();
            _indexById.Clear();
        }

        /// <summary>Recorre las entidades de un tipo concreto, sin reservar memoria.</summary>
        public int CountOf(EntityKind kind)
        {
            int count = 0;
            for (int i = 0; i < _dense.Count; i++)
            {
                if (_dense[i].Kind == kind)
                {
                    count++;
                }
            }

            return count;
        }
    }
}
