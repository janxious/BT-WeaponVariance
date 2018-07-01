using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BattleTech;
using Harmony;
using Harmony.ILCopying;
using Newtonsoft.Json;
using UnityEngine;
using Random = UnityEngine.Random;

namespace WeaponVariance
{
    public static class WeaponVariance
    {
        internal static Settings ModSettings = new Settings();
        internal static string ModDirectory;

        public static void Init(string directory, string settingsJSON)
        {
            ModDirectory = directory;
            try
            {
                ModSettings = JsonConvert.DeserializeObject<Settings>(settingsJSON);
            }
            catch (Exception ex)
            {
                Logger.Error(ex);
                ModSettings = new Settings();
            }

            var harmony = HarmonyInstance.Create(Settings.ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    public static class VariantWeapon
    {
        public static Dictionary<string, float> WeaponDamageMemo = new Dictionary<string, float>();

        public static float VariantDamage(WeaponEffect weaponEffect, DesignMaskDef designMask)
        {
            Logger.Debug("***** yeeeeeehaw *****");
            var weapon = weaponEffect.weapon;
            if (WeaponDamageMemo.ContainsKey(weapon.GUID)) { Logger.Debug($"key found: {weapon.GUID}");return WeaponDamageMemo[weapon.GUID]; }
            var damageVariance = weapon.weaponDef.DamageVariance;
            if (damageVariance == 0)
            {
                WeaponDamageMemo[weapon.GUID] = weapon.DamagePerShotAdjusted(weapon.parent.occupiedDesignMask);
                Logger.Debug($"no variance: {weapon.GUID} / {WeaponDamageMemo[weapon.GUID]}");
                return WeaponDamageMemo[weapon.GUID];
            }
            var damagePerShot = weapon.DamagePerShotAdjusted();
            var varianceRange = new Vector2(damagePerShot - damageVariance, damagePerShot + damageVariance);
            var damage = Random.Range(varianceRange.x, varianceRange.y);
            var combat = Traverse.Create(weapon).Field("combat").GetValue<CombatGameState>();
            var damageWDesign = damage * weapon.GetMaskDamageMultiplier(weapon.parent.occupiedDesignMask);
            var result = damageWDesign * weapon.GetMaskDamageMultiplier(combat.MapMetaData.biomeDesignMask);
            Logger.Debug(
                $"weapon: {weapon.Name} {weapon.UIName}\n" +
                $"damage variance: {damageVariance}\n" +
                $"variance range: {varianceRange.x} {varianceRange.y}\n" +
                $"damage per shot baseline: {damagePerShot}\n" +
                $"damage: {damage}\n" +
                $"damage w/ design mask: {damageWDesign}\n" +
                $"damage w/ env: {result}"
            );
            WeaponDamageMemo[weapon.GUID] = result;
            return WeaponDamageMemo[weapon.GUID];
        }
    }

    [HarmonyPatch(typeof(AttackDirector.AttackSequence), "OnAttackSequenceImpact")]
    public static class AAAAAAAAAHGHHGHG
    {
        static bool Prefix(MessageCenterMessage message, AttackDirector.AttackSequence __instance)
        {
            var attackSequenceImpactMessage = (AttackSequenceImpactMessage)message;
            Logger.Debug($"hit damage: {attackSequenceImpactMessage.hitDamage}\n"
            );
            return true;
        }
    }

    [HarmonyPatch(typeof(AttackDirector), "OnAttackSequenceEnd")]
    public static class DOOOOOKKKKY
    {
        public static void Postfix(MessageCenterMessage message, AttackDirector __instance)
        {
            Logger.Debug($"sequence is over: resetting!");
            VariantWeapon.WeaponDamageMemo.Clear();
        }
    }

    [HarmonyPatch(typeof(LaserEffect), "OnImpact")]
    public static class LaserEffect_OnImpact_Patch
    {
        // static string instructionString;
        // o.g.
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
        // so (IL is wrong):
        //   IL_00ab: ldarg.0
        //   IL_00ac: ldarg.0
        //   IL_00b1: ldfld class BattleTech.Weapon WeaponEffect::weapon
        //   IL_00b2: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
        //   IL_00b7: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
        //   IL_00cc: call instance float32 VariantWeapon::VariantDamage(class WeaponEffect, class BattleTech.DesignMaskDef)
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            //var output = new StringBuilder();
            List<CodeInstruction> instructionList = instructions.ToList();
            var originalMethodInfo = AccessTools.Method(typeof(Weapon), "DamagePerShotAdjusted",
                new Type[] {typeof(DesignMaskDef)});
            var index = instructionList.FindIndex(instruction => instruction.operand == originalMethodInfo);
            var newMethodInfo = AccessTools.Method(typeof(VariantWeapon), "VariantDamage",
                new Type[] {typeof(WeaponEffect), typeof(DesignMaskDef)});
            instructionList[index].operand = (object) newMethodInfo;
            instructionList[index].opcode = OpCodes.Call; // static method use call
            instructionList.RemoveAt(index - 5); // nuke call to "this.weapon" so "this" is on the stack as first arg to variant damage

//            output.Append($"index: {index}\n");
//            instructionList.ToArray().Aggregate(output, (acc, ins) => 
//            {
//                ins?.labels?.ForEach((obj) => acc.Append($"{obj.ToString()} : {obj.GetHashCode()}]\n" ));
//                acc.Append(ins?.opcode.ToString());
//                acc.Append(" : ");
//                acc.Append(ins?.operand?.ToString());
//                acc.Append("\n"); 
//                return acc; 
//            });

            //instructionString = output.ToString();

            return instructionList;
        }
    }

    [HarmonyPatch(typeof(LaserEffect), "PlayImpact")]
    public static class LaserEffect_PlayImpact_Patch
    {
        // static string instructionString;
        // o.g.
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
        // so (IL is wrong):
        //   IL_00ab: ldarg.0
        //   IL_00ac: ldarg.0
        //   IL_00b1: ldfld class BattleTech.Weapon WeaponEffect::weapon
        //   IL_00b2: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
        //   IL_00b7: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
        //   IL_00cc: call instance float32 VariantWeapon::VariantDamage(class WeaponEffect, class BattleTech.DesignMaskDef)
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            //var output = new StringBuilder();
            List<CodeInstruction> instructionList = instructions.ToList();
            var originalMethodInfo = AccessTools.Method(typeof(Weapon), "DamagePerShotAdjusted", new Type[] {typeof(DesignMaskDef)});
            var index = instructionList.FindIndex(instruction => instruction.operand == originalMethodInfo);
            var newMethodInfo = AccessTools.Method(typeof(VariantWeapon), "VariantDamage", new Type[] {typeof(WeaponEffect), typeof(DesignMaskDef)});
            //instructionList = instructionList.MethodReplacer(originalMethodInfo, newMethodInfo).ToList();
            instructionList[index].operand = (object) newMethodInfo;
            instructionList[index].opcode = OpCodes.Call; // static method use call
            instructionList.RemoveAt(index - 5); // nuke call to "this.weapon" so "this" is on the stack as first arg to variant damage

//            output.Append($"index: {index}\n");
//            instructionList.ToArray().Aggregate(output, (acc, ins) => 
//            {
//                ins?.labels?.ForEach((obj) => acc.Append($"{obj.ToString()} : {obj.GetHashCode()}]\n" ));
//                acc.Append(ins?.opcode.ToString());
//                acc.Append(" : ");
//                acc.Append(ins?.operand?.ToString());
//                acc.Append("\n"); 
//                return acc; 
//            });

            //instructionString = output.ToString();

            return instructionList;
        }
        
        static bool Prefix(LaserEffect __instance)
        {
            Logger.Debug($"hit index: {Traverse.Create(__instance).Field("hitIndex").GetValue<int>()}");
            //Logger.Debug(instructionString);
            return true;
        }
    }
    
    //[HarmonyPatch(typeof(AttackDirector.AttackSequence), "OnAttackSequenceFire")]
    public static class ReallyGoFuckYourself
    {
        static string instructionString;

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            // this happens:
            //   float hitDamage = weapon.DamagePerShotAdjusted(weapon.parent.occupiedDesignMask);
            // the right hand of which is turned into this IL:
            //   IL_03dc: ldloc.3
            //   IL_03dd: ldloc.3
            //   IL_03de: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
            //   IL_03e3: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
            //   IL_03e8: callvirt instance float32 BattleTech.Weapon::DamagePerShotAdjusted(class BattleTech.DesignMaskDef)
            // we want to call out to our VariantWeapon calculator w/ "weapon" and the "weapon.parent.occupiedDesignMask" values
            // as params. So we want to produce the following IL instead:
            //   IL_03dc: ldloc.3
            //   IL_03dd: ldloc.3
            //   IL_03de: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
            //   IL_03e3: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
            //   IL_03e8: call instance float32 VariantWeaponCalculator.VariantDamage(class Weapon, class BattleTech.DesignMaskDef)
            var output = new StringBuilder();
            List<CodeInstruction> instructionList = instructions.ToList();
            var originalMethodInfo = AccessTools.Method(typeof(Weapon), "DamagePerShotAdjusted", new Type[] {typeof(DesignMaskDef)});
            var newMethodInfo = AccessTools.Method(typeof(VariantWeapon), "VariantDamage", new Type[] {typeof(Weapon), typeof(DesignMaskDef)});
            var index = instructionList.FindIndex(instruction => instruction.operand == originalMethodInfo);
            instructionList = instructionList.MethodReplacer(originalMethodInfo, newMethodInfo).ToList();
            instructionList[index].opcode = OpCodes.Call;
            output.Append($"index: {index}\n");

//            instructionList.ToArray().Aggregate(output, (acc, ins) => 
//            {
//                ins?.labels?.ForEach((obj) => acc.Append($"{obj.ToString()} : {obj.GetHashCode()}]\n" ));
//                acc.Append(ins?.opcode.ToString());
//                acc.Append(" : ");
//                acc.Append(ins?.operand?.ToString());
//                acc.Append("\n"); 
//                return acc; 
//            });

            instructionString = output.ToString();

            return instructions;
        }

        static bool Prefix(MessageCenterMessage message, AttackDirector.AttackSequence __instance)
        {
            Logger.Debug(instructionString);
            return true;
        }
    }
    
//    [HarmonyPatch(typeof(Mech), "ResolveWeaponDamage",
//        new[] {typeof(WeaponHitInfo), typeof(Weapon), typeof(MeleeAttackType)})]
    public static class GoFuckYourself
    {
        private static bool Prefix(WeaponHitInfo hitInfo, Weapon weapon, MeleeAttackType meleeAttackType,
            Mech __instance)
        {
            var damageVariance = weapon.weaponDef.DamageVariance;
            if (damageVariance == 0) return true;
            var mech = __instance;
            var damagePerShot = weapon.DamagePerShotAdjusted();
            var varianceRange = new Vector2(damagePerShot - damageVariance, damagePerShot + damageVariance);
            var damage = Random.Range(varianceRange.x, varianceRange.y);
            var combat = Traverse.Create(weapon).Field("combat").GetValue<CombatGameState>();
            var damageWDesign = damage * weapon.GetMaskDamageMultiplier(weapon.parent.occupiedDesignMask);
            var __result = damageWDesign * weapon.GetMaskDamageMultiplier(combat.MapMetaData.biomeDesignMask);
            Logger.Debug(
                $"weapon: {weapon.Name} {weapon.UIName}\n" +
                $"damage variance: {damageVariance}\n" +
                $"variance range: {varianceRange.x} {varianceRange.y}\n" +
                $"damage per shot baseline: {damagePerShot}\n" +
                $"damage: {damage}\n" +
                $"damage w/ design mask: {damageWDesign}\n" +
                $"damage w/ env: {__result}"
            );

            ////

            var attackSequence =
                mech.Combat.AttackDirector.GetAttackSequence(hitInfo.attackSequenceId);
//			float num;
//			if (weapon.parent == null)
//			{
//				num = weapon.DamagePerShot;
//			}
//			else
//			{
//				num = weapon.DamagePerShotAdjusted(weapon.parent.occupiedDesignMask);
//			}
            var abstractActor = mech.Combat.FindActorByGUID(hitInfo.attackerId);
            var lineOfFireLevel =
                abstractActor.VisibilityCache.VisibilityToTarget(mech).LineOfFireLevel;
            damage = mech.GetAdjustedDamage(damage, weapon.Category, mech.occupiedDesignMask,
                lineOfFireLevel, false);
            var dictionary = hitInfo.ConsolidateCriticalHitInfo(damage);
            foreach (var keyValuePair in dictionary)
                if (keyValuePair.Key != 0 && keyValuePair.Key != 65536 &&
                    mech.ArmorForLocation(keyValuePair.Key) <= 0f)
                {
                    var chassisLocationFromArmorLocation =
                        MechStructureRules.GetChassisLocationFromArmorLocation((ArmorLocation) keyValuePair.Key);
                    if (!mech.IsLocationDestroyed(chassisLocationFromArmorLocation))
                        Traverse.Create(mech)
                            .Method("CheckForCrit", hitInfo, chassisLocationFromArmorLocation, weapon).GetValue();
                }

            if (weapon.HeatDamagePerShot > 0f)
            {
                for (var i = 0; i < hitInfo.numberOfShots; i++)
                    if (hitInfo.hitLocations[i] != 0 && hitInfo.hitLocations[i] != 65536)
                        mech.AddExternalHeat(string.Format("Heat Damage from {0}", weapon.Description.Name),
                            (int) weapon.HeatDamagePerShotAdjusted(hitInfo.hitQualities[i]));

                if (attackSequence != null) attackSequence.FlagAttackDidHeatDamage();
            }

            var instability = hitInfo.ConsolidateInstability(
                weapon.Instability(),
                mech.Combat.Constants.ResolutionConstants.GlancingBlowDamageMultiplier,
                mech.Combat.Constants.ResolutionConstants.NormalBlowDamageMultiplier,
                mech.Combat.Constants.ResolutionConstants.SolidBlowDamageMultiplier
            );
            instability *= mech.StatCollection.GetValue<float>("ReceivedInstabilityMultiplier");
            instability *= mech.EntrenchedMultiplier;
            mech.AddAbsoluteInstability(instability, StabilityChangeSource.Attack, hitInfo.attackerId);

            //// 
            var fuckyou = false;
            return fuckyou;
        }
    }

//	[HarmonyPatch(typeof(Weapon), "DamagePerShotAdjusted", new[] {typeof(DesignMaskDef)})]
//    public static class Weapon_DamagePerShotAdjusted_Patch
//    {
//        static bool Prefix(DesignMaskDef designMask, Weapon __instance, float __result)
//        {
//            Logger.Debug("Weapon DamagePerShotAdjusted patch hit");
//            var weapon = __instance;
//
//            var damageVariance = weapon.weaponDef.DamageVariance;
//            // the weapon strips damage variance inputs out, but they are still read into the def
//            if (damageVariance != 0) // && ModSettings.VariancePerShot.Contains("foo"))
//            {
//                var damagePerShot = weapon.DamagePerShotAdjusted();
//                var varianceRange = new Vector2(damagePerShot - damageVariance, damagePerShot + damageVariance);
//                var damage = Random.Range(varianceRange.x, varianceRange.y);
//                var combat = Traverse.Create(weapon).Field("combat").GetValue<CombatGameState>();
//                var damageWDesign = damage * weapon.GetMaskDamageMultiplier(designMask);
//                __result = damageWDesign * weapon.GetMaskDamageMultiplier(combat.MapMetaData.biomeDesignMask);
//                Logger.Debug(
//                    $"damage variance: {damageVariance}\n" +
//                    $"variance range: {varianceRange.x} {varianceRange.y}\n" +
//                    $"damage per shot baseline: {damagePerShot}\n" +
//                    $"damage: {damage}\n" +
//                    $"damage w/ design mask: {damageWDesign}\n" +
//                    $"damage w/ env: {__result}"
//                );
//                return false;
//            }
//
//            return true;
//        }
//    }
}