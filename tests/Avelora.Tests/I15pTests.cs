using System.Collections.Generic;
using System.Linq;
using Avelora.Server.Entities;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I15pTests
    {
        private static ZoneMap Zones(MapCollisionGrid map) => ZoneMap.FromDefinitions(new[]
        {
            new ZoneDefinition
            {
                Id = 1,
                Name = "Segura de prueba",
                Kind = "safe",
                Rects = new List<ZoneRectDefinition> { new ZoneRectDefinition { X = 10, Y = 4, W = 4, H = 4 } },
            },
            new ZoneDefinition
            {
                Id = 2,
                Name = "Arena de prueba",
                Kind = "pvp",
                Rects = new List<ZoneRectDefinition> { new ZoneRectDefinition { X = 20, Y = 4, W = 4, H = 4 } },
            },
        }, map);

        private static Zone NewZone(
            bool withZones = true,
            int modeChangeTicks = 20,
            int modeChangeFromPvpTicks = 40,
            int forcedTicks = 40,
            PlayerVitalsSettings? vitals = null)
        {
            MapCollisionGrid map = MapCollisionGrid.AllWalkable(30, 20);
            return new Zone(
                1,
                map,
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(64),
                new MovementSystem(4, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(20, 8, 0),
                attacks: new AttackQueue(64),
                combat: new CombatSettings(1),
                playerVitals: vitals,
                itemActions: new ItemActionQueue(64),
                classes: ClassCatalog.Default,
                zones: withZones ? Zones(map) : null,
                pvpModeChangeTicks: modeChangeTicks,
                pvpModeChangeFromPvpTicks: modeChangeFromPvpTicks,
                pvpForcedTicks: forcedTicks);
        }

        private static void RequestMode(Zone zone, PlayerEntity player, PvpMode mode) =>
            Assert.True(zone.ItemActions!.TryEnqueue(ItemAction.SetPvpMode(player.Id, mode)));

        [Fact]
        public void Jugador_Nuevo_Empieza_En_Pve_Y_Cambia_Tras_20_Ticks_Seguros()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);

            Assert.Equal(PvpMode.Pve, player.PvpMode);
            Assert.False(player.PvpEffective);
            List<PvpModeRejectReason> rejected = new List<PvpModeRejectReason>();
            zone.PvpModeRejected += (_, reason) => rejected.Add(reason);

            for (int i = 0; i < 18; i++)
            {
                zone.Tick();
            }

            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(PvpMode.Pve, player.PvpMode);
            Assert.Equal(1, zone.PvpRejectedTooSoon);
            Assert.Equal(new[] { PvpModeRejectReason.TooSoon }, rejected);

            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(PvpMode.Pvp, player.PvpMode);
            Assert.True(player.PvpEffective);
            Assert.Equal(1, zone.PvpModeChanges);
        }

        [Fact]
        public void Espera_Pve_En_Segura_Pasa_A_Lista_Al_Cumplir_20_Ticks()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            Assert.Equal((ushort)1, Assert.Single(statuses).ChangeSeconds);
            for (int i = 1; i < 19; i++)
            {
                zone.Tick();
            }

            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            Assert.Equal(19, player.SafeZoneTicks);
            zone.Tick();
            Assert.Contains(statuses, status => status.Change == PvpChangeState.Ready);
            Assert.Equal(20, player.SafeZoneTicks);
        }

        [Fact]
        public void Entrar_En_Segura_Con_Modo_Pvp_Espera_40_Ticks()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            player.PvpMode = PvpMode.Pvp;
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            Assert.Equal((ushort)2, statuses[0].ChangeSeconds);
            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            RequestMode(zone, player, PvpMode.Pve);
            zone.Tick();
            Assert.Equal(PvpMode.Pvp, player.PvpMode);
            Assert.Equal(1, zone.PvpRejectedTooSoon);

            for (int i = 21; i < 40; i++)
            {
                zone.Tick();
            }

            Assert.Contains(statuses, status => status.Change == PvpChangeState.Ready);
            Assert.Equal(40, player.SafeZoneTicks);
        }

        [Fact]
        public void Entrar_En_Segura_Con_Forzado_Y_Modo_Pve_Espera_40_Ticks()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            player.PvpForcedTicksLeft = 100;
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            Assert.Equal(PvpMode.Pve, player.PvpMode);
            Assert.Equal(0, player.PvpForcedTicksLeft);
            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            Assert.Equal((ushort)2, statuses[0].ChangeSeconds);
            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            for (int i = 20; i < 40; i++)
            {
                zone.Tick();
            }

            Assert.Contains(statuses, status => status.Change == PvpChangeState.Ready);
            Assert.Equal(40, player.SafeZoneTicks);
        }

        [Fact]
        public void Entrar_Con_Forzado_Lo_Cancela_Y_Espera_40_Ticks()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            player.PvpForcedTicksLeft = 5;
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            Assert.Equal(0, player.PvpForcedTicksLeft);
            Assert.Equal(PvpForcedState.None, statuses[0].Forced);
            Assert.Equal((ushort)0, statuses[0].ForcedSeconds);
            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            Assert.Equal((ushort)2, statuses[0].ChangeSeconds);
            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(0, player.PvpForcedTicksLeft);
            Assert.Equal(PvpChangeState.Waiting, statuses[0].Change);
            for (int i = 20; i < 40; i++)
            {
                zone.Tick();
            }

            Assert.Contains(statuses, status => status.Change == PvpChangeState.Ready);
            Assert.Equal(40, player.SafeZoneTicks);

            player.Position = new Vec2(5, 5);
            zone.Tick();
            Assert.Equal(0, player.SafeZoneTicks);
            player.Position = new Vec2(11, 5);
            zone.Tick();
            Assert.Equal((ushort)1, statuses[statuses.Count - 1].ChangeSeconds);
            for (int i = 1; i < 20; i++)
            {
                zone.Tick();
            }

            Assert.Equal(PvpChangeState.Ready, statuses[statuses.Count - 1].Change);
            Assert.Equal(20, player.SafeZoneTicks);
        }

        [Fact]
        public void Salir_De_La_Segura_Reinicia_La_Espera_Y_Fuera_Rechaza()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            for (int i = 0; i < 15; i++)
            {
                zone.Tick();
            }

            player.Position = new Vec2(5, 5);
            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(0, player.SafeZoneTicks);
            Assert.Equal(1, zone.PvpRejectedNotInSafeZone);

            player.Position = new Vec2(11, 5);
            for (int i = 0; i < 9; i++)
            {
                zone.Tick();
            }

            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(1, zone.PvpRejectedTooSoon);
            Assert.Equal(PvpMode.Pve, player.PvpMode);
        }

        [Fact]
        public void Pvp_Forzado_Se_Cancela_Al_Entrar_En_Segura_Y_Cambia_Tras_40_Ticks()
        {
            Zone zone = NewZone(forcedTicks: 80);
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(21, 5), new Vec2(0.4f, 0.4f), 8);
            zone.Tick();
            Assert.Equal(80, player.PvpForcedTicksLeft);
            Assert.True(player.PvpEffective);

            player.Position = new Vec2(11, 5);
            zone.Tick();
            Assert.Equal(0, player.PvpForcedTicksLeft);
            Assert.Equal(ZoneKind.Safe, player.ZoneKind);
            Assert.False(player.PvpEffective);
            Assert.Equal(40, player.SafeZoneWaitTicks);

            for (int i = 0; i < 37; i++)
            {
                zone.Tick();
            }

            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(39, player.SafeZoneTicks);
            Assert.Equal(PvpMode.Pve, player.PvpMode);
            Assert.Equal(1, zone.PvpRejectedTooSoon);

            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(40, player.SafeZoneTicks);
            Assert.Equal(PvpMode.Pvp, player.PvpMode);
        }

        [Fact]
        public void Entrar_En_Segura_Desde_Pvp_Envia_PvpStatus_Sin_Forzado()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(21, 5), new Vec2(0.4f, 0.4f), 8);
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            Assert.Equal(PvpForcedState.InZone, Assert.Single(statuses).Forced);
            player.Position = new Vec2(11, 5);
            zone.Tick();

            PvpStatusMessage status = statuses[statuses.Count - 1];
            Assert.Equal(PvpForcedState.None, status.Forced);
            Assert.Equal((ushort)0, status.ForcedSeconds);
            Assert.Equal(PvpChangeState.Waiting, status.Change);
            Assert.Equal((ushort)2, status.ChangeSeconds);
            Assert.Equal(0, player.PvpForcedTicksLeft);
        }

        [Fact]
        public void PvpStatus_Solo_Se_Envia_Al_Cambiar_Y_Sin_Zonas_No_Se_Envia()
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8);
            List<PvpStatusMessage> statuses = new List<PvpStatusMessage>();
            zone.PvpStatusChanged += (_, status) => statuses.Add(status);

            zone.Tick();
            zone.Tick();
            Assert.Single(statuses);
            Assert.Equal(PvpChangeState.Unavailable, statuses[0].Change);

            player.Position = new Vec2(11, 5);
            zone.Tick();
            zone.Tick();
            Assert.Equal(2, statuses.Count);
            Assert.Equal(PvpChangeState.Waiting, statuses[1].Change);
            Assert.Equal((ushort)1, statuses[1].ChangeSeconds);

            for (int i = 2; i < 20; i++) zone.Tick();
            Assert.Equal(PvpChangeState.Ready, statuses[2].Change);
            Assert.Equal((ushort)0, statuses[2].ChangeSeconds);
            player.Position = new Vec2(21, 5);
            zone.Tick();
            Assert.Equal(PvpForcedState.InZone, statuses[3].Forced);
            player.Position = new Vec2(5, 5);
            zone.Tick();
            Assert.Equal(PvpForcedState.Timer, statuses[4].Forced);
            Assert.Equal((ushort)2, statuses[4].ForcedSeconds);
            for (int i = 1; i < 40; i++) zone.Tick();
            Assert.Equal(6, statuses.Count);
            Assert.Equal(PvpForcedState.None, statuses[5].Forced);

            Zone withoutZones = NewZone(withZones: false);
            PlayerEntity noZonePlayer = withoutZones.SpawnPlayer(2, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8);
            int noZoneStatuses = 0;
            withoutZones.PvpStatusChanged += (_, _) => noZoneStatuses++;
            for (int i = 0; i < 50; i++)
            {
                withoutZones.Tick();
            }

            Assert.Equal(0, noZoneStatuses);
            Assert.False(noZonePlayer.PvpEffective);
        }

        [Fact]
        public void Dos_Jugadores_Pvp_Pueden_Atacarse_En_Zona_Normal()
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity victim = zone.SpawnPlayer(2, new Vec2(6, 5), new Vec2(0.4f, 0.4f), 8);
            attacker.PvpMode = PvpMode.Pvp;
            victim.PvpMode = PvpMode.Pvp;
            victim.AllocVit = 10;
            int health = victim.Health;
            int raw = CharacterStats.GetAttackDamage(ClassCatalog.Default, attacker.ClassId,
                attacker.Level, 0, 0, 0, 0, null, out _);
            int defense = CharacterStats.Calculate(ClassCatalog.Default, victim.ClassId,
                victim.Level, 0, 0, 0, 10).Defense;
            zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, victim.Id));

            zone.Tick();

            Assert.Equal(1, zone.PvpAttacksOk);
            Assert.Equal(1, zone.AttacksOk);
            Assert.True(victim.Health < health);
            Assert.Equal(health - CharacterStats.MitigateDamage(raw, defense), victim.Health);
            Assert.Equal(0, attacker.LastAttackTick);
            Assert.Equal(0, victim.LastDamageTick);
            Assert.Equal(0, victim.RegenAccumulator);
            Assert.Equal(1, zone.CombatEvents.Count);
            zone.Attacks.TryEnqueue(new AttackIntent(attacker.Id, victim.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedAttackCooldown);
        }

        [Fact]
        public void Ataque_Pvp_Alcanza_Zona_Segura_Y_Arena_Pvp_No_Requiere_Modo_Elegido()
        {
            Zone zone = NewZone();
            PlayerEntity safeAttacker = zone.SpawnPlayer(1, new Vec2(11, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity normalVictim = zone.SpawnPlayer(2, new Vec2(12, 5), new Vec2(0.4f, 0.4f), 8);
            safeAttacker.PvpMode = PvpMode.Pvp;
            normalVictim.PvpMode = PvpMode.Pvp;
            zone.Attacks!.TryEnqueue(new AttackIntent(safeAttacker.Id, normalVictim.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPvpSafeZone);

            PlayerEntity arenaAttacker = zone.SpawnPlayer(3, new Vec2(21, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity arenaVictim = zone.SpawnPlayer(4, new Vec2(22, 5), new Vec2(0.4f, 0.4f), 8);
            int health = arenaVictim.Health;
            zone.Attacks.TryEnqueue(new AttackIntent(arenaAttacker.Id, arenaVictim.Id));
            zone.Tick();
            Assert.Equal(1, zone.PvpAttacksOk);
            Assert.True(arenaVictim.Health < health);
        }

        [Fact]
        public void Rechaza_Atacante_Pve_Objetivo_Pve_Muerto_Y_Autoataque()
        {
            Zone zone = NewZone();
            PlayerEntity pveAttacker = zone.SpawnPlayer(1, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity pvpTarget = zone.SpawnPlayer(2, new Vec2(6, 5), new Vec2(0.4f, 0.4f), 8);
            pvpTarget.PvpMode = PvpMode.Pvp;
            zone.Attacks!.TryEnqueue(new AttackIntent(pveAttacker.Id, pvpTarget.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPvpAttackerPve);

            pveAttacker.PvpMode = PvpMode.Pvp;
            pvpTarget.PvpMode = PvpMode.Pve;
            zone.Attacks.TryEnqueue(new AttackIntent(pveAttacker.Id, pvpTarget.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPvpTargetPve);

            pvpTarget.Action = EntityAction.Dead;
            zone.Attacks.TryEnqueue(new AttackIntent(pveAttacker.Id, pvpTarget.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPvpTargetDead);

            zone.Attacks.TryEnqueue(new AttackIntent(pveAttacker.Id, pveAttacker.Id));
            zone.Tick();
            Assert.Equal(1, zone.RejectedPvpSelf);
            Assert.Equal(0, zone.PvpAttacksOk);
        }

        [Fact]
        public void Muerte_Pvp_No_Da_Experiencia_Ni_Penaliza_Y_Respeta_Reaparicion()
        {
            Zone zone = NewZone(
                withZones: false,
                vitals: new PlayerVitalsSettings(new Vec2(3, 3), 0.5, 0, 0));
            PlayerEntity attacker = zone.SpawnPlayer(
                1, new Vec2(5, 5), new Vec2(0.4f, 0.4f), 8, experience: 50);
            PlayerEntity victim = zone.SpawnPlayer(
                2, new Vec2(6, 5), new Vec2(0.4f, 0.4f), 8, experience: 50);
            attacker.PvpMode = PvpMode.Pvp;
            victim.PvpMode = PvpMode.Pvp;
            victim.Health = 1;
            zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, victim.Id));

            zone.Tick();

            Assert.Equal(EntityAction.Dead, victim.Action);
            Assert.Equal(1, zone.PvpKills);
            Assert.Equal(50, victim.Experience);
            Assert.Equal(50, attacker.Experience);
            Assert.Equal(0, zone.PenalizedDeaths);

            for (int i = 0; i < 10; i++)
            {
                zone.Tick();
            }

            Assert.Equal(EntityAction.None, victim.Action);
            Assert.Equal(new Vec2(3, 3), victim.Position);
            Assert.Equal(1, zone.PlayerRespawns);
        }

        [Theory]
        [InlineData(5)]
        [InlineData(21)]
        public void Fuera_De_Segura_Rechaza_Cambio_Pero_Modo_Igual_No_Emite_Rechazo(int x)
        {
            Zone zone = NewZone();
            PlayerEntity player = zone.SpawnPlayer(1, new Vec2(x, 5), new Vec2(0.4f, 0.4f), 8);
            var reasons = new List<PvpModeRejectReason>();
            zone.PvpModeRejected += (_, reason) => reasons.Add(reason);
            RequestMode(zone, player, PvpMode.Pve);
            zone.Tick();
            Assert.Equal(1, zone.PvpModeUnchanged);
            Assert.Empty(reasons);
            RequestMode(zone, player, PvpMode.Pvp);
            zone.Tick();
            Assert.Equal(PvpModeRejectReason.NotInSafeZone, Assert.Single(reasons));
            Assert.Equal(1, zone.PvpRejectedNotInSafeZone);
        }

        [Theory]
        [InlineData("atacante_pve")]
        [InlineData("objetivo_pve")]
        [InlineData("atacante_segura")]
        [InlineData("objetivo_segura")]
        [InlineData("muerto")]
        [InlineData("propio")]
        public void Rechazos_Pvp_No_Hacen_Daño_Ni_Consumen_Recarga(string caso)
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(9.5f, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity target = zone.SpawnPlayer(2, new Vec2(9, 5), new Vec2(0.4f, 0.4f), 8);
            attacker.PvpMode = target.PvpMode = PvpMode.Pvp;
            if (caso == "atacante_pve") attacker.PvpMode = PvpMode.Pve;
            if (caso == "objetivo_pve") target.PvpMode = PvpMode.Pve;
            if (caso == "atacante_segura") attacker.Position = new Vec2(10, 5);
            if (caso == "objetivo_segura") target.Position = new Vec2(10, 5);
            if (caso == "muerto") target.Action = EntityAction.Dead;
            if (caso == "propio") target = attacker;
            int health = target.Health;
            zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, target.Id));
            zone.Tick();
            Assert.Equal(health, target.Health);
            Assert.Equal(-1, attacker.LastAttackTick);
            Assert.Equal(0, zone.CombatEvents.Count);
            Assert.Equal(1, zone.RejectedPvpAttackerPve + zone.RejectedPvpTargetPve +
                zone.RejectedPvpSafeZone + zone.RejectedPvpTargetDead + zone.RejectedPvpSelf);
        }

        [Fact]
        public void Muerto_En_Arena_Reaparece_En_Segura_Y_Cancela_El_Forzado()
        {
            Zone zone = NewZone(vitals: new PlayerVitalsSettings(new Vec2(11, 5), 0.5, 0, 0));
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(21, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity victim = zone.SpawnPlayer(2, new Vec2(22, 5), new Vec2(0.4f, 0.4f), 8);
            victim.Health = 1;
            zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, victim.Id));
            zone.Tick();
            Assert.True(victim.IsDead);
            for (int i = 0; i < 10; i++) zone.Tick();
            Assert.False(victim.IsDead);
            Assert.Equal(ZoneKind.Safe, victim.ZoneKind);
            Assert.Equal(0, victim.PvpForcedTicksLeft);
            Assert.False(victim.PvpEffective);
            zone.Tick();
            Assert.Equal(0, victim.PvpForcedTicksLeft);
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public void Entrar_O_Salir_De_Arena_Permite_Daño_En_El_Mismo_Tick(bool entrando)
        {
            Zone zone = NewZone();
            PlayerEntity attacker = zone.SpawnPlayer(1, new Vec2(19.5f, 5), new Vec2(0.4f, 0.4f), 8);
            PlayerEntity target = zone.SpawnPlayer(2, new Vec2(entrando ? 19.8f : 20.2f, 5), new Vec2(0.4f, 0.4f), 8);
            attacker.PvpMode = PvpMode.Pvp;
            zone.Tick();
            target.Position = new Vec2(entrando ? 20.2f : 19.8f, 5);
            zone.Attacks!.TryEnqueue(new AttackIntent(attacker.Id, target.Id));
            zone.Tick();
            Assert.Equal(1, zone.PvpAttacksOk);
            Assert.True(target.PvpEffective);
        }
    }
}
