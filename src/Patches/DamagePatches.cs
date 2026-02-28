using HarmonyLib;
using UnityEngine;
using Falcon.Damage;
using TCAMultiplayer.Player;
using TCAMultiplayer.Networking;
using TCAMultiplayer;
using System;
using System.Reflection;
using Falcon.World;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for damage system to sync damage between players
    ///
    /// Design: Victim-side authority
    /// - Remote projectiles (bullets/missiles/bombs) are spawned on the victim's machine
    /// - The game engine handles damage natively when those projectiles hit the local aircraft
    /// - The attacker-side prefixes only BLOCK damage on clones (no packets sent)
    /// - Kill credit is tracked via SourceTarget → RemoteAircraftController.PlayerId
    /// </summary>
    [HarmonyPatch]
    public static class DamagePatches
    {
        // Damage type constants
        private const byte DAMAGE_TYPE_BULLET = 0;
        private const byte DAMAGE_TYPE_EXPLOSIVE = 1;
        
        /// <summary>
        /// Set to true when we've already handled destruction via DamagePatches
        /// so that FlightGamePatches.CheckForRespawn doesn't double-fire.
        /// Reset to false on respawn.
        /// </summary>
        public static bool DestructionHandled { get; set; } = false;

        /// <summary>
        /// Track the last remote attacker for kill credit on delayed deaths.
        /// When a player takes damage then crashes shortly after, the attacker
        /// still gets kill credit within a 15-second window.
        /// </summary>
        public static ulong LastAttackerId { get; set; } = 0;
        public static string LastAttackerWeapon { get; set; } = "";
        public static float LastDamageTime { get; set; } = 0f;

        // Explosion debounce: a single Explosion.Trigger() can call ApplyDamageFromExplosion
        // on the same victim multiple times in one frame (multiple Damageable children hit).
        // We aggregate the first hit and skip subsequent ones within the debounce window.
        private static ulong _lastExplosionVictimId = 0;
        private static float _lastExplosionTime = 0f;
        private static float _lastExplosionDamage = 0f;
        private const float EXPLOSION_DEBOUNCE_SECONDS = 0.15f;

        /// <summary>
        /// Get the local player's peer ID
        /// </summary>
        private static ulong GetLocalPlayerId() => Plugin.Instance?.Network?.LocalPeerId ?? 0;

        /// <summary>
        /// Patch Damageable.ApplyDamageFromImpact to intercept damage on remote aircraft
        /// </summary>
        [HarmonyPatch(typeof(Damageable), "ApplyDamageFromImpact")]
        [HarmonyPrefix]
        public static bool ApplyDamageFromImpact_Prefix(
            Damageable __instance,
            DamageSource damageSource)
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return true;
                if (!Plugin.Instance.Network.IsConnected) return true;

                // Check if this Damageable belongs to a remote player's cloned aircraft
                var controller = __instance.GetComponentInParent<RemoteAircraftController>();
                if (controller == null) return true; // Not a remote aircraft, let damage proceed normally

                // Check if the remote aircraft is already destroyed - don't send more damage
                if (controller.IsDestroyed)
                {
                    LogHelper.Info(LogCategory.Damage, "[DamagePatches] Remote aircraft already destroyed, ignoring damage");
                    return false; // Skip damage - aircraft is dead
                }

                // DAMAGE AUTHORITY: Victim-side only.
                // The remote player's machine spawns our bullets/missiles via SetRemoteGunFiring /
                // HandleMissileLaunch. Those projectiles hit the victim's LOCAL aircraft and damage
                // applies natively through the game engine. We do NOT send a DamagePacket because
                // that would cause DOUBLE DAMAGE (native hit + packet hit on the receiver).
                //
                // We still block damage on the clone (return false) so the clone's HP doesn't
                // desync or trigger false destruction effects on the attacker's side.
                return false; // Block damage on clone — victim handles their own damage natively
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] ApplyDamage error: {ex.Message}");
                return true; // On error, let original proceed
            }
        }

        /// <summary>
        /// Postfix on ApplyDamageFromImpact: track attacker identity when LOCAL aircraft
        /// takes damage from a remote player's bullets (for kill credit).
        /// </summary>
        [HarmonyPatch(typeof(Damageable), "ApplyDamageFromImpact")]
        [HarmonyPostfix]
        public static void ApplyDamageFromImpact_Postfix(
            Damageable __instance,
            DamageSource damageSource)
        {
            TrackRemoteAttacker(__instance, damageSource);
        }

        /// <summary>
        /// Postfix on ApplyDamageFromExplosion: track attacker identity when LOCAL aircraft
        /// takes explosion damage from a remote player's missiles/bombs (for kill credit).
        /// </summary>
        [HarmonyPatch(typeof(Damageable), "ApplyDamageFromExplosion")]
        [HarmonyPostfix]
        public static void ApplyDamageFromExplosion_Postfix(
            Damageable __instance,
            DamageSource damageSource)
        {
            TrackRemoteAttacker(__instance, damageSource);
        }

        /// <summary>
        /// If this damage was caused by a remote player's projectile, record their ID
        /// for kill credit. The projectile's FiredFrom/Ownship Target has a
        /// RemoteAircraftController with the attacker's PlayerId.
        /// </summary>
        private static void TrackRemoteAttacker(Damageable damageable, DamageSource damageSource)
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return;
                if (!Plugin.Instance.Network.IsConnected) return;

                // Only track on the LOCAL player's aircraft (not on clones)
                var controller = damageable.GetComponentInParent<RemoteAircraftController>();
                if (controller != null) return; // This is a clone, not our aircraft

                // Check if the damage source has a SourceTarget from a remote player
                if (damageSource.SourceTarget == null) return;

                var attackerController = damageSource.SourceTarget.GetComponentInParent<RemoteAircraftController>();
                if (attackerController == null) return; // Damage from AI or self, not a remote player

                // Record the remote attacker for kill credit
                LastAttackerId = attackerController.PlayerId;
                LastAttackerWeapon = damageSource.Weapon ?? "Unknown";
                LastDamageTime = UnityEngine.Time.time;
            }
            catch { } // Never let tracking errors break the damage pipeline
        }

        /// <summary>
        /// Patch Damageable.ApplyDamageFromExplosion to intercept MISSILE/BOMB damage on remote aircraft
        /// This is called by Explosion.Trigger() for explosive weapons
        /// </summary>
        [HarmonyPatch(typeof(Damageable), "ApplyDamageFromExplosion")]
        [HarmonyPrefix]
        public static bool ApplyDamageFromExplosion_Prefix(
            Damageable __instance,
            DamageSource damageSource)
        {
            try
            {
                if (Plugin.Instance == null || Plugin.Instance.Network == null) return true;
                if (!Plugin.Instance.Network.IsConnected) return true;

                // Check if this Damageable belongs to a remote player's cloned aircraft
                var controller = __instance.GetComponentInParent<RemoteAircraftController>();
                if (controller == null) return true; // Not a remote aircraft, let damage proceed normally

                // Check if the remote aircraft is already destroyed
                if (controller.IsDestroyed)
                {
                    LogHelper.Info(LogCategory.Damage, "[DamagePatches] Remote aircraft already destroyed, ignoring explosion damage");
                    return false;
                }
                // DAMAGE AUTHORITY: Victim-side only.
                // The remote player's machine spawns our missiles/bombs via HandleMissileLaunch /
                // SpawnNetworkBomb. Those munitions hit the victim's LOCAL aircraft and damage
                // applies natively. We do NOT send a DamagePacket — that would cause DOUBLE DAMAGE.
                return false; // Block damage on clone — victim handles their own damage natively
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] ApplyDamageFromExplosion error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// NOTE: With victim-side damage authority, DamagePackets are no longer sent by the
        /// attacker (bullets/missiles are spawned on the victim's machine and hit natively).
        /// This handler remains as a safety fallback in case any code path still sends damage.
        /// </summary>
        public static void HandleReceivedDamage(DamagePacket packet)
        {
            try
            {
                Plugin.Log?.LogInfo($"[DamagePatches] Received damage: {packet.Damage} type={packet.DamageType} from player {packet.AttackerId}, weapon: {packet.WeaponName}");

                // === MISSILE DAMAGE HANDLING ===
                // Network missiles use damage packets because Munition.Explode() may not apply damage correctly
                // (the Munition component was disabled for network control, which can break its internal state)
                // We apply explosive damage directly from the packet instead of relying on the game engine.

                // === HIT VALIDATION ===
                // Validate the damage packet to prevent cheating

                // 1. Validate damage amount (reasonable bounds)
                const int MAX_REASONABLE_DAMAGE = 10000;
                if (packet.Damage < 0 || packet.Damage > MAX_REASONABLE_DAMAGE)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] Rejecting damage packet: unreasonable damage value {packet.Damage}");
                    return;
                }

                // 2. Validate attacker is actually a connected player
                ulong localPeerId = Plugin.Instance?.Network?.LocalPeerId ?? 0;
                if (packet.AttackerId == localPeerId || packet.AttackerId == 0)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] Rejecting damage packet: invalid attacker ID {packet.AttackerId}");
                    return;
                }

                // 3. Validate victim is us
                if (packet.VictimId != localPeerId)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] Rejecting damage packet: victim {packet.VictimId} is not us {localPeerId}");
                    return;
                }

                // Find local player's aircraft
                var localAircraft = FindLocalPlayerAircraft();
                if (localAircraft == null)
                {
                    Plugin.Log?.LogWarning("[DamagePatches] No local aircraft found to apply damage");
                    return;
                }

                // 4. Validate hit position is near our aircraft
                Vector3d absoluteHitPos = new Vector3d(packet.HitPosX, packet.HitPosY, packet.HitPosZ);
                Vector3 localHitPos = FloatingOriginHelper.AbsoluteToLocal(absoluteHitPos);
                Vector3 aircraftPos = localAircraft.transform.position;

                const float MAX_HIT_DISTANCE = 100f; // Max distance from aircraft center for valid hit
                float hitDistance = Vector3.Distance(localHitPos, aircraftPos);
                if (hitDistance > MAX_HIT_DISTANCE)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] Rejecting damage packet: hit position {localHitPos} too far from aircraft {aircraftPos} (dist={hitDistance:F1}m)");
                    return;
                }

                Plugin.Log?.LogInfo($"[DamagePatches] Hit validated: distance={hitDistance:F1}m, pos absolute={absoluteHitPos} local={localHitPos}");

                // Zero damage (non-penetrating hit) — valid packet but no HP reduction needed
                if (packet.Damage <= 0)
                {
                    Plugin.Log?.LogInfo($"[DamagePatches] Non-penetrating hit from {packet.AttackerId} (0 damage), skipping apply");
                    return;
                }

                // Find Damageable component
                var damageable = localAircraft.GetComponentInChildren<Damageable>();
                if (damageable == null)
                {
                    Plugin.Log?.LogWarning("[DamagePatches] No Damageable found on local aircraft");
                    return;
                }

                // Create DamageSource and apply damage
                var damageSource = new DamageSource
                {
                    Damage = packet.Damage,
                    Penetration = packet.Penetration,
                    HitPosition = localHitPos, // Use converted local position
                    Weapon = packet.WeaponName,
                    IsCausedByWeapon = true,
                    SourceTarget = null, // Remote attacker
                    DamageTime = Time.time
                };

                // Apply damage using the public method - wrapped in separate try-catch to prevent exceptions from breaking pipeline
                try
                {
                    if (packet.DamageType == 1) // Explosive damage
                    {
                        damageable.ApplyDamageFromExplosion(damageSource);
                        Plugin.Log?.LogInfo($"[DamagePatches] Applied {packet.Damage} EXPLOSION damage to local aircraft. HP: {damageable.HitPoints}/{damageable.MaxHitpoints}");
                    }
                    else
                    {
                        damageable.ApplyDamageFromImpact(damageSource);
                        Plugin.Log?.LogInfo($"[DamagePatches] Applied {packet.Damage} IMPACT damage to local aircraft. HP: {damageable.HitPoints}/{damageable.MaxHitpoints}");
                    }
                }
                catch (Exception applyEx)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] ApplyDamage threw exception (damage may not have applied): {applyEx.Message}");
                    // Continue execution - don't let a single damage application failure break the whole system
                }

                // Track last attacker for kill credit on delayed deaths
                // (e.g., damage weakens aircraft → aircraft crashes seconds later)
                LastAttackerId = packet.AttackerId;
                LastAttackerWeapon = packet.WeaponName;
                LastDamageTime = Time.time;

                if (LogHelper.IsEnabled(LogCategory.Damage) &&
                    LogHelper.ShouldSample("DamagePatches.ApplyDetails", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Damage,
                        $"[DamagePatches] Applied from attacker {packet.AttackerId}, weapon={packet.WeaponName}, " +
                        $"absolutePos=({packet.HitPosX:F1},{packet.HitPosY:F1},{packet.HitPosZ:F1}) localPos={localHitPos}");
                }

                // Check if destroyed
                if (damageable.IsDestroyed && !DestructionHandled)
                {
                    DestructionHandled = true;
                    Plugin.Log?.LogInfo("[DamagePatches] Local aircraft destroyed by enemy fire!");
                    Plugin.Instance?.Network?.SendAircraftDestroyedNotification();
                    
                    // Send kill confirmation so the attacker (and all peers) get scoreboard credit
                    Plugin.Instance?.Network?.SendKillConfirmation(packet.AttackerId, GetLocalPlayerId(), packet.WeaponName);
                    
                    // Record the kill locally too
                    Game.ScoreTracker.Instance?.RecordKill(packet.AttackerId, GetLocalPlayerId(), packet.WeaponName);
                    
                    // Trigger respawn UI
                    Game.SpawnManager.Instance?.NotifyPlayerDied();
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] HandleReceivedDamage error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static GameObject FindLocalPlayerAircraft()
        {
            try
            {
                var aircrafts = UnityEngine.Object.FindObjectsByType<Falcon.UniversalAircraft.UniAircraft>(FindObjectsSortMode.None);
                if (aircrafts != null && aircrafts.Length > 0)
                {
                    foreach (var aircraft in aircrafts)
                    {
                        // Skip aircraft with RemoteAircraftController (those are clones)
                        if (aircraft.GetComponent<RemoteAircraftController>() != null) continue;
                        return aircraft.gameObject;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] FindLocalPlayerAircraft error: {ex.Message}");
            }
            return null;
        }



        /// <summary>
        /// Determine the effect type based on what was hit
        /// </summary>
        private static byte DetermineEffectType(Collider hitCollider)
        {
            if (hitCollider == null) return 0; // metal/hard default

            // Check physics material if available
            var material = hitCollider.sharedMaterial;
            if (material != null)
            {
                string matName = material.name.ToLowerInvariant();
                if (matName.Contains("water")) return 2;     // water
                if (matName.Contains("ground") || matName.Contains("dirt") ||
                    matName.Contains("grass") || matName.Contains("sand")) return 1; // soft
            }

            // Check layer
            int layer = hitCollider.gameObject.layer;
            string layerName = LayerMask.LayerToName(layer).ToLowerInvariant();
            if (layerName.Contains("terrain")) return 1;   // soft/ground
            if (layerName.Contains("water")) return 2;     // water

            return 0; // metal/hard default (aircraft)
        }

        /// <summary>
        /// Handle received impact packet - spawn visual effects at impact location
        /// Called from NetworkManager when ProjectileImpact packet is received
        /// </summary>
        public static void HandleReceivedImpact(ProjectileImpactPacket packet)
        {
            try
            {
                ulong localPlayerId = Plugin.Instance?.Network?.IsHost == true ? 1UL : 2UL;

                // Only process impacts where we are the victim
                if (packet.VictimId != localPlayerId) return;

                // Convert ABSOLUTE position to LOCAL for spawning effects
                Vector3d absolutePos = new Vector3d(packet.ImpactPosX, packet.ImpactPosY, packet.ImpactPosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);
                Vector3 impactDir = new Vector3(packet.ImpactDirX, packet.ImpactDirY, packet.ImpactDirZ);

                Plugin.Log?.LogInfo($"[DamagePatches] Spawning impact FX at {localPos}, type={packet.ImpactType}, weapon={packet.WeaponName}");

                // Spawn appropriate visual effect
                CombatVfxManager.SpawnImpactEffect(localPos, impactDir, packet.ImpactType, packet.EffectType, packet.Damage, packet.WeaponName);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] HandleReceivedImpact error: {ex.Message}");
            }
        }

        /// <summary>
        /// Reset static state between game sessions.
        /// </summary>
        public static void ResetState()
        {
            DestructionHandled = false;
            LastAttackerId = 0;
            LastAttackerWeapon = "";
            LastDamageTime = 0f;
            _lastExplosionVictimId = 0;
            _lastExplosionTime = 0f;
            _lastExplosionDamage = 0f;
        }
    }
}
