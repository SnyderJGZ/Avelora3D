using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Avelora.Server.Entities;
using Avelora.Server.Items;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Maps;
using Avelora.Shared.Protocol;
using Xunit;

namespace Avelora.Tests
{
    public sealed class I17Tests
    {
        private static SkillDefinition Skill(
            int id = 4001,
            string name = "Golpe",
            int classId = 1,
            int level = 1,
            int cost = 30,
            double cooldown = 6,
            float range = 1.2f,
            int damage = 22,
            string type = "physical")
        {
            return new SkillDefinition
            {
                Id = id,
                Name = name,
                ClassId = classId,
                RequiredLevel = level,
                ResourceCost = cost,
                CooldownSeconds = cooldown,
                Range = range,
                Damage = damage,
                DamageType = type,
            };
        }

        private static SkillCatalog Catalog(ClassCatalog classes, params SkillDefinition[] skills)
        {
            var catalog = new SkillCatalog { Skills = new List<SkillDefinition>(skills) };
            catalog.Validate(classes);
            return catalog;
        }

        private static ZoneMap SafeZones(MapCollisionGrid map) => ZoneMap.FromDefinitions(new[]
        {
            new ZoneDefinition
            {
                Id = 1,
                Name = "Segura",
                Kind = "safe",
                Rects = new List<ZoneRectDefinition> { new ZoneRectDefinition { X = 10, Y = 4, W = 4, H = 4 } },
            },
        }, map);

        private static Zone NewZone(
            SkillCatalog? skills = null,
            MapCollisionGrid? map = null,
            ClassCatalog? classes = null,
            NpcCatalog? npcs = null,
            ZoneMap? zones = null)
        {
            return new Zone(
                1,
                map ?? MapCollisionGrid.AllWalkable(30, 20),
                new EntityStore(),
                new EntityIdAllocator(),
                new InputQueue(128),
                new MovementSystem(4f, SimulationConstants.FixedDt),
                128,
                4,
                new InterestSettings(30f, 8f, 0f),
                npcs,
                12345,
                new AttackQueue(128),
                new CombatSettings(1f),
                items: null,
                itemActions: new ItemActionQueue(64),
                classes: classes,
                zones: zones,
                pvpModeChangeTicks: 20,
                pvpModeChangeFromPvpTicks: 40,
                pvpForcedTicks: 40,
                skills: skills);
        }

        private static NpcEntity AddNpc(
            Zone zone,
            int id,
            Vec2 position,
            int health = 1000,
            int defense = 0,
            int magicResist = 0)
        {
            var npc = new NpcEntity(
                id,
                2001,
                0f,
                position,
                0f,
                nextDirectionChangeTick: long.MaxValue,
                health,
                200,
                spawnerIndex: -1,
                defense: defense,
                magicResist: magicResist)
            {
                Position = position,
                HalfSize = new Vec2(0.35f, 0.35f),
            };

            zone.AddEntity(npc);
            return npc;
        }

        private static void Use(Zone zone, PlayerEntity player, int targetId, ushort skillId) =>
            Assert.True(zone.Attacks!.TryEnqueue(new AttackIntent(player.Id, targetId, skillId)));

        private static int ExpectedDamage(
            ClassCatalog classes,
            PlayerEntity player,
            int baseDamage,
            int defenseOrResistance)
        {
            DerivedCharacterStats stats = CharacterStats.Calculate(
                classes, player.ClassId, player.Level, player.AllocStr, player.AllocDex, player.AllocInt, player.AllocVit);
            CharacterClass characterClass = classes.Find(player.ClassId)!;
            int primary = characterClass.Primary == "str"
                ? stats.Str
                : characterClass.Primary == "dex"
                    ? stats.Dex
                    : stats.Int;
            int bonus = (int)decimal.Floor(primary * classes.DamagePerPoint);
            return CharacterStats.MitigateDamage(baseDamage + bonus, defenseOrResistance);
        }


        private static SkillDefinition Area() => new SkillDefinition { Id = 4005, Name = "área", ClassId = 2,
            RequiredLevel = 1, ResourceCost = 35, CooldownSeconds = 8, Range = 6, Damage = 14,
            DamageType = "magical", Radius = 2.5f, MaxTargets = 3 };
        private static SkillDefinition Cura() => new SkillDefinition { Id = 4006, Name = "Cura", ClassId = 4,
            RequiredLevel = 1, ResourceCost = 25, CooldownSeconds = 3, Range = 6, Damage = 20,
            DamageType = "magical", Effect = "heal" };
        private static Zone Zona(MapCollisionGrid? map = null, ZoneMap? zones = null) =>
            NewZone(Catalog(ClassCatalog.Default, Area(), Cura()), map, zones: zones);
        private static PlayerEntity Jugador(Zone z, int id = 1, byte clase = 4) =>
            z.SpawnPlayer(id, new Vec2(8,10), new Vec2(.4f,.4f), 8, classId: clase);

        [Fact]
        public void Catalogo_Del_Repo_Tiene_Area_Y_Cura()
        {
            var c = SkillCatalog.Load(Path.Combine(AppContext.BaseDirectory, "skills.json"), ClassCatalog.Default);
            Assert.Equal(10, c.Skills!.Count);
            Assert.Equal(2.5f, c.Find(4005)!.Radius);
            Assert.Equal(6, c.Find(4005)!.MaxTargets);
            Assert.True(c.Find(4006)!.IsHeal);
        }

        [Theory]
        [InlineData("fire",0,0,20)]
        [InlineData("damage",.4f,3,20)]
        [InlineData("damage",8.1f,3,20)]
        [InlineData("damage",0,3,20)]
        [InlineData("damage",2,1,20)]
        [InlineData("damage",2,17,20)]
        [InlineData("heal",2,3,20)]
        [InlineData("heal",0,0,0)]
        [InlineData("damage",float.NaN,3,20)]
        [InlineData("damage",float.PositiveInfinity,3,20)]
        public void Catalogo_Rechaza_Combinaciones_Invalidas(string effect,float radius,int max,int damage)
        {
            var s = Cura(); s.Effect=effect; s.Radius=radius; s.MaxTargets=max; s.Damage=damage;
            Assert.Throws<InvalidOperationException>(() => Catalog(ClassCatalog.Default,s));
        }

        [Fact]
        public void Area_Incluye_Borde_Pero_No_Exterior_Y_Emite_Eventos()
        {
            var z=Zona(); var p=Jugador(z,clase:2);
            var centro=AddNpc(z,100,new Vec2(10,10));
            var borde=AddNpc(z,101,new Vec2(12.5f,10));
            var fuera=AddNpc(z,102,new Vec2(12.51f,10));
            Use(z,p,100,4005); z.Tick();
            Assert.True(centro.Health<1000); Assert.True(borde.Health<1000); Assert.Equal(1000,fuera.Health);
            Assert.Equal(2,z.CombatEvents.Count); Assert.Equal(1,z.AreaTargetsHit);
        }

        [Fact]
        public void Area_Elige_Cercanos_Con_Empate_Estable_Y_Cobra_Una_Vez()
        {
            var z=Zona(); var p=Jugador(z,clase:2); int mana=p.Mana;
            AddNpc(z,100,new Vec2(10,10));
            var lejos=AddNpc(z,101,new Vec2(12,10));
            var cercano=AddNpc(z,102,new Vec2(10.5f,10));
            var empate=AddNpc(z,103,new Vec2(9.5f,10));
            var otro=AddNpc(z,104,new Vec2(10,10.5f));
            Use(z,p,100,4005); z.Tick();
            Assert.True(cercano.Health<1000); Assert.True(empate.Health<1000);
            Assert.Equal(1000,lejos.Health); Assert.Equal(1000,otro.Health);
            Assert.Equal(2,z.AreaTargetsHit); Assert.Equal(1,z.AreaCapped);
            Assert.Equal(mana-35,p.Mana); Assert.Equal(160,p.SkillReadyTicks[4005]);
        }

        [Fact]
        public void Area_Filtra_Return_Muertos_Pve_Y_Propio_Lanzador()
        {
            var z=Zona(); var p=Jugador(z,clase:2); int vida=p.Health;
            AddNpc(z,100,new Vec2(10,10));
            var retorno=AddNpc(z,101,new Vec2(10,11)); retorno.AiState=NpcAiState.Return;
            var muerto=AddNpc(z,102,new Vec2(10,9)); muerto.Action=EntityAction.Dead;
            var jugador=Jugador(z,2); jugador.Position=new Vec2(10,10);
            Use(z,p,100,4005); z.Tick();
            Assert.Equal(1000,retorno.Health); Assert.Equal(1000,muerto.Health);
            Assert.Equal(jugador.MaxHealth,jugador.Health); Assert.Equal(vida,p.Health);
            Assert.Equal(0,z.AreaTargetsHit);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Area_Respeta_Pvp_Y_Atribuye_Muerte(bool pvp)
        {
            var z=Zona(); var p=Jugador(z,clase:2); if(pvp)p.PvpMode=PvpMode.Pvp;
            AddNpc(z,100,new Vec2(10,10)); var v=Jugador(z,2); v.Position=new Vec2(10,10);
            v.PvpMode=PvpMode.Pvp; v.Health=1;
            Use(z,p,100,4005);z.Tick();
            Assert.Equal(pvp,v.IsDead);Assert.Equal(pvp?1:0,z.PvpKills);
        }

        [Fact]
        public void Area_Marca_Botin_Aggro_Y_Conserva_Dueno_Reciente()
        {
            var z=Zona(); var p=Jugador(z,clase:2); var otro=Jugador(z,2);
            var a=AddNpc(z,100,new Vec2(10,10));var b=AddNpc(z,101,new Vec2(11,10));
            var c=AddNpc(z,102,new Vec2(10,11));c.LootOwnerId=otro.Id;c.LootOwnerLastHitTick=0;
            Use(z,p,100,4005);z.Tick();
            Assert.Equal(p.Id,a.LootOwnerId);Assert.Equal(p.Id,b.LootOwnerId);Assert.Equal(otro.Id,c.LootOwnerId);
            Assert.Equal(NpcAiState.Chase,a.AiState);Assert.Equal(NpcAiState.Chase,b.AiState);
            Assert.Equal(p.Id,b.TargetEntityId);
        }

        [Fact]
        public void Principal_Fuera_De_Alcance_No_Gasta_Ni_Golpea()
        {
            var z=Zona();var p=Jugador(z,clase:2);int mana=p.Mana;var a=AddNpc(z,100,new Vec2(20,10));
            Use(z,p,100,4005);z.Tick();Assert.Equal(1000,a.Health);Assert.Equal(mana,p.Mana);
            Assert.Equal(1,z.SkillsRejected(SkillRejectReason.OutOfRange));Assert.Equal(0,z.AreaTargetsHit);
        }

        [Theory]
        [InlineData(50)] [InlineData(85)]
        public void Cura_Propia_Recorta_Vida_Y_No_Modifica_Estado_De_Combate(int vida)
        {
            var z=Zona();var p=Jugador(z);p.MaxHealth=90;p.Health=vida;int mana=p.Mana;
            p.LastDamageTick=12; var accion=p.Action; var regen=p.RegenAccumulator;
            int target=0;z.SkillUsed+=(_,_,id)=>target=id;
            Use(z,p,p.Id,4006);z.Tick();
            int cantidad=Math.Min(ExpectedDamage(z.Classes,p,20,0),90-vida);
            Assert.Equal(vida+cantidad,p.Health);Assert.Equal(mana-25,p.Mana);Assert.Equal(p.Id,target);
            Assert.Equal(1,z.HealsDone);Assert.Equal(cantidad,z.HealAmount);
            Assert.Equal(12,p.LastDamageTick);Assert.Equal(accion,p.Action);Assert.Equal(regen,p.RegenAccumulator);
            Assert.Equal(CombatEventFlags.Heal,z.CombatEvents[0].Flags);Assert.Equal(cantidad,z.CombatEvents[0].Damage);
        }

        [Fact]
        public void Cura_Vida_Llena_No_Gasta_Ni_Recarga()
        {
            var z=Zona();var p=Jugador(z);int mana=p.Mana;Use(z,p,p.Id,4006);z.Tick();
            Assert.Equal(1,z.SkillsRejected(SkillRejectReason.FullHealth));Assert.Equal(mana,p.Mana);
            Assert.Empty(p.SkillReadyTicks);Assert.Equal(0,z.HealsDone);
        }

        [Theory]
        [InlineData(0)] [InlineData(1)] [InlineData(2)]
        public void Cura_Rechaza_Pnj_Muerto_E_Inexistente(int caso)
        {
            var z=Zona();var p=Jugador(z);int id=999;
            if(caso==0)id=AddNpc(z,100,new Vec2(10,10)).Id;
            if(caso==1){var v=Jugador(z,2);v.Health=0;v.Action=EntityAction.Dead;id=v.Id;}
            Use(z,p,id,4006);z.Tick();Assert.Equal(1,z.SkillsRejected(SkillRejectReason.InvalidTarget));
        }

        [Theory]
        [InlineData(false,false,true)] [InlineData(false,true,false)]
        [InlineData(true,false,true)] [InlineData(true,true,true)]
        public void Cura_Regla_Pvp(bool casterPvp,bool targetPvp,bool acepta)
        {
            var z=Zona();var p=Jugador(z);var v=Jugador(z,2);v.Health=20;
            if(casterPvp)p.PvpMode=PvpMode.Pvp;if(targetPvp)v.PvpMode=PvpMode.Pvp;
            Use(z,p,v.Id,4006);z.Tick();Assert.Equal(acepta?1:0,z.HealsDone);
            Assert.Equal(acepta?0:1,z.SkillsRejected(SkillRejectReason.PvpNotAllowed));
        }

        [Fact]
        public void Cura_En_Segura_No_Cambia_Amenaza()
        {
            var map=MapCollisionGrid.AllWalkable(30,20);var z=Zona(map,SafeZones(map));var p=Jugador(z);var v=Jugador(z,2);
            p.Position=new Vec2(11,5);v.Position=new Vec2(12,5);v.Health=20;
            var n=AddNpc(z,100,new Vec2(12,6));n.AiState=NpcAiState.Chase;n.TargetEntityId=v.Id;
            Use(z,p,v.Id,4006);z.Tick();Assert.Equal(1,z.HealsDone);
            Assert.Equal(NpcAiState.Chase,n.AiState);Assert.Equal(v.Id,n.TargetEntityId);
        }

        [Fact]
        public void Area_No_Atraviesa_Pared_Desde_El_Centro()
        {
            var rows=Enumerable.Repeat(new string('.',30),20).ToArray();rows[2]="....#"+new string('.',25);
            var map=MapCollisionGrid.FromRows(rows);
            var z=Zona(map);var p=Jugador(z,clase:2);p.Position=new Vec2(4.5f,.5f);
            var a=AddNpc(z,100,new Vec2(3.5f,2.5f));var b=AddNpc(z,101,new Vec2(5.5f,2.5f));
            Assert.True(map.HasLineOfSight(p.Position,b.Position));
            Use(z,p,a.Id,4005);z.Tick();Assert.True(a.Health<1000);Assert.Equal(1000,b.Health);
        }

        [Fact]
        public void Area_Omite_Secundario_En_Segura()
        {
            var map=MapCollisionGrid.AllWalkable(30,20);var z=Zona(map,SafeZones(map));var p=Jugador(z,clase:2);
            p.Position=new Vec2(8,5);var centro=AddNpc(z,100,new Vec2(9,5));var seguro=AddNpc(z,101,new Vec2(11,5));
            Use(z,p,centro.Id,4005);z.Tick();Assert.True(centro.Health<1000);Assert.Equal(1000,seguro.Health);
        }

        [Theory]
        [InlineData(false)] [InlineData(true)]
        public void Cura_Rechaza_Alcance_Y_Pared(bool pared)
        {
            var rows=Enumerable.Repeat(new string('.',30),20).ToArray();
            for(int i=1;i<=3;i++)rows[i]="....#"+new string('.',25);
            var map=MapCollisionGrid.FromRows(rows);
            var z=Zona(map);var p=Jugador(z);var v=Jugador(z,2);p.Position=new Vec2(2.5f,2.5f);
            v.Position=new Vec2(pared?6.5f:15.5f,2.5f);v.Health=20;int mana=p.Mana;
            Use(z,p,v.Id,4006);z.Tick();Assert.Equal(1,z.SkillsRejected(pared?SkillRejectReason.NoLineOfSight:SkillRejectReason.OutOfRange));
            Assert.Equal(mana,p.Mana);Assert.Equal(20,v.Health);
        }
    }
}
