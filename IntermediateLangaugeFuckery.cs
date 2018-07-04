using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using BattleTech;
using Harmony;

namespace WeaponVariance
{
    internal static class IntermediateLangaugeFuckery {
        public static IEnumerable<CodeInstruction> PerformDamagePerShotAdjustedFuckery(IEnumerable<CodeInstruction> instructions)
        {   
            // o.g. call - for the purposes of our exercise, base/this are the same.
            //   this.weapon.DamagePerShotAdjusted(this.weapon.parent.occupiedDesignMask)
            // becomes
            //   IL_00ab: ldarg.0
            //   IL_00ac: ldfld class BattleTech.Weapon WeaponEffect::weapon
            //   IL_00b1: ldarg.0
            //   IL_00b2: ldfld class BattleTech.Weapon WeaponEffect::weapon
            //   IL_00b7: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
            //   IL_00bc: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
            //   IL_00c1: callvirt instance float32 BattleTech.Weapon::DamagePerShotAdjusted(class BattleTech.DesignMaskDef)
            // we want
            //   VariantWeapon.VariantDamage(WeaponEffect, DesignMaskDef)
            // so (IL is slightly wrong):
            //   IL_00ab: ldarg.0
            //   IL_00ac: ldarg.0
            //   IL_00b1: ldfld class BattleTech.Weapon WeaponEffect::weapon
            //   IL_00b2: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
            //   IL_00b7: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
            //   IL_00cc: call instance float32 VariantWeapon::VariantDamage(class WeaponEffect, class BattleTech.DesignMaskDef)
            var instructionList = instructions.ToList();
            var originalMethodInfo = AccessTools.Method(typeof(Weapon), "DamagePerShotAdjusted", new Type[] {typeof(DesignMaskDef)});
            var index = instructionList.FindIndex(instruction => instruction.operand == originalMethodInfo);
            var newMethodInfo = AccessTools.Method(typeof(VariantWeapon), "VariantDamage", new Type[] {typeof(WeaponEffect), typeof(DesignMaskDef)});
            instructionList[index].operand = (object) newMethodInfo;
            instructionList[index].opcode = OpCodes.Call; // static method use call
            instructionList.RemoveAt(index - 5); // nuke call to "this.weapon" so "this" is on the stack as first arg to variant damage
            return instructionList;
        }
    }
}