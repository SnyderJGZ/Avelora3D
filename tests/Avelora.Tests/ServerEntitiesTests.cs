using System;
using Avelora.Server.Entities;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class EntityIdAllocatorTests
    {
        [Fact]
        public void Empieza_En_Uno_Y_Sube_De_Uno_En_Uno()
        {
            var allocator = new EntityIdAllocator();

            Assert.Equal(EntityIdAllocator.FirstId, allocator.Allocate());
            Assert.Equal(EntityIdAllocator.FirstId + 1, allocator.Allocate());
            Assert.Equal(EntityIdAllocator.FirstId + 2, allocator.Allocate());
            Assert.Equal(3, allocator.Issued);
        }

        [Fact]
        public void Nunca_Repite_Un_Identificador()
        {
            var allocator = new EntityIdAllocator();
            const int Reparto = 10_000;

            var seen = new System.Collections.Generic.HashSet<int>();
            for (int i = 0; i < Reparto; i++)
            {
                Assert.True(seen.Add(allocator.Allocate()), "Se ha repetido un identificador.");
            }

            Assert.Equal(Reparto, allocator.LastAllocated);
        }

        [Fact]
        public void El_Cero_No_Se_Reparte()
        {
            // El 0 queda libre para poder usarlo como «ninguno» en los mensajes de red.
            var allocator = new EntityIdAllocator();

            for (int i = 0; i < 100; i++)
            {
                Assert.NotEqual(0, allocator.Allocate());
            }
        }
    }

    public class EntityStoreTests
    {
        [Fact]
        public void Anade_Y_Encuentra_Por_Id()
        {
            var store = new EntityStore();
            var player = new PlayerEntity(7, characterId: 3, inputBufferCapacity: 4);

            store.Add(player);

            Assert.Equal(1, store.Count);
            Assert.True(store.TryGet(7, out Entity? found));
            Assert.Same(player, found);
            Assert.Null(store.Get(8));
        }

        [Fact]
        public void No_Admite_Dos_Entidades_Con_El_Mismo_Id()
        {
            var store = new EntityStore();
            store.Add(new PlayerEntity(1, 1, 1));

            Assert.Throws<InvalidOperationException>(() => store.Add(new PlayerEntity(1, 2, 1)));
        }

        [Fact]
        public void Al_Quitar_Se_Cierra_El_Hueco_Y_Sigue_Encontrando_Lo_Demas()
        {
            var store = new EntityStore();
            for (int i = 1; i <= 5; i++)
            {
                store.Add(new PlayerEntity(i, i, 1));
            }

            Assert.True(store.Remove(3));

            Assert.Equal(4, store.Count);
            Assert.Null(store.Get(3));

            // El resto sigue ahí y accesible, tanto por Id como por el recorrido denso.
            for (int i = 1; i <= 5; i++)
            {
                if (i == 3)
                {
                    continue;
                }

                Assert.True(store.TryGet(i, out Entity? found), "Se ha perdido la entidad " + i + ".");
                Assert.Equal(i, found!.Id);
            }

            Assert.False(store.Remove(3));
        }

        [Fact]
        public void El_Recorrido_Por_Indice_Ve_Todas_Las_Vivas()
        {
            var store = new EntityStore();
            for (int i = 1; i <= 4; i++)
            {
                store.Add(new PlayerEntity(i, i, 1));
            }

            store.Remove(2);
            store.Remove(4);

            var ids = new System.Collections.Generic.List<int>();
            for (int i = 0; i < store.Count; i++)
            {
                ids.Add(store[i].Id);
            }

            ids.Sort();
            Assert.Equal(new[] { 1, 3 }, ids);
        }

        [Fact]
        public void Cuenta_Por_Tipo()
        {
            var store = new EntityStore();
            store.Add(new PlayerEntity(1, 1, 1));
            store.Add(new Entity(2, EntityKind.Npc));

            Assert.Equal(1, store.CountOf(EntityKind.Player));
            Assert.Equal(1, store.CountOf(EntityKind.Npc));
        }

        [Fact]
        public void El_Indice_Fuera_De_Rango_Se_Avisa()
        {
            var store = new EntityStore();
            store.Add(new PlayerEntity(1, 1, 1));

            Assert.Throws<ArgumentOutOfRangeException>(() => store[1]);
            Assert.Throws<ArgumentOutOfRangeException>(() => store[-1]);
        }

        [Fact]
        public void El_Jugador_Guarda_Su_Caja_Y_Su_Conexion()
        {
            var player = new PlayerEntity(5, characterId: 9, inputBufferCapacity: 4)
            {
                Position = new Vec2(1.5f, -2.5f),
                HalfSize = new Vec2(0.4f, 0.4f),
            };

            Assert.Equal(5, player.Id);
            Assert.Equal(9, player.CharacterId);
            Assert.Equal(EntityKind.Player, player.Kind);
            Assert.Equal(-1, player.ConnectionId);
            Assert.Equal(new Vec2(1.5f, -2.5f), player.Position);
            Assert.Equal(0u, player.LastProcessedSequence);
        }
    }
}
