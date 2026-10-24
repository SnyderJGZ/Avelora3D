#nullable enable
namespace Avelora.Server.Entities
{
    /// <summary>
    /// Cálculo de la vida en porcentaje que viaja en el snapshot (I-02 e I-03).
    /// </summary>
    /// <remarks>
    /// Jugadores y PNJ usan la misma regla: se redondea hacia arriba para que una entidad con vida
    /// positiva nunca mande 0 al cliente, y 0 solo significa «muerto».
    /// </remarks>
    public static class EntityHealth
    {
        /// <summary>
        /// Vida restante en porcentaje (0–100), redondeando hacia arriba. Un valor de vida ≤ 0 da 0.
        /// </summary>
        public static byte Percent(int health, int maxHealth)
        {
            if (health <= 0 || maxHealth < 1)
            {
                return 0;
            }

            int percent = ((100 * health) + maxHealth - 1) / maxHealth;
            return (byte)(percent > 100 ? 100 : percent);
        }
    }
}
