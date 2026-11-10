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

        /// <summary>Segundos de recarga global compartida por las habilidades; cero la desactiva.</summary>
        public double GlobalCooldownSeconds { get; set; }

        /// <summary>Recarga global calculada en ticks.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int GlobalCooldownTicks { get; private set; }

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
            if (double.IsNaN(GlobalCooldownSeconds) || double.IsInfinity(GlobalCooldownSeconds) ||
                GlobalCooldownSeconds < 0 || GlobalCooldownSeconds > 10)
            {
                throw new InvalidOperationException(
                    "skills.json: globalCooldownSeconds debe estar entre 0 y 10.");
            }

            GlobalCooldownTicks = (int)Math.Ceiling(GlobalCooldownSeconds * SimulationConstants.TickRate);
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

                if (skill.WeaponPercent < 0 || skill.WeaponPercent > 1000)
                {
                    throw new InvalidOperationException(
                        "skills.json: " + where + ".weaponPercent debe estar entre 0 y 1000.");
                }

                if (skill.DamageType != "physical" && skill.DamageType != "magical")
                {
                    throw new InvalidOperationException(
                        "skills.json: " + where + ".damageType debe ser physical o magical.");
                }

                if (skill.Effect != "damage" && skill.Effect != "heal" && skill.Effect != "none")
                    throw new InvalidOperationException("skills.json: " + where + ".effect debe ser damage, heal o none.");
                if (!float.IsFinite(skill.Radius) || (skill.Radius != 0 && (skill.Radius < 0.5f || skill.Radius > 8f)))
                    throw new InvalidOperationException("skills.json: " + where + ".radius debe ser 0 o estar entre 0.5 y 8.");
                if (skill.Radius == 0 && skill.MaxTargets != 0)
                    throw new InvalidOperationException("skills.json: " + where + ".maxTargets solo vale con radius.");
                if (skill.Radius > 0 && (skill.MaxTargets < 2 || skill.MaxTargets > MaxAreaTargets))
                    throw new InvalidOperationException("skills.json: " + where + ".maxTargets debe estar entre 2 y 16.");
                if (skill.Hits < 1 || skill.Hits > 5 ||
                    (skill.Hits > 1 && (!skill.IsDamage || skill.Radius > 0)))
                    throw new InvalidOperationException("skills.json: " + where + ".hits fuera de rango o incompatible con effect/radius.");
                if (skill.CritBonus < 0 || skill.CritBonus > 500 || (skill.CritBonus > 0 && skill.Effect == "none"))
                    throw new InvalidOperationException("skills.json: " + where + ".critBonus fuera de rango o incompatible con effect.");
                if (skill.AreaCenter != "target" && skill.AreaCenter != "self")
                    throw new InvalidOperationException("skills.json: " + where + ".areaCenter debe ser target o self.");
                if (skill.AreaSelf && !((skill.Radius > 0 && (skill.IsDamage || skill.IsHeal)) ||
                    (skill.Radius == 0 && skill.Effect == "none" &&
                     (skill.Status == "bonus" || skill.Status == "shield"))))
                    throw new InvalidOperationException("skills.json: " + where + ".areaCenter self no es válido para este efecto.");
                if (skill.IsHeal && skill.Radius > 0 && !skill.AreaSelf)
                    throw new InvalidOperationException("skills.json: " + where + ".areaCenter debe ser self para curación en área.");
                if (skill.IsHeal && skill.Damage < 1)
                    throw new InvalidOperationException("skills.json: " + where + ".damage de una cura debe ser al menos 1.");

                string status = skill.Status ?? string.Empty;
                if (status != string.Empty && status != "bonus" && status != "shield" && status != "slow" && status != "taunt")
                    throw new InvalidOperationException("skills.json: " + where + ".status debe ser bonus, shield, slow o taunt.");
                if (status == string.Empty && (skill.StatusSeconds != 0 || skill.StatusAmount != 0))
                    throw new InvalidOperationException("skills.json: " + where + ".statusSeconds y .statusAmount solo valen con status.");
                if (status != string.Empty && (double.IsNaN(skill.StatusSeconds) || double.IsInfinity(skill.StatusSeconds) ||
                    skill.StatusSeconds < 0.5 || skill.StatusSeconds > 30))
                    throw new InvalidOperationException("skills.json: " + where + ".statusSeconds debe estar entre 0.5 y 30.");
                if (skill.Effect == "none" && status == string.Empty)
                    throw new InvalidOperationException("skills.json: " + where + ".effect none necesita status.");
                if (skill.Effect == "none" && skill.Damage != 0)
                    throw new InvalidOperationException("skills.json: " + where + ".damage debe ser 0 con effect none.");
                bool areaHealShield = skill.IsHeal && skill.Radius > 0 && status == "shield";
                if (skill.IsHeal && status != string.Empty && !areaHealShield)
                    throw new InvalidOperationException("skills.json: " + where + ".status en una cura solo admite shield en curación en área.");
                if ((status == "bonus" || status == "shield" || status == "taunt") && skill.Effect != "none" && !areaHealShield)
                    throw new InvalidOperationException("skills.json: " + where + ".status " + status + " necesita effect none.");
                if (status != string.Empty && skill.Radius > 0 && !areaHealShield)
                    throw new InvalidOperationException("skills.json: " + where + ".status: los estados en área no existen todavía.");
                if ((status == "slow" && (skill.StatusAmount < 10 || skill.StatusAmount > 80)) ||
                    (status == "shield" && (skill.StatusAmount < 1 || skill.StatusAmount > 10000)) ||
                    (status == "bonus" && (skill.StatusAmount < 1 || skill.StatusAmount > 1000)) ||
                    (status == "taunt" && skill.StatusAmount != 0))
                    throw new InvalidOperationException("skills.json: " + where + ".statusAmount fuera de rango para " + status + ".");

                skill.StatusKind = status switch
                {
                    "bonus" => StatusKind.Bonus,
                    "shield" => StatusKind.Shield,
                    "slow" => StatusKind.Slow,
                    "taunt" => StatusKind.Taunt,
                    _ => StatusKind.None,
                };
                skill.StatusTicks = status == string.Empty
                    ? 0
                    : (int)Math.Ceiling(skill.StatusSeconds * SimulationConstants.TickRate);

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
        public int WeaponPercent { get; set; }
        public string DamageType { get; set; } = string.Empty;
        public string Effect { get; set; } = "damage";
        public float Radius { get; set; }
        public int MaxTargets { get; set; }
        public string Status { get; set; } = string.Empty;
        public double StatusSeconds { get; set; }
        public int StatusAmount { get; set; }
        public int Hits { get; set; } = 1;
        public int CritBonus { get; set; }
        public string AreaCenter { get; set; } = "target";
        [System.Text.Json.Serialization.JsonIgnore]
        public bool AreaSelf => AreaCenter == "self";
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsHeal => Effect == "heal";
        [System.Text.Json.Serialization.JsonIgnore]
        public StatusKind StatusKind { get; internal set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public int StatusTicks { get; internal set; }
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsDamage => Effect == "damage";
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsBeneficial => StatusKind == StatusKind.Bonus || StatusKind == StatusKind.Shield;

        /// <summary>Ticks de recarga calculados en <see cref="SkillCatalog.Validate"/>.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public int CooldownTicks { get; internal set; }
    }
}
