using System;
using System.IO;
using Avelora.Server.Configuration;
using Avelora.Server.Simulation;
using Avelora.Shared;
using Xunit;

namespace Avelora.Tests
{
    public class ServerConfigTests : IDisposable
    {
        private readonly string _folder;

        public ServerConfigTests()
        {
            _folder = Path.Combine(Path.GetTempPath(), "avelora-config-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_folder);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_folder, recursive: true);
            }
            catch (IOException)
            {
                // Un archivo en uso no debe hacer fallar la prueba.
            }
        }

        private string Path_(string name) => Path.Combine(_folder, name);

        [Fact]
        public void Los_Valores_Por_Defecto_Valen_Y_Apuntan_Al_Tick_Compartido()
        {
            var config = new ServerConfig();

            config.Validate();

            Assert.Equal(SimulationConstants.TickRate, config.TickRate);
            Assert.Equal(20, config.TickRate);
            Assert.Equal(0.05f, SimulationConstants.FixedDt, 6);
            Assert.Equal(InterestSettings.DefaultRadius, config.InterestRadius);
            Assert.Equal(InterestSettings.DefaultCellSize, config.InterestCellSize);
            Assert.Equal(InterestSettings.DefaultHysteresis, config.InterestHysteresis);
            Assert.True(config.PersistenceEnabled);
            Assert.Equal("data/avelora.db", config.DatabasePath);
            Assert.Equal(string.Empty, config.RegistrationInvitationCode);
            Assert.Equal(60d, config.SaveIntervalSeconds);
            Assert.Equal(16, config.MaxPendingLogins);
            Assert.Equal(5, config.MaxFailedLoginsPerAccountPerMinute);
            Assert.Equal(500, config.RejectDisconnectDelayMilliseconds);
        }

        [Fact]
        public void El_Registro_Viene_Cerrado_Si_No_Se_Configura_La_Invitacion()
        {
            // Sin código a propósito, un servidor expuesto por descuido no deja crear cuentas;
            // el servidor de desarrollo lo escribe explícitamente en server.json.
            var config = new ServerConfig();

            Assert.Equal(string.Empty, config.RegistrationInvitationCode);
            config.Validate();
        }

        [Fact]
        public void Si_El_Archivo_No_Existe_Se_Escribe_Uno_Para_Poder_Editarlo()
        {
            string path = Path_("server.json");
            Assert.False(File.Exists(path));

            ServerConfig config = ServerConfig.Load(path);

            Assert.True(File.Exists(path), "Debería haber dejado una configuración escrita.");
            Assert.Equal(SimulationConstants.TickRate, config.TickRate);

            // Y lo escrito se vuelve a leer sin pérdidas.
            ServerConfig again = ServerConfig.Load(path);
            Assert.Equal(config.TickRate, again.TickRate);
            Assert.Equal(config.MapPath, again.MapPath);
            Assert.Equal(config.PlayerSpeed, again.PlayerSpeed);
        }

        [Fact]
        public void Lo_Guardado_Se_Recupera_Entero()
        {
            string path = Path_("otra.json");
            var original = new ServerConfig
            {
                MapPath = "mapas/prueba.avmap",
                Port = 7800,
                MaxPlayers = 8,
                MaxConnections = 12,
                SpawnX = 1.25f,
                SpawnY = -8.5f,
                FirstCharacterId = 42,
                PersistenceEnabled = true,
                DatabasePath = "datos/prueba.db",
                RegistrationInvitationCode = "invitacion",
                SaveIntervalSeconds = 12.5d,
                MaxPendingLogins = 4,
                MaxFailedLoginsPerAccountPerMinute = 2,
                PlayerSpeed = 7.5f,
                PlayerHalfWidth = 0.3f,
                PlayerHalfHeight = 0.6f,
                MaxIntentsPerTick = 12,
                MaxIntentsPerPlayerPerTick = 3,
                InputBufferCapacity = 5,
                MaxQueuedIntents = 99,
                MaxMessagesPerSecondPerConnection = 120,
                MaxQueuedOutboundMessages = 128,
                SnapshotEveryTicks = 2,
                InterestRadius = 5.5f,
                InterestCellSize = 2.5f,
                InterestHysteresis = 0.75f,
                HandshakeTimeoutSeconds = 3.5d,
                RejectDisconnectDelayMilliseconds = 750,
                MetricsWindowTicks = 30,
                ReportIntervalSeconds = 2.5d,
                RunSeconds = 3d,
            };

            ServerConfig.Save(path, original);
            ServerConfig loaded = ServerConfig.Load(path);

            Assert.Equal(original.MapPath, loaded.MapPath);
            Assert.Equal(original.Port, loaded.Port);
            Assert.Equal(original.MaxPlayers, loaded.MaxPlayers);
            Assert.Equal(original.MaxConnections, loaded.MaxConnections);
            Assert.Equal(original.SpawnX, loaded.SpawnX);
            Assert.Equal(original.SpawnY, loaded.SpawnY);
            Assert.Equal(original.FirstCharacterId, loaded.FirstCharacterId);
            Assert.Equal(original.PersistenceEnabled, loaded.PersistenceEnabled);
            Assert.Equal(original.DatabasePath, loaded.DatabasePath);
            Assert.Equal(original.RegistrationInvitationCode, loaded.RegistrationInvitationCode);
            Assert.Equal(original.SaveIntervalSeconds, loaded.SaveIntervalSeconds);
            Assert.Equal(original.MaxPendingLogins, loaded.MaxPendingLogins);
            Assert.Equal(original.MaxFailedLoginsPerAccountPerMinute, loaded.MaxFailedLoginsPerAccountPerMinute);
            Assert.Equal(original.PlayerSpeed, loaded.PlayerSpeed);
            Assert.Equal(original.PlayerHalfWidth, loaded.PlayerHalfWidth);
            Assert.Equal(original.PlayerHalfHeight, loaded.PlayerHalfHeight);
            Assert.Equal(original.MaxIntentsPerTick, loaded.MaxIntentsPerTick);
            Assert.Equal(original.MaxIntentsPerPlayerPerTick, loaded.MaxIntentsPerPlayerPerTick);
            Assert.Equal(original.InputBufferCapacity, loaded.InputBufferCapacity);
            Assert.Equal(original.MaxQueuedIntents, loaded.MaxQueuedIntents);
            Assert.Equal(original.MaxMessagesPerSecondPerConnection, loaded.MaxMessagesPerSecondPerConnection);
            Assert.Equal(original.MaxQueuedOutboundMessages, loaded.MaxQueuedOutboundMessages);
            Assert.Equal(original.SnapshotEveryTicks, loaded.SnapshotEveryTicks);
            Assert.Equal(original.InterestRadius, loaded.InterestRadius);
            Assert.Equal(original.InterestCellSize, loaded.InterestCellSize);
            Assert.Equal(original.InterestHysteresis, loaded.InterestHysteresis);
            Assert.Equal(original.HandshakeTimeoutSeconds, loaded.HandshakeTimeoutSeconds);
            Assert.Equal(original.RejectDisconnectDelayMilliseconds, loaded.RejectDisconnectDelayMilliseconds);
            Assert.Equal(original.MetricsWindowTicks, loaded.MetricsWindowTicks);
            Assert.Equal(original.ReportIntervalSeconds, loaded.ReportIntervalSeconds);
            Assert.Equal(original.RunSeconds, loaded.RunSeconds);
        }

        [Fact]
        public void Un_Tick_Distinto_Al_Compartido_Impide_Arrancar()
        {
            // El cliente lleva el paso compilado: aceptar otro valor en silencio sería un desajuste
            // imposible de ver.
            var config = new ServerConfig { TickRate = 30 };

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(config.Validate);
            Assert.Contains("tickRate", error.Message);
        }

        [Theory]
        [InlineData(0f, 0.4f)]
        [InlineData(4f, 0f)]
        [InlineData(4f, -0.4f)]
        public void Una_Caja_Imposible_Impide_Arrancar(float speed, float halfWidth)
        {
            var config = new ServerConfig { PlayerSpeed = speed, PlayerHalfWidth = halfWidth };

            Assert.Throws<InvalidOperationException>(config.Validate);
        }

        [Fact]
        public void Un_Mapa_Sin_Ruta_Impide_Arrancar()
        {
            var config = new ServerConfig { MapPath = "  " };

            Assert.Throws<InvalidOperationException>(config.Validate);
        }

        [Fact]
        public void Los_Topes_Tienen_Que_Tener_Sentido()
        {
            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxIntentsPerTick = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { InputBufferCapacity = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxQueuedIntents = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { MetricsWindowTicks = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { ReportIntervalSeconds = 0d }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { RunSeconds = -1d }.Validate);
        }

        [Fact]
        public void Un_Ajuste_De_Red_Imposible_Impide_Arrancar()
        {
            Assert.Throws<InvalidOperationException>(new ServerConfig { Port = -1 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { Port = 70000 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxPlayers = 0 }.Validate);

            // Si el transporte no admite ni a los jugadores que caben, el que sobra no puede recibir
            // un rechazo: la conexión se cerraría sin explicación.
            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxPlayers = 20, MaxConnections = 10 }.Validate);

            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxMessagesPerSecondPerConnection = 1 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { MaxQueuedOutboundMessages = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { SnapshotEveryTicks = 0 }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { HandshakeTimeoutSeconds = 0d }.Validate);
            Assert.Throws<InvalidOperationException>(
                new ServerConfig { RejectDisconnectDelayMilliseconds = -1 }.Validate);

            // 0 es válido: corta en la vuelta siguiente sin esperar.
            new ServerConfig { RejectDisconnectDelayMilliseconds = 0 }.Validate();
        }

        [Fact]
        public void Un_Area_De_Interes_Imposible_Impide_Arrancar()
        {
            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestRadius = 0f }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestRadius = -1f }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestRadius = float.NaN }.Validate);

            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestCellSize = 0f }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestCellSize = float.PositiveInfinity }.Validate);

            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestHysteresis = -0.1f }.Validate);
            Assert.Throws<InvalidOperationException>(new ServerConfig { InterestHysteresis = float.NaN }.Validate);
        }

        [Fact]
        public void Un_Ajuste_De_Persistencia_Imposible_Impide_Arrancar()
        {
            Assert.Throws<InvalidOperationException>(
                new ServerConfig { PersistenceEnabled = true, DatabasePath = "  " }.Validate);

            Assert.Throws<InvalidOperationException>(
                new ServerConfig { PersistenceEnabled = true, SaveIntervalSeconds = 0d }.Validate);

            Assert.Throws<InvalidOperationException>(
                new ServerConfig { PersistenceEnabled = true, SaveIntervalSeconds = double.NaN }.Validate);

            Assert.Throws<InvalidOperationException>(
                new ServerConfig
                {
                    PersistenceEnabled = true,
                    RegistrationInvitationCode = new string('x', 65),
                }.Validate);

            // Desactivada, la ruta y el intervalo no importan.
            new ServerConfig
            {
                PersistenceEnabled = false,
                DatabasePath = string.Empty,
                SaveIntervalSeconds = 0d,
            }.Validate();
        }

        [Fact]
        public void Un_Ajuste_De_Login_Imposible_Impide_Arrancar()
        {
            Assert.Throws<InvalidOperationException>(
                new ServerConfig { MaxPendingLogins = 0 }.Validate);

            Assert.Throws<InvalidOperationException>(
                new ServerConfig { MaxFailedLoginsPerAccountPerMinute = 0 }.Validate);

            new ServerConfig { MaxPendingLogins = 1, MaxFailedLoginsPerAccountPerMinute = 1 }.Validate();
        }

        [Fact]
        public void La_Base_De_Datos_Se_Resuelve_Junto_A_La_Configuracion()
        {
            string configPath = Path_("server.json");
            string expected = Path.Combine(_folder, "datos", "avelora.db");

            Assert.Equal(
                expected,
                new ServerConfig { DatabasePath = "datos/avelora.db" }.ResolveDatabasePath(configPath));

            Assert.Equal(
                ":memory:",
                new ServerConfig { DatabasePath = ":memory:" }.ResolveDatabasePath(configPath));
        }

        [Fact]
        public void Un_Archivo_Con_Valores_Invalidos_No_Deja_Arrancar()
        {
            string path = Path_("malo.json");
            File.WriteAllText(path, "{ \"tickRate\": 60 }");

            Assert.Throws<InvalidOperationException>(() => ServerConfig.Load(path));
        }

        [Fact]
        public void Resuelve_La_Ruta_Del_Mapa_Junto_A_La_Configuracion()
        {
            string configPath = Path_("server.json");
            ServerConfig.Save(configPath, new ServerConfig { MapPath = "maps/ejemplo.avmap" });

            string mapsFolder = Path.Combine(_folder, "maps");
            Directory.CreateDirectory(mapsFolder);
            string mapPath = Path.Combine(mapsFolder, "ejemplo.avmap");
            File.WriteAllBytes(mapPath, new byte[] { 1, 2, 3 });

            Assert.Equal(mapPath, new ServerConfig { MapPath = "maps/ejemplo.avmap" }.ResolveMapPath(configPath));
        }

        [Fact]
        public void Si_El_Mapa_No_Esta_Se_Dice_Donde_Se_Ha_Buscado()
        {
            string configPath = Path_("server.json");

            FileNotFoundException error = Assert.Throws<FileNotFoundException>(
                () => new ServerConfig { MapPath = "maps/no-existe.avmap" }.ResolveMapPath(configPath));

            Assert.Contains("no-existe.avmap", error.Message);
        }

        [Fact]
        public void La_Ruta_Absoluta_Del_Mapa_Se_Respeta()
        {
            string mapPath = Path_("absoluto.avmap");
            File.WriteAllBytes(mapPath, new byte[] { 4, 5, 6 });

            Assert.Equal(mapPath, new ServerConfig { MapPath = mapPath }.ResolveMapPath(Path_("server.json")));
        }

        [Fact]
        public void Una_Configuracion_Indicada_Que_No_Existe_Se_Avisa()
        {
            Assert.Throws<FileNotFoundException>(() => ServerConfig.ResolveConfigPath(Path_("nada.json")));
        }
    }
}
