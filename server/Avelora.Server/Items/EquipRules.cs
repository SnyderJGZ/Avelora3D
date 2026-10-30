#nullable enable
using System;
using System.Collections.Generic;
using Avelora.Server.Progression;

namespace Avelora.Server.Items
{
    public enum EquipCheck
    {
        Ok,
        WrongClass,
        LowLevel,
    }

    /// <summary>Reglas comunes para equipar objetos desde acciones y al cargar personajes.</summary>
    public static class EquipRules
    {
        public static EquipCheck Check(ItemType item, CharacterClass characterClass, int level)
        {
            if (item == null) throw new ArgumentNullException(nameof(item));
            if (characterClass == null) throw new ArgumentNullException(nameof(characterClass));

            if (item.IsWeapon)
            {
                bool allowed = false;
                List<string>? weaponTypes = characterClass.WeaponTypes;
                if (weaponTypes != null)
                {
                    for (int i = 0; i < weaponTypes.Count; i++)
                    {
                        if (string.Equals(weaponTypes[i], item.WeaponType, StringComparison.OrdinalIgnoreCase))
                        {
                            allowed = true;
                            break;
                        }
                    }
                }

                if (!allowed)
                {
                    return EquipCheck.WrongClass;
                }
            }

            return level < item.RequiredLevel ? EquipCheck.LowLevel : EquipCheck.Ok;
        }
    }
}
