#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;
using Avelora.Shared;
using Avelora.Shared.Protocol;

namespace Avelora.Server.Progression
{
    /// <summary>Catálogo de habilidades cargado desde <c>skills.json</c> (I-16).</summary>
    public sealed class SkillCatalog
    {
        public const int MaxAreaTargets = 16;

        private static readonly UTF8Encoding StrictUtf8 = new UTF8Encoding(false, true);

        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };

        public List<SkillDefinition>? Skills { get; set; }

        private Dictionary<int, SkillDefinition>? _byId;

        /// <summary>Carga y valida el catálogo de habilidades.</summary>
        public static SkillCatalog Load(string path, ClassCatalog classes)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("La ruta de skills.json no puede estar vacía.", nameof(path));
            }

            if (!File.Exists(path))
            {
                throw new FileNotFoundException("No se encuentra el archivo de habilidades '" + path + "'.", path);
            }

            SkillCatalog catalog = JsonSerializer.Deserialize<SkillCatalog>(File.ReadAllText(path), JsonOptions)
                ?? throw new InvalidOperationException("skills.json está vacío o no contiene un objeto.");
            catalog.Validate(classes);
            return catalog;
        }

        /// <summary>Valida las reglas comunes y cada definición de habilidad.</summary>
        public void Validate(ClassCatalog classes)
        {
            if (Skills == null || Skills.Count == 0)
            {
                throw new InvalidOperationException("skills.json: skills debe contener al menos una entrada.");
            }

            _byId = new Dictionary<int, SkillDefinition>(Skills.Count);

            for (int i = 0; i < Skills.Count; i++)
            {
                SkillDefinition? skill = Skills[i];
                string where = "skills[" + i + "]";
                if (skill == null)
                {
                    throw new InvalidOperationException("skills.json: " + where + " está vacío.");
                }

                if (skill.Id < 1 || skill.Id > ushort.MaxValue)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".id debe estar entre 1 y 65535.");
                }

                if (_byId.ContainsKey(skill.Id))
                {
                    throw new InvalidOperationException("skills.json: el id " + skill.Id + " está repetido.");
                }

                string name = skill.Name?.Trim() ?? string.Empty;
                if (string.IsNullOrEmpty(name))
                {
                    throw new InvalidOperationException("skills.json: " + where + ".name no puede estar vacío.");
                }

                int nameBytes;
                try
                {
                    nameBytes = StrictUtf8.GetByteCount(name);
                }
                catch (EncoderFallbackException)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".name no es UTF-8 válido.");
                }
                if (nameBytes > ProtocolConstants.MaxSkillNameBytes)
                {
                    throw new InvalidOperationException(
                        "skills.json: " + where + ".name ocupa " + nameBytes + " bytes UTF-8 y el tope es " + ProtocolConstants.MaxSkillNameBytes + ".");
                }

                if (skill.ClassId < 1 || skill.ClassId > byte.MaxValue)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".classId debe estar entre 1 y 255.");
                }

                CharacterClass? characterClass = classes.Find(skill.ClassId);
                if (characterClass == null)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".classId " + skill.ClassId + " no existe en classes.json.");
                }

                if (skill.RequiredLevel < 1 || skill.RequiredLevel > byte.MaxValue)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".requiredLevel debe estar entre 1 y 255.");
                }

                if (skill.ResourceCost < 0 || skill.ResourceCost > 10000)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".resourceCost debe estar entre 0 y 10000.");
                }

                if (double.IsNaN(skill.CooldownSeconds) || double.IsInfinity(skill.CooldownSeconds) ||
                    skill.CooldownSeconds < 0.05 || skill.CooldownSeconds > 600)
                {
                    throw new InvalidOperationException(
                        "skills.json: " + where + ".cooldownSeconds debe ser finito y estar entre 0.05 y 600.");
                }

                if (float.IsNaN(skill.Range) || float.IsInfinity(skill.Range) || skill.Range < 0.5f || skill.Range > 20f)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".range debe ser finito y estar entre 0.5 y 20.");
                }

                if (skill.Damage < 0 || skill.Damage > 10000)
                {
                    throw new InvalidOperationException("skills.json: " + where + ".damage debe estar entre 0 y 10000.");
                }

                if (skill.DamageType != "physical" && skill.DamageType != "magical")
                {
                    throw new InvalidOperationException(
                        "skills.json: " + where + ".damageType debe ser physical o magical.");
                }

                if (skill.Effect != "damage" && skill.Effect != "heal")
                    throw new InvalidOperationException("skills.json: " + where + ".effect debe ser damage o heal.");
                if (!float.IsFinite(skill.Radius) || (skill.Radius != 0 && (skill.Radius < 0.5f || skill.Radius > 8f)))
                    throw new InvalidOperationException("skills.json: " + where + ".radius debe ser 0 o estar entre 0.5 y 8.");
                if (skill.Radius == 0 && skill.MaxTargets != 0)
                    throw new InvalidOperationException("skills.json: " + where + ".maxTargets solo vale con radius.");
                if (skill.Radius > 0 && (skill.MaxTargets < 2 || skill.MaxTargets > MaxAreaTargets))
                    throw new InvalidOperationException("skills.json: " + where + ".maxTargets debe estar entre 2 y 16.");
                if (skill.IsHeal && skill.Radius > 0)
                    throw new InvalidOperationException("skills.json: " + where + ".radius: la curación en área no existe todavía.");
                if (skill.IsHeal && skill.Damage < 1)
                    throw new InvalidOperationException("skills.json: " + where + ".damage de una cura debe ser al menos 1.");

                skill.CooldownTicks = Math.Max(1, (int)Math.Ceiling(skill.CooldownSeconds * SimulationConstants.TickRate));
                _byId[skill.Id] = skill;
            }

            // Máximo de habilidades por clase
            var perClass = new Dictionary<int, int>();
            foreach (SkillDefinition skill in Skills)
            {
                if (!perClass.TryGetValue(skill.ClassId, out int count))
                {
                    count = 0;
                }
                count++;
                if (count > ProtocolConstants.MaxSkillsPerClass)
                {
                    throw new InvalidOperationException(
                        "skills.json: la clase " + skill.ClassId + " tiene " + count + " habilidades y el máximo es " + ProtocolConstants.MaxSkillsPerClass + ".");
                }
                perClass[skill.ClassId] = count;
            }

            // El catálogo se envía también a las clases sin habilidades.
            foreach (CharacterClass c in classes.Classes!)
            {
                string resourceName = c.Resource?.Name ?? string.Empty;
                string where = "classes[" + c.Id + "].resource.name";
                if (string.IsNullOrEmpty(resourceName))
                {
                    throw new InvalidOperationException("classes.json: " + where + " no puede estar vacío (va en SkillCatalog).");
                }

                int resourceNameBytes;
                try
                {
                    resourceNameBytes = StrictUtf8.GetByteCount(resourceName);
                }
                catch (EncoderFallbackException)
                {
                    throw new InvalidOperationException("classes.json: " + where + " no es UTF-8 válido.");
                }
                if (resourceNameBytes > ProtocolConstants.MaxSkillNameBytes)
                {
                    throw new InvalidOperationException(
                        "classes.json: " + where + " ocupa " + resourceNameBytes + " bytes UTF-8 y el tope es " + ProtocolConstants.MaxSkillNameBytes + " (va en SkillCatalog).");
                }
            }
        }

        /// <summary>Busca una habilidad por id.</summary>
        public SkillDefinition? Find(int id)
        {
            if (_byId == null)
            {
                return null;
            }

            _byId.TryGetValue(id, out SkillDefinition? skill);
            return skill;
        }
    }

    public sealed class SkillDefinition
    {
        public int Id { get; set; } = -1;
        public string Name { get; set; } = string.Empty;
        public int ClassId { get; set; } = -1;
        public int RequiredLevel { get; set; } = -1;
        public int ResourceCost { get; set; } = -1;
        public double CooldownSeconds { get; set; } = double.NaN;
        public float Range { get; set; } = float.NaN;
        public int Damage { get; set; } = -1;
        public string DamageType { get; set; } = string.Empty;
        public string Effect { get; set; } = "damage";
        public float Radius { get; set; }
        public int MaxTargets { get; set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsHeal => Effect == "heal";

        /// <summary>Ticks de recarga calculados en <see cref="SkillCatalog.Validate"/>.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int CooldownTicks { get; internal set; }
    }
}