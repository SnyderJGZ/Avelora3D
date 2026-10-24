#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Shared.Protocol;

namespace Avelora.Shared.Prediction
{
    /// <summary>Muestra de una entidad ya descuantizada, lista para dibujar.</summary>
    public readonly struct EntitySample
    {
        public EntitySample(
            Vec2 position,
            sbyte directionX,
            sbyte directionY,
            EntityKind kind,
            byte healthPercent = 100,
            EntityAction action = EntityAction.None,
            ushort typeId = 0)
        {
            Position = position;
            DirectionX = directionX;
            DirectionY = directionY;
            Kind = kind;
            HealthPercent = healthPercent;
            Action = action;
            TypeId = typeId;
        }

        public Vec2 Position { get; }

        public sbyte DirectionX { get; }

        public sbyte DirectionY { get; }

        public EntityKind Kind { get; }

        /// <summary>Vida en porcentaje (0–100) del snapshot más nuevo de los dos interpolados.</summary>
        public byte HealthPercent { get; }

        /// <summary>Acción del snapshot más nuevo: no se interpola, se toma tal cual.</summary>
        public EntityAction Action { get; }

        /// <summary>Tipo de PNJ del snapshot más nuevo; 0 para los jugadores (I-03c).</summary>
        public ushort TypeId { get; }
    }

    /// <summary>
    /// Historial corto de snapshots para dibujar al resto de entidades con ~100 ms de retraso
    /// (plan §5.2): siempre se interpola entre dos estados conocidos, nunca se enseña el último
    /// salto de red.
    /// </summary>
    /// <remarks>
    /// Los snapshots llegan a 20 Hz y el render va a la velocidad que sea. Con dos snapshots
    /// (100 ms) hay margen para perder uno sin quedarse sin estados. Si el render se adelanta al
    /// último snapshot, se extrapola como mucho <c>maxExtrapolationSeconds</c> y después se
    /// congela: es preferible un personaje quieto un instante a uno que se teletransporta.
    /// </remarks>
    public sealed class SnapshotTimeline
    {
        private struct Entry
        {
            public Entry(double time, in SnapshotMessage snapshot)
            {
                Time = time;
                Snapshot = snapshot;
            }

            public double Time { get; }

            public SnapshotMessage Snapshot { get; }
        }

        private readonly List<Entry> _entries;
        private readonly double _maxHistorySeconds;
        private readonly double _maxExtrapolationSeconds;

        public SnapshotTimeline(double maxHistorySeconds = 1.0, double maxExtrapolationSeconds = 0.1)
        {
            if (maxHistorySeconds <= 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxHistorySeconds), maxHistorySeconds, "El historial debe ser positivo.");
            }

            if (maxExtrapolationSeconds < 0.0)
            {
                throw new ArgumentOutOfRangeException(nameof(maxExtrapolationSeconds), maxExtrapolationSeconds, "La extrapolación no puede ser negativa.");
            }

            _maxHistorySeconds = maxHistorySeconds;
            _maxExtrapolationSeconds = maxExtrapolationSeconds;
            _entries = new List<Entry>(32);
        }

        /// <summary>Snapshots guardados ahora mismo.</summary>
        public int Count => _entries.Count;

        public bool HasSnapshots => _entries.Count > 0;

        /// <summary>Último snapshot recibido, o <c>default</c> si todavía no hay ninguno.</summary>
        public SnapshotMessage Latest =>
            _entries.Count > 0 ? _entries[_entries.Count - 1].Snapshot : default;

        /// <summary>
        /// Añade un snapshot con la hora local a la que llegó. Devuelve <c>false</c> si es viejo o
        /// repetido: el orden lo manda el tick del servidor, no la red.
        /// </summary>
        public bool Add(in SnapshotMessage snapshot, double receiveTime)
        {
            if (_entries.Count > 0 &&
                !SequenceMath.IsNewer(snapshot.ServerTick, _entries[_entries.Count - 1].Snapshot.ServerTick))
            {
                return false;
            }

            _entries.Add(new Entry(receiveTime, snapshot));

            // Se conserva siempre el más reciente, aunque el historial sea más corto que el hueco
            // entre snapshots (por ejemplo, tras una pausa larga).
            double cutoff = receiveTime - _maxHistorySeconds;
            int remove = 0;
            while (remove < _entries.Count - 1 && _entries[remove].Time < cutoff)
            {
                remove++;
            }

            if (remove > 0)
            {
                _entries.RemoveRange(0, remove);
            }

            return true;
        }

        /// <summary>
        /// Busca la posición de <paramref name="entityId"/> en <paramref name="renderTime"/>
        /// (hora local). Si la entidad no aparece en ese instante, devuelve <c>false</c>.
        /// </summary>
        public bool TrySample(int entityId, double renderTime, out EntitySample sample)
        {
            sample = default;

            if (_entries.Count == 0)
            {
                return false;
            }

            Entry first = _entries[0];
            Entry last = _entries[_entries.Count - 1];

            if (renderTime <= first.Time)
            {
                return TryRead(first.Snapshot, entityId, out sample);
            }

            if (renderTime >= last.Time)
            {
                if (_entries.Count >= 2 &&
                    renderTime - last.Time <= _maxExtrapolationSeconds)
                {
                    Entry previous = _entries[_entries.Count - 2];
                    double span = last.Time - previous.Time;

                    if (span > 0.0 &&
                        TryRead(previous.Snapshot, entityId, out EntitySample from) &&
                        TryRead(last.Snapshot, entityId, out EntitySample to))
                    {
                        double fraction = (renderTime - previous.Time) / span;
                        sample = new EntitySample(
                            Lerp(from.Position, to.Position, (float)fraction),
                            to.DirectionX,
                            to.DirectionY,
                            to.Kind,
                            to.HealthPercent,
                            to.Action,
                            to.TypeId);
                        return true;
                    }
                }

                return TryRead(last.Snapshot, entityId, out sample);
            }

            int next = 1;
            while (next < _entries.Count && _entries[next].Time <= renderTime)
            {
                next++;
            }

            Entry before = _entries[next - 1];
            Entry after = _entries[next];

            bool hasBefore = TryRead(before.Snapshot, entityId, out EntitySample beforeSample);
            bool hasAfter = TryRead(after.Snapshot, entityId, out EntitySample afterSample);

            if (!hasBefore && !hasAfter)
            {
                return false;
            }

            // Una entidad que aparece o desaparece entre dos snapshots no se interpola contra la
            // nada: se dibuja en el estado que sí existe.
            if (!hasBefore)
            {
                sample = afterSample;
                return true;
            }

            if (!hasAfter)
            {
                sample = beforeSample;
                return true;
            }

            double duration = after.Time - before.Time;
            if (duration <= 0.0)
            {
                sample = afterSample;
                return true;
            }

            double t = (renderTime - before.Time) / duration;
            if (t < 0.0)
            {
                t = 0.0;
            }
            else if (t > 1.0)
            {
                t = 1.0;
            }

            sample = new EntitySample(
                Lerp(beforeSample.Position, afterSample.Position, (float)t),
                afterSample.DirectionX,
                afterSample.DirectionY,
                afterSample.Kind,
                afterSample.HealthPercent,
                afterSample.Action,
                afterSample.TypeId);
            return true;
        }

        /// <summary>Vacía el historial (reconexión o cambio de servidor).</summary>
        public void Clear() => _entries.Clear();

        private static Vec2 Lerp(Vec2 from, Vec2 to, float t) => from + ((to - from) * t);

        private static bool TryRead(in SnapshotMessage snapshot, int entityId, out EntitySample sample)
        {
            EntityState[]? entities = snapshot.Entities;

            if (entities != null)
            {
                for (int i = 0; i < entities.Length; i++)
                {
                    EntityState state = entities[i];

                    if (state.Id == entityId)
                    {
                        sample = new EntitySample(
                            new Vec2(
                                ProtocolConstants.DequantizePosition(state.X),
                                ProtocolConstants.DequantizePosition(state.Y)),
                            state.DirectionX,
                            state.DirectionY,
                            state.Kind,
                            state.HealthPercent,
                            state.Action,
                            state.TypeId);
                        return true;
                    }
                }
            }

            sample = default;
            return false;
        }
    }
}
