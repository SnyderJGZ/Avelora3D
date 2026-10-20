#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;

namespace Avelora.Server.Persistence
{
    /// <summary>
    /// Cola de guardado con un único hilo escritor (plan del servidor §8).
    /// </summary>
    /// <remarks>
    /// <para>
    /// La simulación nunca espera a SQLite: entrega una foto inmutable del personaje y sigue con
    /// el tick. Si un personaje se guarda dos veces antes de que el hilo lo escriba, la última
    /// foto reemplaza a la anterior: no interesa una cola de posiciones viejas.
    /// </para>
    /// <para>
    /// Al desconectar se guarda una vez; además, el servidor pide un guardado periódico y otro al
    /// cerrar. El <see cref="Flush"/> permite esperar a que la cola termine antes de salir.
    /// </para>
    /// </remarks>
    public sealed class CharacterSaveService : IDisposable
    {
        private readonly IPlayerRepository _repository;
        private readonly object _gate = new object();
        private readonly Dictionary<int, CharacterRecord> _pending = new Dictionary<int, CharacterRecord>();
        private readonly AutoResetEvent _signal = new AutoResetEvent(false);
        private readonly ManualResetEventSlim _idle = new ManualResetEventSlim(true);
        private readonly Thread _thread;

        private volatile bool _stopping;
        private volatile string? _lastError;
        private bool _disposed;
        private long _saved;
        private long _failed;

        public CharacterSaveService(IPlayerRepository repository)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));

            _thread = new Thread(Run)
            {
                Name = "avelora-save",
                IsBackground = true,
            };

            _thread.Start();
        }

        /// <summary>Personajes escritos correctamente desde que arrancó el servicio.</summary>
        public long Saved => Interlocked.Read(ref _saved);

        /// <summary>Escrituras que fallaron. El error concreto queda en <see cref="LastError"/>.</summary>
        public long Failed => Interlocked.Read(ref _failed);

        /// <summary>Personajes esperando a que el hilo los escriba.</summary>
        public int Pending
        {
            get
            {
                lock (_gate)
                {
                    return _pending.Count;
                }
            }
        }

        /// <summary>Último error de SQLite, si lo hubo. Se puede leer desde otro hilo.</summary>
        public string? LastError => _lastError;

        /// <summary>Encola una foto. Si ya había otra del mismo personaje, la sustituye.</summary>
        public void Enqueue(CharacterRecord character)
        {
            if (character is null)
            {
                throw new ArgumentNullException(nameof(character));
            }

            if (_disposed)
            {
                return;
            }

            lock (_gate)
            {
                _pending[character.Id] = character;
                _idle.Reset();
            }

            _signal.Set();
        }

        /// <summary>Espera a que no quede nada pendiente. Devuelve <c>false</c> si se agota el tiempo.</summary>
        public bool Flush(TimeSpan timeout) => _idle.Wait(timeout);

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _stopping = true;
            _disposed = true;
            _signal.Set();
            _thread.Join(TimeSpan.FromSeconds(10));

            _signal.Dispose();
            _idle.Dispose();
        }

        private void Run()
        {
            while (true)
            {
                _signal.WaitOne();

                while (true)
                {
                    Dictionary<int, CharacterRecord> batch;

                    lock (_gate)
                    {
                        if (_pending.Count == 0)
                        {
                            _idle.Set();

                            if (_stopping)
                            {
                                return;
                            }

                            break;
                        }

                        batch = new Dictionary<int, CharacterRecord>(_pending);
                        _pending.Clear();
                    }

                    foreach (CharacterRecord character in batch.Values)
                    {
                        try
                        {
                            _repository.SaveCharacter(character);
                            Interlocked.Increment(ref _saved);
                        }
                        catch (Exception ex)
                        {
                            _lastError = ex.Message;

                            // Un guardado perdido es progreso perdido: además de contarlo, se dice.
                            Console.Error.WriteLine(
                                "ERROR: no se pudo guardar el personaje " + character.Id + ": " + ex.Message);

                            Interlocked.Increment(ref _failed);
                        }
                    }
                }
            }
        }
    }
}
