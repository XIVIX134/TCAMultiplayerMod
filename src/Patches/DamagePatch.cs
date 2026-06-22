using HarmonyLib;
using Falcon.Damage;
using Falcon.Targeting;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Blocks damage application on remote aircraft clones.
    /// Remote clones receive damage via network packets, not local simulation.
    /// When damage is blocked, fires OnCloneDamageBlocked so the network layer
    /// can forward the damage to the clone's owner.
    /// </summary>
    [HarmonyPatch(typeof(Damageable))]
    internal static class DamagePatch
    {
        /// <summary>
        /// Delegate to check if a Damageable belongs to a remote clone.
        /// Set by the session layer when connecting.
        /// </summary>
        public static System.Func<Damageable, bool> IsRemoteClone;
        public static System.Func<Target, bool> IsRemoteSourceTarget;
        public static System.Func<Damageable, DamageSource, bool> AllowRemoteCloneNativeDamage;
        public static int NetworkDamageDepth;

        /// <summary>
        /// Callback when damage is blocked on a remote clone.
        /// Allows the network layer to forward the damage to the clone's owner.
        /// </summary>
        public static System.Action<Damageable, DamageSource, bool> OnCloneDamageBlocked;

        [HarmonyPrefix]
        [HarmonyPatch("ApplyDamageFromImpact")]
        static bool ApplyDamageFromImpactPrefix(Damageable __instance, DamageSource damageSource)
        {
            if (IsRemoteClone?.Invoke(__instance) == true)
            {
                if (NetworkDamageDepth > 0)
                    return true;

                if (AllowRemoteCloneNativeDamage?.Invoke(__instance, damageSource) == true)
                    return true;

                if (ShouldBlockRemoteNativeDamage(damageSource))
                    return false;

                // Clone hit by local authority; forward damage info to network layer.
                OnCloneDamageBlocked?.Invoke(__instance, damageSource, false);
                return false;
            }

            return !ShouldBlockRemoteNativeDamage(damageSource);
        }

        [HarmonyPrefix]
        [HarmonyPatch("ApplyDamageFromExplosion")]
        static bool ApplyDamageFromExplosionPrefix(Damageable __instance, DamageSource damageSource)
        {
            if (IsRemoteClone?.Invoke(__instance) == true)
            {
                if (NetworkDamageDepth > 0)
                    return true;

                if (AllowRemoteCloneNativeDamage?.Invoke(__instance, damageSource) == true)
                    return true;

                if (ShouldBlockRemoteNativeDamage(damageSource))
                    return false;

                OnCloneDamageBlocked?.Invoke(__instance, damageSource, true);
                return false;
            }

            return !ShouldBlockRemoteNativeDamage(damageSource);
        }

        private static bool ShouldBlockRemoteNativeDamage(DamageSource damageSource)
        {
            if (NetworkDamageDepth > 0)
                return false;

            return damageSource.IsCausedByWeapon
                && damageSource.SourceTarget != null
                && IsRemoteSourceTarget?.Invoke(damageSource.SourceTarget) == true;
        }
    }
}
