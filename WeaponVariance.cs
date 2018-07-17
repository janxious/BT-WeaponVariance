using System;
using System.Collections.Generic;
using System.Reflection;
using BattleTech;
using Harmony;
using Newtonsoft.Json;
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
                if (ModSettings.debug)
                {
                    HarmonyInstance.DEBUG = true;
                }
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

            Logger.Debug(
                $"some damage numbers:\n" +
                $"weapon damage: {weapon.DamagePerShot}\n" +
                $"weapon damage adjusted: {weapon.DamagePerShotAdjusted()}\n" +
                $"stats based: {weapon.StatCollection.GetValue<float>("DamagePerShot")}"
            );

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
            Logger.Debug($"shotmemokey weaponeffectid: {weaponEffectId}");
            if (weaponEffect is BulletEffect bulletEffect)
            {
                weaponEffectId = Traverse.Create(bulletEffect).Field("parentLauncher").GetValue<BallisticEffect>()
                    .GetInstanceID();
                Logger.Debug($"shotmemokey bullet weaponeffectid: {weaponEffectId}");
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
            Logger.Debug($"variant damage key found: {key.weaponEffectId} / {key.hitIndex}");
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

    #region burst ballistic
    // Burst ballistic effects call out to damage per shot adjusted, but it's in the wrong place,
    // so we have to adjust the function and then move the call a bit
    [HarmonyPatch(typeof(BurstBallisticEffect), "Update")]
    public static class BurstBallistic_Update_Patch
    {
        public static Dictionary<int, bool> _logged = new Dictionary<int, bool>();

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            IEnumerable<CodeInstruction> damagePerShotAdjusted = IntermediateLangaugeFuckery.PerformDamagePerShotAdjustedFuckery(instructions);
            damagePerShotAdjusted = IntermediateLangaugeFuckery.PerformBurstBallisticUpdateDamagePerShotMoveFuckery(damagePerShotAdjusted);
            return damagePerShotAdjusted;
        }

        static void Postfix(BurstBallisticEffect __instance)
        {
            var id = __instance.GetInstanceID();
            if (!_logged.ContainsKey(id))
            {
                Logger.Debug($"Update burst ballistic effect id: {id}");
                _logged[id] = true;
            }
        }
    }
    #endregion

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

// kenniloggin'
//    [HarmonyPatch(typeof(Weapon), "DamagePerShotAdjusted", new Type[]{})]
//    public static class Weapon_DamagePerShotAdjusted_Patch
//    {
//        static void Prefix(Weapon __instance)
//        {
//            Logger.Debug($"DamagePerShotAdjusted (DamagePerShot): {__instance.DamagePerShot}");                                                                           
//        }
//        static void Postfix(Weapon __instance, float __result)
//        {
//            Logger.Debug($"DamagePerShotAdjusted (result): {__result}");                                                                           
//        }
//
//    }

    namespace ShotCountEnabler
    {
        [HarmonyPatch(typeof(BallisticEffect), "Update")]
        public static class BallisticEffect_Update_Patch
        {
            static readonly Dictionary<int, int> _shotCountHolder = new Dictionary<int, int>();

            static void Prefix(BallisticEffect __instance)
            {
                try
                {
                    var ballisticEffect = __instance;
                    var instance = Traverse.Create(ballisticEffect);
                    var effectId = ballisticEffect.GetInstanceID();

                    var allBullets = instance.Method("AllBulletsComplete").GetValue<bool>();
                    //Logger.Debug($"all bullets? {allBullets}");

                    if (ballisticEffect.currentState != WeaponEffect.WeaponEffectState.WaitingForImpact || !allBullets) return;

                    if (!_shotCountHolder.ContainsKey(effectId))
                    {
                        _shotCountHolder[effectId] = 1;
                        Logger.Debug($"effectId: shotcount for {effectId} added");
                    }

                    var hitIndex = instance.Field("hitIndex");
                    Logger.Debug($"hitIndex before: {hitIndex.GetValue<int>()}");
                    instance.Field("hitIndex").SetValue(_shotCountHolder[effectId] - 1);
                    Logger.Debug($"hitIndex after: {hitIndex.GetValue<int>()}");
                    if (_shotCountHolder[effectId] >= ballisticEffect.hitInfo.numberOfShots)
                    {
                        _shotCountHolder[effectId] = 1;
                        instance.Method("OnImpact", new object[] {VariantWeapon.VariantDamage(ballisticEffect, ballisticEffect.weapon.parent.occupiedDesignMask)}).GetValue();
                        Logger.Debug($"effectId: {effectId} shotcount reset"); 
                    }
                    else
                    {
                        _shotCountHolder[effectId]++;
                        instance.Method("OnImpact", new object[] {VariantWeapon.VariantDamage(ballisticEffect, ballisticEffect.weapon.parent.occupiedDesignMask)}).GetValue();
                        ballisticEffect.Fire(ballisticEffect.hitInfo, 0, 0);
                        Logger.Debug($"effectId: {effectId} shotcount incremented to: {_shotCountHolder[effectId]}");
                    }
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }

        [HarmonyPatch(typeof(BallisticEffect), "OnComplete")]
        public static class BallisticEffect_OnComplete_Patch
        {
            static void Prefix(BallisticEffect __instance, ref float __state)
            {
                try
                {
                    Logger.Debug("Setting damagepershot to zero");
                    var weapon = __instance.weapon;
                    __state = weapon.DamagePerShot;
                    weapon.StatCollection.Set("DamagePerShot", 0f);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }

            static void Postfix(BallisticEffect __instance, float __state)
            {
                try
                {
                    Logger.Debug($"Setting damagepershot back to {__state}");
                    __instance.weapon.StatCollection.Set("DamagePerShot", __state);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }
            }
        }
    } 
}