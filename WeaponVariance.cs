using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using BattleTech;
using Harmony;
using Newtonsoft.Json;
using UnityEngine;
using Random = UnityEngine.Random;
using static WeaponVariance.WeaponVariance;

namespace WeaponVariance
{
    public static class WeaponVariance
    {
        public const string ModName = "WeaponVariance";
        public const string ModId   = "com.joelmeador.WeaponVariance";

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

            var harmony = HarmonyInstance.Create(ModId);
            harmony.PatchAll(Assembly.GetExecutingAssembly());
        }
    }

    internal struct VarianceBounds
    {
        public float min;
        public float max;
        public float standardDeviation;

        public VarianceBounds(float min, float max, float standardDeviation)
        {
            this.min = min;
            this.max = max;
            this.standardDeviation = standardDeviation;
        }
    }

    public static class VariantWeapon
    {
        private static bool runDebug = false;
        public static readonly Dictionary<string, float> WeaponDamageMemo = new Dictionary<string, float>();
        private static uint _nextId = 4_000_000_001u; // start high to avoid collision base persistence. Gives us ~294mil numbers
        private static uint NextId => _nextId++;

        private static float NormalDistibutionRandom(VarianceBounds bounds, int step = -1)
        {   // compute a random number that fits a gaussian function https://en.wikipedia.org/wiki/Gaussian_function
            // iterative w/ limit adapted from https://natedenlinger.com/php-random-number-generator-with-normal-distribution-bell-curve/
            const int iterationLimit = 10;
            var iterations = 0;
            float randomNumber;
            do
            {
                var rand1 = Random.value;
                var rand2 = Random.value;
                var gaussianNumber = Mathf.Sqrt(-2 * Mathf.Log(rand1)) * Mathf.Cos(2 * Mathf.PI * rand2);
                var mean = (bounds.max + bounds.min) / 2;
                randomNumber = (gaussianNumber * bounds.standardDeviation) + mean;
                if (step > 0) randomNumber = Mathf.RoundToInt(randomNumber / step) * step;
                iterations++;
            } while ((randomNumber < bounds.min || randomNumber > bounds.max) && iterations < iterationLimit);
            if (iterations == iterationLimit) randomNumber = (bounds.min + bounds.max) / 2.0f;
            return randomNumber;
        }

        public static float VariantDamage(WeaponEffect weaponEffect, DesignMaskDef designMask)
        {
            var weapon = weaponEffect.weapon;
            if (string.IsNullOrEmpty(weapon.GUID))
            {   // Why is this? Well, the game doesn't generate GUIDs for a lot of objects until the game is saved, but
                // that's not good for us if we're starting a new campaign, or running a skirmish, or new enemies spawn, so we
                // need to generate a GUID for our weapon. Why and how the fuck the game generates "SRC<the one and only>".
                // Instead of loading up the entirety of the game save persistence engine here, I'm just going to wing it with
                // magic strings, which I've just explained. There also appears to be a bug in harmony with calling the same
                // method call to get the save game system's id generator. So, I dunno. Fuck all that. We'll make our own.
                var newGuid = $"SRC<the one and only>_AG_{NextId.ToString()}";
                weapon.SetGuid(newGuid);
            }
            Logger.Debug($"uid: {weapon.uid} | guid: {weapon.GUID} | {weapon.defId}");
            if (WeaponDamageMemo.ContainsKey(weapon.GUID))
            {   // compute once per shot arrggghghhh why didn't the game designers just do this?
                // computers are fast at math but it's already in memory.
                Logger.Debug($"key found: {weapon.GUID}");
                return WeaponDamageMemo[weapon.GUID];
            }
            // we reach for weapondef because Weapon.DamageVariance always returns 0. Really.
            var damageVariance = weapon.weaponDef.DamageVariance;
            if (damageVariance == 0)
            {   // normal weapon!
                WeaponDamageMemo[weapon.GUID] = weapon.DamagePerShotAdjusted(weapon.parent.occupiedDesignMask);
                Logger.Debug($"no variance: {weapon.GUID} / {WeaponDamageMemo[weapon.GUID]}");
                return WeaponDamageMemo[weapon.GUID];
            }
            // the following should match with Weapon.DamagePerShotAdjusted(DesignMaskDef), with
            // the addition of the variance computations
            var damagePerShot = weapon.DamagePerShotAdjusted();
            var bounds = new VarianceBounds(
                min: damagePerShot - damageVariance,
                max: damagePerShot + damageVariance,
                standardDeviation: ModSettings.StandardDeviationVarianceMultiplier * damageVariance
            );
            if (runDebug)
            {
                runDebug = false;
                StringBuilder builder;
                VarianceBounds testBounds;
                testBounds = new VarianceBounds(
                    damagePerShot - damageVariance,
                    damagePerShot + damageVariance,
                    ModSettings.StandardDeviationVarianceMultiplier * damageVariance
                );
                builder = new StringBuilder();
                builder.AppendLine("HEADER");
                for (int i = 0; i < 2000; i++)
                {
                    builder.AppendLine(NormalDistibutionRandom(testBounds, 1).ToString());
                }
                builder.AppendLine($"{testBounds.min} {testBounds.max} {testBounds.standardDeviation}");
                Logger.Debug(builder.ToString());

                testBounds = new VarianceBounds(
                    damagePerShot - 1,
                    damagePerShot + 1,
                    ModSettings.StandardDeviationVarianceMultiplier
                );
                builder = new StringBuilder();
                builder.AppendLine("HEADER");
                for (int i = 0; i < 2000; i++)
                {
                    builder.AppendLine(NormalDistibutionRandom(testBounds).ToString());
                }
                builder.AppendLine($"{testBounds.min} {testBounds.max} {testBounds.standardDeviation}");
                Logger.Debug(builder.ToString());

                testBounds = new VarianceBounds(
                    damagePerShot - 25,
                    damagePerShot + 25,
                    ModSettings.StandardDeviationVarianceMultiplier * 25
                );
                builder = new StringBuilder();
                builder.AppendLine("HEADER");
                for (int i = 0; i < 2000; i++)
                {
                    builder.AppendLine(NormalDistibutionRandom(testBounds).ToString());
                }
                builder.AppendLine($"{testBounds.min} {testBounds.max} {testBounds.standardDeviation}");
                Logger.Debug(builder.ToString());
            }
            var damage = NormalDistibutionRandom(bounds);
            var combat = Traverse.Create(weapon).Field("combat").GetValue<CombatGameState>();
            var damageWDesign = damage * weapon.GetMaskDamageMultiplier(weapon.parent.occupiedDesignMask);
            var result = damageWDesign * weapon.GetMaskDamageMultiplier(combat.MapMetaData.biomeDesignMask);
            Logger.Debug(
                $"weapon: {weapon.Name} {weapon.GUID}\n" +
                $"damage and variance: {damagePerShot}+-{damageVariance}\n" +
                $"damage range: {bounds.min}-{bounds.max} (std. dev. {bounds.standardDeviation}\n" +
                $"computed damage: {damage}\n" +
                $"damage w/ design mask: {damageWDesign}\n" +
                $"damage w/ env: {result}"
            );
            WeaponDamageMemo[weapon.GUID] = result;
            return WeaponDamageMemo[weapon.GUID];
        }
    }

    [HarmonyPatch(typeof(AttackDirector), "OnAttackSequenceEnd")]
    public static class AttackDirector_OnAttackSequenceEnd_Patch
    {
        public static void Postfix(MessageCenterMessage message, AttackDirector __instance)
        {   // we want to clear out our memoized shot damage after every attack sequence
            // because if we don't then we get one random number per variant weapon per game ^_^
            Logger.Debug($"sequence is over: resetting!");
            VariantWeapon.WeaponDamageMemo.Clear();
        }
    }

    [HarmonyPatch(typeof(AttackDirector.AttackSequence), "OnAttackSequenceImpact")]
    public static class AttackDirector__AttackSequence_OnAttackSequenceImpact_Patch
    {
        static bool Prefix(MessageCenterMessage message, AttackDirector.AttackSequence __instance)
        {   // The only point of this is logging the damage that is sent through the system messaging queues
            var attackSequenceImpactMessage = (AttackSequenceImpactMessage)message;
            Logger.Debug($"hit damage: {attackSequenceImpactMessage.hitDamage}");
            return true;
        }
    }

    [HarmonyPatch(typeof(LaserEffect), "OnImpact")]
    public static class LaserEffect_OnImpact_Patch
    {   // o.g. call - for the purposes of our exercise, base/this are the same.
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
        // so (IL is likely slightly wrong):
        //   IL_00ab: ldarg.0
        //   IL_00ac: ldarg.0
        //   IL_00b1: ldfld class BattleTech.Weapon WeaponEffect::weapon
        //   IL_00b2: ldfld class BattleTech.AbstractActor BattleTech.MechComponent::parent
        //   IL_00b7: callvirt instance class BattleTech.DesignMaskDef BattleTech.AbstractActor::get_occupiedDesignMask()
        //   IL_00cc: call instance float32 VariantWeapon::VariantDamage(class WeaponEffect, class BattleTech.DesignMaskDef)
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> instructionList = instructions.ToList();
            var originalMethodInfo = AccessTools.Method(typeof(Weapon), "DamagePerShotAdjusted", new Type[] {typeof(DesignMaskDef)});
            var index = instructionList.FindIndex(instruction => instruction.operand == originalMethodInfo);
            var newMethodInfo = AccessTools.Method(typeof(VariantWeapon), "VariantDamage", new Type[] {typeof(WeaponEffect), typeof(DesignMaskDef)});
            instructionList[index].operand = (object) newMethodInfo;
            instructionList[index].opcode = OpCodes.Call; // static method use call
            instructionList.RemoveAt(index - 5); // nuke call to "this.weapon" so "this" is on the stack as first arg to variant damage
            return instructionList;
        }
    }

    [HarmonyPatch(typeof(LaserEffect), "PlayImpact")]
    public static class LaserEffect_PlayImpact_Patch
    {   // o.g.
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
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> instructionList = instructions.ToList();
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