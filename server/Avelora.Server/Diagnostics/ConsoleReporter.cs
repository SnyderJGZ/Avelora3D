#nullable enable
using System;
using System.Globalization;
using System.Text;
using Avelora.Server.Configuration;
using Avelora.Server.Entities;
using Avelora.Server.Metrics;
using Avelora.Server.Net;
using Avelora.Server.Npcs;
using Avelora.Server.Progression;
using Avelora.Server.Simulation;
using Avelora.Server.Zones;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Diagnostics
{
    /// <summary>
    /// Informes por consola: arranque, marca periódica y resumen final (guía del núcleo §11.1).
    /// </summary>
    /// <remarks>
    /// Todo lo que sale por aquí es para una persona. Por eso usa números con separador decimal
    /// fijo y no depende de la configuración regional: los informes deben poder compararse entre
    /// máquinas y entre ejecuciones.
    ///
    /// Los jugadores se leen de <see cref="Zone.Players"/> (una copia publicada por el tick), no del
    /// almacén de entidades: este hilo no puede recorrer lo que la simulación está modificando.
    /// </remarks>
    public static class ConsoleReporter
    {
        /// <summary>Escribe la cabecera de arranque con la configuración efectiva.</summary>
        public static void WriteStartup(
            string configPath,
            string mapPath,
            MapCollisionGrid map,
            string mapHash,
            ServerConfig config,
            int port,
            string? databasePath,
            string serverKeyFingerprint,
            Avelora.Server.Npcs.NpcCatalog? npcs = null,
            LevelTable? levels = null,
            ClassCatalog? classes = null,
            ZoneMap? zones = null,
            SkillCatalog? skills = null)
        {
            Console.WriteLine("Avelora · servidor autoritativo (Etapa F: persistencia)");
            Console.WriteLine("  Configuración : " + configPath);
            Console.WriteLine("  Mapa          : " + mapPath);
            Console.WriteLine(
                "  Rejilla       : " + map.Width + "x" + map.Height +
                " (origen " + map.OriginX + "," + map.OriginY + ")  huella " + ShortHash(mapHash));
            Console.WriteLine(
                "  Persistencia  : " + (databasePath == null
                    ? "desactivada"
                    : "SQLite " + databasePath + " (guardado cada " +
                      Invariant(config.SaveIntervalSeconds, "0.#") + " s)"));
            Console.WriteLine(
                "  Tick          : " + config.TickRate + " Hz (paso fijo " +
                Invariant(SimulationConstants.FixedDt, "0.####") + " s = " +
                Invariant(SimulationConstants.StepMs, "0.###") + " ms)");
            Console.WriteLine(
                "  Clave         : RSA-2048, huella " + serverKeyFingerprint);
            Console.WriteLine(
                "  Red           : UDP 0.0.0.0:" + port + ", hasta " + config.MaxPlayers +
                " jugadores (" + config.MaxConnections + " conexiones, " +
                config.MaxConnectionsPerIp + " por IP)");
            Console.WriteLine(
                "  Aparición     : (" + Invariant(config.SpawnX, "0.##") + "; " + Invariant(config.SpawnY, "0.##") +
                ")  medio=(" + Invariant(config.PlayerHalfWidth, "0.##") + "; " +
                Invariant(config.PlayerHalfHeight, "0.##") + ")  velocidad=" +
                Invariant(config.PlayerSpeed, "0.##") + "/s");
            Console.WriteLine(
                "  Protocolo     : v" + ProtocolConstants.Version + ", snapshot cada " + config.SnapshotEveryTicks +
                " tick(s), " + config.MaxMessagesPerSecondPerConnection + " msgs/s por conexión, saludo en " +
                Invariant(config.HandshakeTimeoutSeconds, "0.#") + " s, " +
                "inundación sostenida " + config.MaxSecondsOverMessageLimit + " s por minuto");
            Console.WriteLine(
                "  Interés       : radio " + Invariant(config.InterestRadius, "0.##") +
                " tiles, celda " + Invariant(config.InterestCellSize, "0.##") +
                ", histéresis " + Invariant(config.InterestHysteresis, "0.##") + " tiles");
            Console.WriteLine(
                "  Enemigos      : " + (npcs == null
                    ? "ninguno"
                    : (npcs.Types?.Count ?? 0) + " tipo(s), " + (npcs.Spawners?.Count ?? 0) +
                      " spawner(s), " + npcs.TotalEnemies + " enemigo(s), rangos: " + DescribeRanks(npcs)));
            Console.WriteLine("  Zonas         : " + DescribeZones(zones));
            Console.WriteLine(
                "  PvP           : cambio tras " + Invariant(config.PvpModeChangeSeconds, "0.#") +
                " s en zona segura, desde PvP " + Invariant(config.PvpModeChangeFromPvpSeconds, "0.#") +
                " s, forzado " + Invariant(config.PvpForcedSeconds, "0.#") + " s");
            Console.WriteLine(
                "  Combate       : daño, alcance y recarga por clase o arma, tolerancia +" +
                Invariant(config.AttackRangeTolerance, "0.##") + " tiles, cola de ataques " +
                config.MaxQueuedAttacks);
            Console.WriteLine(
                "  Vida jugador  : máx. por clase, reaparición " + Invariant(config.PlayerRespawnSeconds, "0.#") + " s (" +
                (long)Math.Ceiling(config.PlayerRespawnSeconds * SimulationConstants.TickRate) +
                " ticks), regeneración " + Invariant(config.PlayerRegenPerSecond, "0.##") + "/s tras " +
                Invariant(config.PlayerRegenDelaySeconds, "0.#") + " s sin daño");
            LevelTable levelTable = levels ?? LevelTable.Default;
            Console.WriteLine(
                "  Niveles       : máximo " + levelTable.MaxLevel + ", puntos/nivel " + levelTable.PointsPerLevel +
                ", clases " + (classes?.Classes?.Count ?? ClassCatalog.Default.Classes?.Count ?? 0));
            Console.WriteLine(
                "  Habilidades   : " + (skills == null
                    ? "desactivadas"
                    : (skills.Skills?.Count ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            Console.WriteLine(
                "  Chat          : zona, máx " + config.ChatMaxBytes + " B UTF-8, ráfaga " +
                config.ChatBurst + ", reposición " + Invariant(config.ChatPerSecond, "0.##") + "/s");
            Console.WriteLine(
                "  Objetos       : huecos " + config.InventorySlots +
                ", catálogo " + (string.IsNullOrWhiteSpace(config.ItemsPath) ? "desactivado" : config.ItemsPath) +
                ", suelo " + Invariant(config.GroundItemSeconds, "0.#") + " s (" +
                (long)Math.Ceiling(config.GroundItemSeconds * SimulationConstants.TickRate) +
                " ticks), máx " + config.MaxGroundItems +
                ", recoger " + Invariant(config.PickupRange, "0.##") + " (+" +
                Invariant(config.AttackRangeTolerance, "0.##") + ") tiles" +
                ", poción cd " + Invariant(config.PotionCooldownSeconds, "0.#") + " s (" +
                (long)Math.Ceiling(config.PotionCooldownSeconds * SimulationConstants.TickRate) +
                " ticks), cola " + config.MaxQueuedItemActions +
                ", reserva botín " + Invariant(config.LootReserveSeconds, "0.#") + " s, marca " +
                Invariant(config.LootTagSeconds, "0.#") + " s");
            Console.WriteLine(
                "  Límites       : " + config.MaxIntentsPerTick + " inputs/tick, " +
                config.MaxIntentsPerPlayerPerTick + "/jugador/tick, cola de jugador " +
                config.InputBufferCapacity + ", cola de entrada " + config.MaxQueuedIntents +
                ", cola de salida " + config.MaxQueuedOutboundMessages);
            Console.WriteLine(
                "  Informes      : ventana de " + config.MetricsWindowTicks + " ticks cada " +
                Invariant(config.ReportIntervalSeconds, "0.#") + " s");
            Console.WriteLine();
        }

        /// <summary>Escribe una marca de seguimiento.</summary>
        public static void WriteReport(double elapsedSeconds, Zone zone, TickMetrics metrics, NetworkStats network)
        {
            TickMetricsSnapshot snapshot = metrics.Snapshot();

            Console.WriteLine(
                "[t=" + Invariant(elapsedSeconds, "0.0").PadLeft(6) + "s] " +
                "tick=" + zone.TickCount +
                "  jug=" + zone.Players.Count +
                "  tickMs media=" + Invariant(snapshot.MeanTickMs, "0.000") +
                " p99=" + Invariant(snapshot.P99TickMs, "0.000") +
                " max=" + Invariant(snapshot.MaxTickMs, "0.000") +
                "  sobre_" + Invariant(snapshot.BudgetMs, "0") + "ms=" + snapshot.OverBudgetTicks +
                "  descartado=" + Invariant(snapshot.DiscardedMs, "0.0") + "ms");

            Console.WriteLine(
                "            red: conn=" + network.Connections +
                " msgs=" + network.MessagesReceived +
                " rech=" + network.MessagesRejected +
                " saludo=" + network.HandshakesRejected +
                " limite=" + network.RateLimited +
                " cortes=" + network.RateLimitedDisconnects +
                " cola=" + network.DroppedOutbound +
                " snaps=" + network.SnapshotsSent +
                 " ents=" + network.EntitiesSent +
                 " enviados=" + network.SnapshotBytes +
                 " combate_msgs=" + network.CombatEventMessages +
                 " combate_entradas=" + network.CombatEventEntries +
                 " combate_bytes=" + network.CombatEventBytes +
                  " botin_msgs=" + network.LootGainedMessages +
                  " botin_bytes=" + network.LootGainedBytes +
                  " zona_msgs=" + network.ZoneChangedMessages +
                  " zona_bytes=" + network.ZoneChangedBytes +
                 " combate_recortados=" + network.CombatEventsTrimmed +
                 " combate_descartados=" + zone.CombatEventsDropped +
                 " recibidos=" + network.BytesReceived +
                "  inputs ok=" + zone.Inputs.Received +
                " redundantes=" + zone.RedundantDuplicates +
                " fuera_de_orden=" + zone.RejectedOutOfOrder +
                " buffer_lleno=" + zone.RejectedBufferFull +
                " tope_jugador=" + zone.RejectedPerPlayerLimit +
                " ajena=" + zone.RejectedUnknownEntity +
                " direccion=" + zone.Inputs.RejectedInvalidDirection);

            Console.WriteLine(
                "            combate: ataques_ok=" + zone.AttacksOk +
                " ataques_con_arma=" + zone.AttacksWithWeapon +
                " ataque_rech_objetivo=" + zone.RejectedAttackTarget +
                 " ataque_rech_distancia=" + zone.RejectedAttackDistance +
                 " ataque_rech_vision=" + zone.RejectedAttackLineOfSight +
                  " ataque_rech_regreso=" + zone.RejectedAttackEvading +
                  " ataque_rech_zona_segura=" + zone.RejectedAttackSafeZone +
                  " pvp_ataques_ok=" + zone.PvpAttacksOk +
                  " pvp_muertes=" + zone.PvpKills +
                  " ataque_rech_pvp_segura=" + zone.RejectedPvpSafeZone +
                  " ataque_rech_pvp_atacante_pve=" + zone.RejectedPvpAttackerPve +
                  " ataque_rech_pvp_objetivo_pve=" + zone.RejectedPvpTargetPve +
                  " ataque_rech_pvp_muerto=" + zone.RejectedPvpTargetDead +
                  " ataque_rech_pvp_propio=" + zone.RejectedPvpSelf +
                  " pvp_cambios_modo=" + zone.PvpModeChanges +
                  " pvp_modo_igual=" + zone.PvpModeUnchanged +
                  " pvp_rech_fuera_segura=" + zone.PvpRejectedNotInSafeZone +
                  " pvp_rech_pronto=" + zone.PvpRejectedTooSoon +
                  " pvp_estado_msgs=" + zone.PvpStatusMessages +
                 " aggro_cancelado_zona=" + zone.NpcAggroCancelledSafeZone +
                 " cambios_zona=" + zone.ZoneChanges +
                 " entradas_zona_segura=" + zone.SafeZoneEntries +
                 " ataques_distancia=" + zone.AttacksRanged +
                 " ataque_rech_cooldown=" + zone.RejectedAttackCooldown +
                 " ataque_rech_muerto=" + zone.RejectedAttackDead +
                 " cambios_dueno=" + zone.LootOwnerChanges +
                " ataques_cola_llena=" + (zone.Attacks?.RejectedQueueFull ?? 0) +
                 " npc_golpes=" + zone.NpcHits +
                 " pnj_persecucion_abandonada=" + zone.NpcChaseGiveUps +
                 " pnj_regreso_forzado=" + zone.NpcReturnTeleports +
                 " npc_muertes=" + zone.NpcDeaths +
                " npc_reapariciones=" + zone.NpcRespawns +
                " elites_aparecidos=" + zone.EliteAppearances +
                " elites_muertos=" + zone.EliteDeaths +
                DescribeRankMetrics(zone) +
                 " experiencia_otorgada=" + zone.ExperienceGranted +
                 " oro_otorgado=" + zone.GoldGranted +
                 " pnj_sin_dueno=" + zone.NpcDeathsWithoutOwner +
                " subidas_nivel=" + zone.LevelUps +
                " jugador_muertes=" + zone.PlayerDeaths +
                " muertes_penalizadas=" + zone.PenalizedDeaths +
                " xp_perdida_muerte=" + zone.ExperienceLost +
                " jugador_reapariciones=" + zone.PlayerRespawns);

            Console.WriteLine(
                "            objetos: objetos_soltados=" + zone.ItemsDropped +
                " objetos_recogidos=" + zone.ItemsPickedUp +
                " objetos_caducados=" + zone.ItemsExpired +
                 " objetos_descartados=" + zone.ItemsDiscarded +
                 " botin_directo=" + zone.ItemsLootedDirect +
                 " botin_suelo_lleno=" + zone.ItemsLootedToGround +
                 " botin_lineas_recortadas=" + zone.LootLinesTrimmed +
                " objetos_en_suelo=" + zone.GroundItemCount +
                " recoger_rech_muerto=" + zone.RejectedPickupDead +
                " recoger_rech_objeto=" + zone.RejectedPickupItem +
                " recoger_rech_distancia=" + zone.RejectedPickupDistance +
                 " recoger_rech_lleno=" + zone.RejectedPickupFull +
                 " recogida_rech_reservada=" + zone.RejectedPickupReserved +
                " usar_rech_muerto=" + zone.RejectedUseDead +
                " usar_rech_objeto=" + zone.RejectedUseItem +
                " usar_rech_lleno=" + zone.RejectedUseFull +
                " usar_rech_recarga=" + zone.RejectedUseCooldown +
                " pociones_usadas=" + zone.PotionsUsed +
                " equipados=" + zone.ItemsEquipped +
                " desequipados=" + zone.ItemsUnequipped +
                " intercambios_equipo=" + zone.EquipmentSwaps +
                " movidos=" + zone.ItemsMoved +
                " divididos=" + zone.ItemsSplit +
                " juntados=" + zone.ItemsMerged +
                " intercambiados=" + zone.ItemsSwapped +
                " destruidos=" + zone.ItemsDestroyed +
                " equipo_rech_muerto=" + zone.RejectedEquipDead +
                " equip_rech_vacio=" + zone.RejectedEquipEmpty +
                " equip_rech_objeto=" + zone.RejectedEquipItem +
                " equipo_rech_vacio=" + zone.RejectedUnequipEmpty +
                " equipo_rech_lleno=" + zone.RejectedEquipFull +
                " equip_rech_clase=" + zone.RejectedEquipClass +
                " equip_rech_nivel=" + zone.RejectedEquipLevel +
                " equip_rech_dosmanos=" + zone.RejectedEquipTwoHand +
                " equipo_corregido_carga=" + zone.EquipmentCorrectedOnLoad +
                " equipo_aviso_carga=" + zone.EquipmentLoadWarnings +
                " equipo_rech_hueco=" + zone.RejectedEquipmentSlot +
                " equipo_rech_destino=" + zone.RejectedEquipmentDestination +
                " mover_rech_muerto=" + zone.RejectedMoveDead +
                " mover_rech_hueco=" + zone.RejectedMoveSlot +
                " mover_rech_mismo=" + zone.RejectedMoveSameSlot +
                " mover_rech_vacio=" + zone.RejectedMoveEmpty +
                " mover_rech_cantidad=" + zone.RejectedMoveCount +
                " mover_rech_objeto=" + zone.RejectedMoveItem +
                " mover_rech_lleno=" + zone.RejectedMoveFull +
                " destruir_rech_muerto=" + zone.RejectedDestroyDead +
                " destruir_rech_hueco=" + zone.RejectedDestroySlot +
                " destruir_rech_vacio=" + zone.RejectedDestroyEmpty +
                " destruir_rech_cantidad=" + zone.RejectedDestroyCount +
                " destruir_rech_objeto=" + zone.RejectedDestroyItem +
                " acciones_objeto_cola_llena=" + zone.RejectedItemActionQueueFull);

            Console.WriteLine(
                "            atributos: stats_repartos=" + zone.StatsAllocations +
                " stats_puntos=" + zone.StatsPointsAllocated +
                " stats_rech_muerto=" + zone.RejectedStatsDead +
                " stats_rech_vacio=" + zone.RejectedStatsEmpty +
                " stats_rech_exceso=" + zone.RejectedStatsExcess +
                " reajustes=" + zone.PointResets +
                " reajuste_rech_muerto=" + zone.RejectedResetDead +
                " reajuste_rech_vacio=" + zone.RejectedResetEmpty +
                " reajuste_rech_objeto=" + zone.RejectedResetItem);

            Console.WriteLine(
                "            cuenta: login_ok=" + network.LoginsSucceeded +
                " login_rech=" + network.LoginsRejected +
                " guardados=" + network.CharactersSaved +
                " fallos=" + network.SaveFailures +
                " pendientes=" + network.PendingSaves);

            Console.WriteLine(
                "            chat: chat_recibidos=" + network.ChatReceived +
                " chat_entregados=" + network.ChatDelivered +
                " chat_rech_invalido=" + network.ChatRejectedInvalid +
                " chat_rech_largo=" + network.ChatRejectedLong +
                " chat_rech_rapido=" + network.ChatRejectedFast);
            Console.WriteLine(
                "            pvp: pvp_estado_msgs=" + network.PvpStatusMessages +
                " pvp_estado_bytes=" + network.PvpStatusBytes +
                " pvp_rech_msgs=" + network.PvpRejectedMessages);
            Console.WriteLine(
                "            habilidades: hab_usadas=" + zone.SkillsUsed +
                " hab_area_objetivos=" + zone.AreaTargetsHit + " hab_area_tope=" + zone.AreaCapped +
                " hab_curas=" + zone.HealsDone + " hab_cura_total=" + zone.HealAmount +
                " hab_rechazadas=" + TotalSkillRejections(zone) +
                " recurso_msgs=" + network.ResourceMessages +
                " recurso_bytes=" + network.ResourceBytes +
                " est_lento=" + zone.StatusesApplied(StatusKind.Slow) +
                " est_escudo=" + zone.StatusesApplied(StatusKind.Shield) +
                " est_absorbido=" + zone.ShieldAbsorbed +
                " hab_catalogo_bytes=" + network.SkillCatalogBytes);

            if (network.SaveFailures > 0 && !string.IsNullOrEmpty(network.SaveLastError))
            {
                Console.WriteLine("            error de guardado: " + network.SaveLastError);
            }

            Console.WriteLine(
                "            memoria: GC " + GC.CollectionCount(0) + "/" + GC.CollectionCount(1) + "/" +
                GC.CollectionCount(2) + "  mem=" + Invariant(GC.GetTotalMemory(false) / 1048576.0, "0.0") + "MB");

            // Ver a los jugadores moverse es la forma más rápida de saber que el tick hace algo.
            Console.WriteLine("            jugadores: " + DescribePlayers(zone));
        }

        /// <summary>Suma de rechazos de habilidad de todos los motivos (I-16).</summary>
        private static long TotalSkillRejections(Zone zone)
        {
            long total = 0;
            for (int reason = (int)SkillRejectReason.UnknownSkill; reason <= (int)SkillRejectReason.FullHealth; reason++)
            {
                total += zone.SkillsRejected((SkillRejectReason)reason);
            }

            return total;
        }

        /// <summary>Posición y último input aplicado de los jugadores de la zona.</summary>
        private static string DescribePlayers(Zone zone)
        {
            const int MaxShown = 4;

            var text = new StringBuilder();
            int shown = 0;
            int total = zone.Players.Count;

            for (int i = 0; i < total; i++)
            {
                if (shown >= MaxShown)
                {
                    break;
                }

                PlayerInfo player = zone.Players[i];

                if (shown > 0)
                {
                    text.Append("  ");
                }

                text.Append("ent").Append(player.Id)
                    .Append(" pos=(").Append(Invariant(player.X, "0.000")).Append("; ")
                    .Append(Invariant(player.Y, "0.000")).Append(')')
                    .Append(" dir=(").Append(player.DirectionX).Append(',').Append(player.DirectionY).Append(')')
                    .Append(" seq=").Append(player.LastProcessedSequence);
                shown++;
            }

            if (total > shown)
            {
                text.Append("  (+").Append(total - shown).Append(" más)");
            }

            return total == 0 ? "(ninguno)" : text.ToString();
        }

        /// <summary>Escribe el resumen final de la ejecución.</summary>
        public static void WriteSummary(
            double elapsedSeconds,
            Zone zone,
            TickMetrics metrics,
            NetworkStats network,
            int exitCode)
        {
            TickMetricsSnapshot snapshot = metrics.Snapshot();
            double expected = elapsedSeconds * SimulationConstants.TickRate;

            Console.WriteLine();
            Console.WriteLine("Resumen");
            Console.WriteLine("  Duración        : " + Invariant(elapsedSeconds, "0.0") + " s");
            Console.WriteLine(
                "  Ticks           : " + snapshot.TotalTicks + " (esperados ~" + Invariant(expected, "0") +
                ", desviación " + Invariant(Percent(snapshot.TotalTicks, expected), "0.00") + " %)");
            Console.WriteLine(
                "  Duración de tick: media " + Invariant(snapshot.MeanTickMs, "0.000") + " ms, p99 " +
                Invariant(snapshot.P99TickMs, "0.000") + " ms, peor " + Invariant(snapshot.MaxTickMs, "0.000") + " ms");
            Console.WriteLine(
                "  Presupuesto     : " + snapshot.OverBudgetTicks + " ticks por encima de " +
                Invariant(snapshot.BudgetMs, "0") + " ms, " + Invariant(snapshot.DiscardedMs, "0.0") +
                " ms descartados por el tope anti-espiral");
            Console.WriteLine(
                "  Red             : " + network.PlayersJoined + " altas, " + network.PlayersLeft + " bajas, " +
                network.MessagesReceived + " mensajes, " + network.MessagesRejected + " rechazados, " +
                network.HandshakesRejected + " saludos rechazados, " + network.RateLimited + " limitados (" +
                network.RateLimitedDisconnects + " cortes por abuso), " +
                 network.SnapshotsSent + " snapshots, " + network.EntitiesSent + " entidades, " +
                  network.SnapshotBytes + " bytes enviados, combate_msgs=" + network.CombatEventMessages +
                  ", combate_entradas=" + network.CombatEventEntries +
                  ", combate_bytes=" + network.CombatEventBytes +
                  ", combate_recortados=" + network.CombatEventsTrimmed +
                   ", botin_msgs=" + network.LootGainedMessages +
                   ", botin_bytes=" + network.LootGainedBytes +
                   ", zona_msgs=" + network.ZoneChangedMessages +
                   ", zona_bytes=" + network.ZoneChangedBytes +
                 ", combate_descartados=" + zone.CombatEventsDropped + ", " + network.BytesReceived +
                " bytes recibidos, " + network.DroppedOutbound + " descartados en cola");
            Console.WriteLine(
                "  Inputs          : " + zone.Inputs.Received + " ok, " +
                zone.RedundantDuplicates + " redundantes, " + zone.RejectedOutOfOrder +
                " fuera de orden, " + zone.RejectedPerPlayerLimit + " por tope de jugador, " +
                zone.RejectedBufferFull + " por búfer lleno");
            Console.WriteLine(
                "  Combate         : " + zone.AttacksOk + " ataques válidos, " +
                zone.AttacksWithWeapon + " ataques con arma, " +
                zone.RejectedAttackTarget + " rechazados por objetivo, " +
                 zone.RejectedAttackDistance + " por distancia, " +
                  "ataque_rech_vision=" + zone.RejectedAttackLineOfSight + ", " +
                  "ataque_rech_regreso=" + zone.RejectedAttackEvading + ", " +
                   "ataque_rech_zona_segura=" + zone.RejectedAttackSafeZone + ", " +
                   "pvp_ataques_ok=" + zone.PvpAttacksOk + ", " +
                   "pvp_muertes=" + zone.PvpKills + ", " +
                   "ataque_rech_pvp_segura=" + zone.RejectedPvpSafeZone + ", " +
                   "ataque_rech_pvp_atacante_pve=" + zone.RejectedPvpAttackerPve + ", " +
                   "ataque_rech_pvp_objetivo_pve=" + zone.RejectedPvpTargetPve + ", " +
                   "ataque_rech_pvp_muerto=" + zone.RejectedPvpTargetDead + ", " +
                   "ataque_rech_pvp_propio=" + zone.RejectedPvpSelf + ", " +
                   "pvp_cambios_modo=" + zone.PvpModeChanges + ", " +
                   "pvp_modo_igual=" + zone.PvpModeUnchanged + ", " +
                   "pvp_rech_fuera_segura=" + zone.PvpRejectedNotInSafeZone + ", " +
                   "pvp_rech_pronto=" + zone.PvpRejectedTooSoon + ", " +
                   "pvp_estado_msgs=" + zone.PvpStatusMessages + ", " +
                  "aggro_cancelado_zona=" + zone.NpcAggroCancelledSafeZone + ", " +
                  "cambios_zona=" + zone.ZoneChanges + ", " +
                  "entradas_zona_segura=" + zone.SafeZoneEntries + ", " +
                 "ataques_distancia=" + zone.AttacksRanged + ", " +
                 zone.RejectedAttackCooldown + " por recarga, " +
                 zone.RejectedAttackDead + " por jugador muerto, " +
                 "cambios_dueno=" + zone.LootOwnerChanges + ", " +
                 (zone.Attacks?.RejectedQueueFull ?? 0) + " por cola llena, " +
                 zone.NpcHits + " golpes de PNJ, " +
                 "pnj_persecucion_abandonada=" + zone.NpcChaseGiveUps + ", " +
                 "pnj_regreso_forzado=" + zone.NpcReturnTeleports + ", " +
                zone.NpcDeaths + " muertes, " + zone.NpcRespawns + " reapariciones de PNJ, " +
                "elites_aparecidos=" + zone.EliteAppearances +
                ", elites_muertos=" + zone.EliteDeaths + DescribeRankMetrics(zone) + ", " +
                 "experiencia_otorgada=" + zone.ExperienceGranted +
                 ", oro_otorgado=" + zone.GoldGranted +
                 ", pnj_sin_dueno=" + zone.NpcDeathsWithoutOwner +
                ", subidas_nivel=" + zone.LevelUps);
            Console.WriteLine(
                "  Jugador         : " + zone.PlayerDeaths + " muertes, " +
                zone.PlayerRespawns + " reapariciones, muertes_penalizadas=" + zone.PenalizedDeaths +
                ", xp_perdida_muerte=" + zone.ExperienceLost);
            Console.WriteLine(
                "  Habilidades     : hab_usadas=" + zone.SkillsUsed +
                ", hab_area_objetivos=" + zone.AreaTargetsHit + ", hab_area_tope=" + zone.AreaCapped +
                ", hab_curas=" + zone.HealsDone + ", hab_cura_total=" + zone.HealAmount +
                ", hab_rech_vida_llena=" + zone.SkillsRejected(SkillRejectReason.FullHealth) +
                ", hab_rechazadas=" + TotalSkillRejections(zone) +
                ", recurso_msgs=" + network.ResourceMessages +
                ", recurso_bytes=" + network.ResourceBytes +
                ", hab_catalogo_bytes=" + network.SkillCatalogBytes +
                ", est_bonus=" + zone.StatusesApplied(StatusKind.Bonus) +
                ", est_escudo=" + zone.StatusesApplied(StatusKind.Shield) +
                ", est_lento=" + zone.StatusesApplied(StatusKind.Slow) +
                ", est_provocar=" + zone.StatusesApplied(StatusKind.Taunt) +
                ", est_ignorados=" + zone.StatusesIgnoredWeaker +
                ", est_expirados=" + zone.StatusesExpired +
                ", est_muerte=" + zone.StatusesClearedByDeath +
                ", est_provocar_roto=" + zone.TauntsBroken +
                ", est_absorbido=" + zone.ShieldAbsorbed +
                ", est_msgs=" + network.StatusMessages +
                ", est_bytes=" + network.StatusBytes +
                ", est_evt_msgs=" + network.StatusEventMessages +
                ", est_evt_bytes=" + network.StatusEventBytes +
                ", hab_rech_desconocida=" + zone.SkillsRejected(SkillRejectReason.UnknownSkill) +
                ", hab_rech_clase=" + zone.SkillsRejected(SkillRejectReason.WrongClass) +
                ", hab_rech_nivel=" + zone.SkillsRejected(SkillRejectReason.LevelTooLow) +
                ", hab_rech_muerto=" + zone.SkillsRejected(SkillRejectReason.Dead) +
                ", hab_rech_objetivo=" + zone.SkillsRejected(SkillRejectReason.InvalidTarget) +
                ", hab_rech_segura=" + zone.SkillsRejected(SkillRejectReason.SafeZone) +
                ", hab_rech_pvp=" + zone.SkillsRejected(SkillRejectReason.PvpNotAllowed) +
                ", hab_rech_distancia=" + zone.SkillsRejected(SkillRejectReason.OutOfRange) +
                ", hab_rech_vision=" + zone.SkillsRejected(SkillRejectReason.NoLineOfSight) +
                ", hab_rech_recarga=" + zone.SkillsRejected(SkillRejectReason.Cooldown) +
                ", hab_rech_recurso=" + zone.SkillsRejected(SkillRejectReason.NotEnoughResource));
            Console.WriteLine(
                "  Objetos         : objetos_soltados=" + zone.ItemsDropped +
                ", objetos_recogidos=" + zone.ItemsPickedUp +
                ", objetos_caducados=" + zone.ItemsExpired +
                 ", objetos_descartados=" + zone.ItemsDiscarded +
                 ", botin_directo=" + zone.ItemsLootedDirect +
                 ", botin_suelo_lleno=" + zone.ItemsLootedToGround +
                 ", botin_lineas_recortadas=" + zone.LootLinesTrimmed +
                ", objetos_en_suelo=" + zone.GroundItemCount +
                ", recoger_rech_muerto=" + zone.RejectedPickupDead +
                ", recoger_rech_objeto=" + zone.RejectedPickupItem +
                ", recoger_rech_distancia=" + zone.RejectedPickupDistance +
                 ", recoger_rech_lleno=" + zone.RejectedPickupFull +
                 ", recogida_rech_reservada=" + zone.RejectedPickupReserved +
                ", usar_rech_muerto=" + zone.RejectedUseDead +
                ", usar_rech_objeto=" + zone.RejectedUseItem +
                ", usar_rech_lleno=" + zone.RejectedUseFull +
                ", usar_rech_recarga=" + zone.RejectedUseCooldown +
                ", pociones_usadas=" + zone.PotionsUsed +
                ", equipados=" + zone.ItemsEquipped +
                ", desequipados=" + zone.ItemsUnequipped +
                ", intercambios_equipo=" + zone.EquipmentSwaps +
                ", movidos=" + zone.ItemsMoved +
                ", divididos=" + zone.ItemsSplit +
                ", juntados=" + zone.ItemsMerged +
                ", intercambiados=" + zone.ItemsSwapped +
                ", destruidos=" + zone.ItemsDestroyed +
                ", equipo_rech_muerto=" + zone.RejectedEquipDead +
                ", equip_rech_vacio=" + zone.RejectedEquipEmpty +
                ", equip_rech_objeto=" + zone.RejectedEquipItem +
                ", equipo_rech_vacio=" + zone.RejectedUnequipEmpty +
                ", equipo_rech_lleno=" + zone.RejectedEquipFull +
                ", equip_rech_clase=" + zone.RejectedEquipClass +
                ", equip_rech_nivel=" + zone.RejectedEquipLevel +
                ", equip_rech_dosmanos=" + zone.RejectedEquipTwoHand +
                ", equipo_corregido_carga=" + zone.EquipmentCorrectedOnLoad +
                ", equipo_aviso_carga=" + zone.EquipmentLoadWarnings +
                ", equipo_rech_hueco=" + zone.RejectedEquipmentSlot +
                ", equipo_rech_destino=" + zone.RejectedEquipmentDestination +
                ", mover_rech_muerto=" + zone.RejectedMoveDead +
                ", mover_rech_hueco=" + zone.RejectedMoveSlot +
                ", mover_rech_mismo=" + zone.RejectedMoveSameSlot +
                ", mover_rech_vacio=" + zone.RejectedMoveEmpty +
                ", mover_rech_cantidad=" + zone.RejectedMoveCount +
                ", mover_rech_objeto=" + zone.RejectedMoveItem +
                ", mover_rech_lleno=" + zone.RejectedMoveFull +
                ", destruir_rech_muerto=" + zone.RejectedDestroyDead +
                ", destruir_rech_hueco=" + zone.RejectedDestroySlot +
                ", destruir_rech_vacio=" + zone.RejectedDestroyEmpty +
                ", destruir_rech_cantidad=" + zone.RejectedDestroyCount +
                ", destruir_rech_objeto=" + zone.RejectedDestroyItem +
                ", acciones_objeto_cola_llena=" + zone.RejectedItemActionQueueFull);
            Console.WriteLine(
                "  Atributos       : stats_repartos=" + zone.StatsAllocations +
                ", stats_puntos=" + zone.StatsPointsAllocated +
                ", stats_rech_muerto=" + zone.RejectedStatsDead +
                ", stats_rech_vacio=" + zone.RejectedStatsEmpty +
                ", stats_rech_exceso=" + zone.RejectedStatsExcess +
                ", reajustes=" + zone.PointResets +
                ", reajuste_rech_muerto=" + zone.RejectedResetDead +
                ", reajuste_rech_vacio=" + zone.RejectedResetEmpty +
                ", reajuste_rech_objeto=" + zone.RejectedResetItem);
            Console.WriteLine(
                "  Persistencia    : " + network.LoginsSucceeded + " login(s) correctos, " +
                network.LoginsRejected + " rechazados, " + network.CharactersSaved +
                " personaje(s) guardados, " + network.SaveFailures + " fallos, " +
                network.PendingSaves + " pendiente(s)" +
                (network.SaveFailures > 0 && !string.IsNullOrEmpty(network.SaveLastError)
                    ? ", último error: " + network.SaveLastError
                    : string.Empty));
            Console.WriteLine(
                "  Chat            : chat_recibidos=" + network.ChatReceived +
                ", chat_entregados=" + network.ChatDelivered +
                ", chat_rech_invalido=" + network.ChatRejectedInvalid +
                ", chat_rech_largo=" + network.ChatRejectedLong +
                ", chat_rech_rapido=" + network.ChatRejectedFast);
            Console.WriteLine(
                "  PvP             : pvp_estado_msgs=" + network.PvpStatusMessages +
                ", pvp_estado_bytes=" + network.PvpStatusBytes +
                ", pvp_rech_msgs=" + network.PvpRejectedMessages);
            Console.WriteLine("  Jugadores       : " + zone.Players.Count);
            Console.WriteLine("  Código de salida: " + exitCode);
        }

        /// <summary>Escribe un error en color.</summary>
        public static void WriteError(string message)
        {
            ConsoleColor previous = Console.ForegroundColor;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine("ERROR: " + message);
            Console.ForegroundColor = previous;
        }

        /// <summary>Primeros 16 caracteres de la huella, que bastan para comparar visualmente.</summary>
        public static string ShortHash(string hash) =>
            hash.Length <= 16 ? hash : hash.Substring(0, 16) + "…";

        private static string DescribeZones(ZoneMap? zones)
        {
            if (zones == null || zones.Zones.Length == 0)
            {
                return "ninguna";
            }

            int safe = 0;
            int pvp = 0;
            for (int i = 0; i < zones.Zones.Length; i++)
            {
                if (zones.Zones[i].Kind == ZoneKind.Safe) safe++;
                else if (zones.Zones[i].Kind == ZoneKind.Pvp) pvp++;
            }

            return zones.Zones.Length + " (" + safe + " seguras, " + pvp + " PvP, " +
                zones.RectCount + " rectángulos)";
        }

        private static string DescribeRanks(NpcCatalog npcs)
        {
            if (npcs.Ranks == null || npcs.Ranks.Count == 0)
            {
                return "ninguno";
            }

            var text = new StringBuilder();
            bool first = true;
            for (int id = 1; id <= NpcCatalog.MaxRankId; id++)
            {
                Avelora.Server.Npcs.NpcRank? rank = npcs.FindRank(id);
                if (rank == null)
                {
                    continue;
                }

                if (!first)
                {
                    text.Append(", ");
                }

                text.Append(id).Append(':').Append(rank.Name)
                    .Append(" (chance=").Append(Invariant(rank.Chance, "0.##")).Append(')');
                first = false;
            }

            return text.ToString();
        }

        private static string DescribeRankMetrics(Zone zone)
        {
            NpcCatalog? npcs = zone.Npcs;
            if (npcs == null || npcs.Ranks == null || npcs.Ranks.Count == 0)
            {
                return string.Empty;
            }

            var text = new StringBuilder();
            for (int id = 1; id <= NpcCatalog.MaxRankId; id++)
            {
                if (npcs.FindRank(id) == null)
                {
                    continue;
                }

                text.Append(" rango").Append(id).Append("_aparecidos=").Append(zone.RankAppearances(id))
                    .Append(" rango").Append(id).Append("_muertos=").Append(zone.RankDeaths(id));
            }

            return text.ToString();
        }

        private static string Invariant(double value, string format) =>
            value.ToString(format, CultureInfo.InvariantCulture);

        private static double Percent(double actual, double expected) =>
            expected <= 0d ? 0d : ((actual - expected) / expected) * 100d;
    }
}
