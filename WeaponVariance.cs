using System;
using System.Collections.Generic;
using System.Reflection;
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

    public static class Utility
    {
        internal static float NormalDistibutionRandom(VarianceBounds bounds, int step = -1)
        {
            // compute a random number that fits a gaussian function https://en.wikipedia.org/wiki/Gaussian_function
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
    }

    public static class VariantWeapon
    {
        public static readonly Dictionary<ShotMemoKey, float> WeaponDamageMemo = new Dictionary<ShotMemoKey, float>();

        // This method is used by the IL generators in IntermediateLanguageFuckery
        public static float VariantDamage(WeaponEffect weaponEffect, DesignMaskDef designMask)
        {
            var weapon = weaponEffect.weapon;
            var key = ShotMemoKey(weaponEffect);            
            if (DamageWasAlreadyCalculated(key, out var variantDamage)) return variantDamage;
            if (IsNonVariantWeapon(key, weapon, out var damageVariance, out var normalDamage)) return normalDamage;

            // the following damage calcs should match with Weapon.DamagePerShotAdjusted(DesignMaskDef), with
            // the addition of the variance computations
            var damagePerShot = weapon.DamagePerShotAdjusted();
            var bounds = new VarianceBounds(
                min: damagePerShot - damageVariance,
                max: damagePerShot + damageVariance,
                standardDeviation: ModSettings.StandardDeviationVarianceMultiplier * damageVariance
            );
            var damage = Utility.NormalDistibutionRandom(bounds);
            var combat = Traverse.Create(weapon).Field("combat").GetValue<CombatGameState>();
            var damageWDesign = damage * weapon.GetMaskDamageMultiplier(weapon.parent.occupiedDesignMask);
            var result = damageWDesign * weapon.GetMaskDamageMultiplier(combat.MapMetaData.biomeDesignMask);
            Logger.Debug(
                $"effect id: {key.weaponEffectId}\n" +
                $"hit index: {key.hitIndex}\n" +
                $"damage and variance: {damagePerShot}+-{damageVariance}\n" +
                $"damage range: {bounds.min}-{bounds.max} (std. dev. {bounds.standardDeviation}\n" +
                $"computed damage: {damage}\n" +
                $"damage w/ design mask: {damageWDesign}\n" +
                $"damage w/ env: {result}"
            );
            WeaponDamageMemo[key] = result;
            return WeaponDamageMemo[key];
        }

        private static ShotMemoKey ShotMemoKey(WeaponEffect weaponEffect)
        {
            var hitIndex = Traverse.Create(weaponEffect).Field("hitIndex").GetValue<int>();
            var weaponEffectId = weaponEffect.GetInstanceID();
            if (weaponEffect is BulletEffect bulletEffect)
            {
                weaponEffectId = Traverse.Create(bulletEffect).Field("parentLauncher").GetValue<BallisticEffect>()
                    .GetInstanceID();
            }

            var key = new ShotMemoKey(weaponEffectId, hitIndex);
            return key;
        }

        private static bool IsNonVariantWeapon(ShotMemoKey key, Weapon weapon, out int damageVariance, out float normalDamage)
        {
            // we reach for weapondef because Weapon.DamageVariance always returns 0. Really.
            damageVariance = weapon.weaponDef.DamageVariance;
            if (damageVariance != 0)
            {
                Logger.Debug($"variance: {key.weaponEffectId} / {key.hitIndex}");
                normalDamage = -1f;
                return false;
            }
            WeaponDamageMemo[key] = weapon.DamagePerShotAdjusted(weapon.parent.occupiedDesignMask);
            Logger.Debug($"no variance: {key.weaponEffectId} / {key.hitIndex}");
            {
                normalDamage = WeaponDamageMemo[key];
                return true;
            }

        }

        private static bool DamageWasAlreadyCalculated(ShotMemoKey key, out float variantDamage)
        {
            if (!WeaponDamageMemo.ContainsKey(key))
            {
                variantDamage = -1f;
                Logger.Debug($"key was not found: {key.weaponEffectId} / {key.hitIndex}");
                return false;
            }
            // compute once per shot arrggghghhh why didn't the game designers just do this?
            // computers are fast at math but it's already in memory.
            Logger.Debug($"key found: {key.weaponEffectId} / {key.hitIndex}");
            variantDamage = WeaponDamageMemo[key];
            return true;
        }
    }

    [HarmonyPatch(typeof(AttackDirector), "OnAttackSequenceBegin")]
    public static class AttackDirector_OnAttackSequenceBegin_Patch
    {
        public static void Postfix(MessageCenterMessage message, AttackDirector __instance)
        {   // we want to clear out our memoized shot damage after every attack sequence
            // because if we don't then we get one random number per weapon per game ^_^
            Logger.Debug($"sequence is beginning: resetting memoized damage rolls!");
            VariantWeapon.WeaponDamageMemo.Clear();
        }
    }

    #region ballistic
    // ballistic is… different. This will effectively do *random* number × bullet count, which is
    // variant, but sort of shitty.
    [HarmonyPatch(typeof(BallisticEffect), "OnComplete")]
    public static class BallisticEffect_OnImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(BallisticEffect __instance)
        {
            Logger.Debug($"OnComplete ballistic effect id: {__instance.GetInstanceID()}");
        }
    }
    #endregion ballistic

    #region missiles
    // Laser effects override WeaponEffect.OnImpact, but does call into base
    [HarmonyPatch(typeof(MissileEffect), "OnImpact")]
    public static class MissileEffect_OnImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }

        static void Postfix(float hitDamage, MissileEffect __instance)
        {
            Logger.Debug($"missile effect id: {__instance.GetInstanceID()}");
        }
    }

    // Laser Effects override WeaponEffect.PlayImpact and does not call into base
    [HarmonyPatch(typeof(MissileEffect), "PlayImpact")]
    public static class MissileEffect_PlayImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(MissileEffect __instance)
        {
            Logger.Debug($"PlayImpact missile effect id: {__instance.GetInstanceID()}");
        }
    }
    #endregion missiles

    #region lasers
    // Laser effects override WeaponEffect.OnImpact, but does call into base
    [HarmonyPatch(typeof(LaserEffect), "OnImpact")]
    public static class LaserEffect_OnImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(float hitDamage, LaserEffect __instance)
        {
            Logger.Debug($"OnImpact laser effect id: {__instance.GetInstanceID()}");
        }
    }

    // Laser Effects override WeaponEffect.PlayImpact and does not call into base
    [HarmonyPatch(typeof(LaserEffect), "PlayImpact")]
    public static class LaserEffect_PlayImpact_Patch
    {  
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(LaserEffect __instance)
        {
            Logger.Debug($"PlayImpact laser effect id: {__instance.GetInstanceID()}");
        }
    }
    #endregion lasers

    #region PPCs
    // PPC effects override WeaponEffect.OnImpact, but does call into base
    [HarmonyPatch(typeof(PPCEffect), "OnImpact")]
    public static class PPCEffect_OnImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(float hitDamage, PPCEffect __instance)
        {
            Logger.Debug($"OnImpact ppc effect id: {__instance.GetInstanceID()}");
        }
    }
    #endregion

    #region melee
    // Melee effects override WeaponEffect.OnImpact, but does call into base
    [HarmonyPatch(typeof(MeleeEffect), "OnImpact")]
    public static class MeleeEffect_OnImpact_Patch
    {
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(float hitDamage, MeleeEffect __instance)
        {
            Logger.Debug($"OnImpact melee effect id: {__instance.GetInstanceID()}");
        }
    }
    #endregion melee

    // Melee and PPC Effects override WeaponEffect.PlayImpact, but just call into base, so we need to 
    // actually patch WeaponEffect.PlayImpact
    // Additionally Ballistic (and thus Gauss) call it directly.
    // As does BurstBallistic.
    [HarmonyPatch(typeof(WeaponEffect), "PlayImpact")]
    public static class WeaponEffect_PlayImpact_Patch
    {  
        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            return IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
        }
        static void Postfix(WeaponEffect __instance)
        {
            Logger.Debug($"PlayImpact (base?) weapon effect id: {__instance.GetInstanceID()}\n{__instance.GetType()}");
        }
    }
}