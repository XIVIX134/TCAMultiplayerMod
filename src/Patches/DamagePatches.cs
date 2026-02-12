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
    /// Design: Shooter Authority
    /// - When local player shoots and hits the remote aircraft clone, we detect the damage
    /// - We send a damage packet to the remote player
    /// - Remote player receives and applies damage to their real aircraft
    /// </summary>
    [HarmonyPatch]
    public static class DamagePatches
    {
        // Damage type constants
        private const byte DAMAGE_TYPE_BULLET = 0;
        private const byte DAMAGE_TYPE_EXPLOSIVE = 1;

        /// <summary>
        /// Get the local player's peer ID
        /// </summary>
        private static ulong GetLocalPlayerId() => Plugin.Instance.Network.LocalPeerId;

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

                // This is damage on a REMOTE aircraft clone (the enemy we're shooting at)
                // We need to send this damage to the remote player

                Plugin.Log?.LogInfo($"[DamagePatches] Hit detected on remote aircraft! Damage: {damageSource.Damage}");
                if (LogHelper.IsEnabled(LogCategory.Damage) &&
                    LogHelper.ShouldSample("DamagePatches.ImpactDetails", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Damage,
                        $"[DamagePatches] Impact details weapon={damageSource.Weapon ?? "Unknown"} " +
                        $"pen={damageSource.Penetration} hitPos={damageSource.HitPosition}");
                }

                // Convert hit position to ABSOLUTE coordinates for network sync
                Vector3d absoluteHitPos = FloatingOriginHelper.LocalToAbsolute(damageSource.HitPosition);

                Plugin.Log?.LogInfo($"[DamagePatches] Hit pos local={damageSource.HitPosition} absolute={absoluteHitPos}");

                // Determine damage type based on weapon name
                byte damageType = DAMAGE_TYPE_BULLET;
                string weaponUpper = (damageSource.Weapon ?? "").ToUpperInvariant();
                if (weaponUpper.Contains("AIM") || weaponUpper.Contains("MISSILE") ||
                    weaponUpper.Contains("ROCKET") || weaponUpper.Contains("BOMB"))
                {
                    damageType = DAMAGE_TYPE_EXPLOSIVE;
                }

                // Build damage packet with ABSOLUTE coordinates
                var packet = new DamagePacket
                {
                    VictimId = controller.PlayerId,
                    AttackerId = GetLocalPlayerId(),
                    Damage = damageSource.Damage,
                    Penetration = damageSource.Penetration,
                    DamageType = damageType,
                    HitPosX = absoluteHitPos.x,
                    HitPosY = absoluteHitPos.y,
                    HitPosZ = absoluteHitPos.z,
                    WeaponName = damageSource.Weapon ?? "Unknown"
                };

                // Send damage packet to remote player
                byte[] data = PacketSerializer.SerializeDamage(packet);
                Plugin.Instance.Network.SendPacket(PacketType.DamageDealt, data, reliable: true);

                Plugin.Log?.LogInfo($"[DamagePatches] Sent damage packet: {packet.Damage} damage from {packet.WeaponName}");
                if (LogHelper.IsEnabled(LogCategory.Damage) &&
                    LogHelper.ShouldSample("DamagePatches.PacketDetails", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Damage,
                        $"[DamagePatches] Packet victim={packet.VictimId} attacker={packet.AttackerId} " +
                        $"pos=({packet.HitPosX:F1},{packet.HitPosY:F1},{packet.HitPosZ:F1})");
                }

                // Also send impact packet for FX synchronization
                // This ensures the victim sees the impact effect at the correct location
                var impactPacket = new ProjectileImpactPacket
                {
                    AttackerId = GetLocalPlayerId(),
                    VictimId = controller.PlayerId,
                    ImpactType = damageType,
                    EffectType = DetermineEffectType(damageSource.HitCollider),
                    ImpactPosX = absoluteHitPos.x,
                    ImpactPosY = absoluteHitPos.y,
                    ImpactPosZ = absoluteHitPos.z,
                    ImpactDirX = -damageSource.HitPosition.normalized.x,
                    ImpactDirY = -damageSource.HitPosition.normalized.y,
                    ImpactDirZ = -damageSource.HitPosition.normalized.z,
                    Damage = damageSource.Damage,
                    WeaponName = damageSource.Weapon ?? "Unknown"
                };

                byte[] impactData = PacketSerializer.SerializeProjectileImpact(impactPacket);
                Plugin.Instance.Network.SendPacket(PacketType.ProjectileImpact, impactData, reliable: true);
                Plugin.Log?.LogInfo($"[DamagePatches] Sent impact FX packet at ({absoluteHitPos.x:F1},{absoluteHitPos.y:F1},{absoluteHitPos.z:F1})");

                // Don't apply damage locally to the clone - the remote player will handle their own HP
                return false; // Skip original method - don't apply damage to clone
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] ApplyDamage error: {ex.Message}");
                return true; // On error, let original proceed
            }
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

                // This is EXPLOSION damage on a REMOTE aircraft clone (missile hit!)
                Plugin.Log?.LogInfo($"[DamagePatches] EXPLOSION hit on remote aircraft! Damage: {damageSource.Damage}, Weapon: {damageSource.Weapon}");

                // Convert hit position to ABSOLUTE coordinates for network sync
                Vector3d absoluteHitPos = FloatingOriginHelper.LocalToAbsolute(damageSource.HitPosition);

                // Build damage packet - mark as explosive
                var packet = new DamagePacket
                {
                    VictimId = controller.PlayerId,
                    AttackerId = GetLocalPlayerId(),
                    Damage = damageSource.Damage,
                    Penetration = damageSource.Penetration,
                    DamageType = 1, // 1 = missile/explosive
                    HitPosX = absoluteHitPos.x,
                    HitPosY = absoluteHitPos.y,
                    HitPosZ = absoluteHitPos.z,
                    WeaponName = damageSource.Weapon ?? "Explosion"
                };

                // Send damage packet
                byte[] data = PacketSerializer.SerializeDamage(packet);
                Plugin.Instance.Network.SendPacket(PacketType.DamageDealt, data, reliable: true);

                Plugin.Log?.LogInfo($"[DamagePatches] Sent EXPLOSION damage packet: {packet.Damage} damage from {packet.WeaponName}");

                // Also send impact packet for FX
                var impactPacket = new ProjectileImpactPacket
                {
                    AttackerId = GetLocalPlayerId(),
                    VictimId = controller.PlayerId,
                    ImpactType = 1, // explosive
                    EffectType = 2, // explosion effect
                    ImpactPosX = absoluteHitPos.x,
                    ImpactPosY = absoluteHitPos.y,
                    ImpactPosZ = absoluteHitPos.z,
                    ImpactDirX = 0,
                    ImpactDirY = 1,
                    ImpactDirZ = 0,
                    Damage = damageSource.Damage,
                    WeaponName = damageSource.Weapon ?? "Explosion"
                };

                byte[] impactData = PacketSerializer.SerializeProjectileImpact(impactPacket);
                Plugin.Instance.Network.SendPacket(PacketType.ProjectileImpact, impactData, reliable: true);

                // Don't apply damage locally to the clone
                return false;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[DamagePatches] ApplyDamageFromExplosion error: {ex.Message}");
                return true;
            }
        }

        /// <summary>
        /// Handle received damage packet - apply damage to local player's aircraft
        /// Called from NetworkManager when DamageDealt packet is received
        /// Includes hit validation to prevent cheating
        /// </summary>
        public static void HandleReceivedDamage(DamagePacket packet)
        {
            try
            {
                Plugin.Log?.LogInfo($"[DamagePatches] Received damage: {packet.Damage} from player {packet.AttackerId}, weapon: {packet.WeaponName}");

                // === HIT VALIDATION ===
                // Validate the damage packet to prevent cheating

                // 1. Validate damage amount (reasonable bounds)
                const int MAX_REASONABLE_DAMAGE = 10000;
                const int MIN_DAMAGE = 1;
                if (packet.Damage < MIN_DAMAGE || packet.Damage > MAX_REASONABLE_DAMAGE)
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
                    damageable.ApplyDamageFromImpact(damageSource);
                    Plugin.Log?.LogInfo($"[DamagePatches] Applied {packet.Damage} damage to local aircraft. HP: {damageable.HitPoints}/{damageable.MaxHitpoints}");
                }
                catch (Exception applyEx)
                {
                    Plugin.Log?.LogWarning($"[DamagePatches] ApplyDamageFromImpact threw exception (damage may not have applied): {applyEx.Message}");
                    // Continue execution - don't let a single damage application failure break the whole system
                }
                if (LogHelper.IsEnabled(LogCategory.Damage) &&
                    LogHelper.ShouldSample("DamagePatches.ApplyDetails", LogHelper.HighFreqSampleRate))
                {
                    LogHelper.Info(LogCategory.Damage,
                        $"[DamagePatches] Applied from attacker {packet.AttackerId}, weapon={packet.WeaponName}, " +
                        $"absolutePos=({packet.HitPosX:F1},{packet.HitPosY:F1},{packet.HitPosZ:F1}) localPos={localHitPos}");
                }

                // Check if destroyed
                if (damageable.IsDestroyed)
                {
                    Plugin.Log?.LogInfo("[DamagePatches] Local aircraft destroyed by enemy fire!");
                    Plugin.Instance?.Network?.SendAircraftDestroyedNotification();
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
    }
}
