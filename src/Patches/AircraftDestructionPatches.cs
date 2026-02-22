using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Player;

namespace TCAMultiplayer.Patches
{
    /// <summary>
    /// Patches for syncing aircraft destruction between players.
    ///
    /// PROBLEM:
    ///   The previous implementation manually instantiated a ParticleSystem prefab at the
    ///   crash position. This missed the entire native crash sequence: GibAircraft() which
    ///   hides the normal aircraft mesh, activates gib parts, and physicalizes every mesh
    ///   piece with AddExplosionForce(5000, radius 25m).
    ///
    /// FIX:
    ///   Sender (Postfix on UniAircraft.DestroyAircraft):
    ///     - Only fires for the LOCAL player's aircraft (not AI, not remote clones).
    ///     - Reads private destroyedBy field (DestructionReason: Air/GroundSoft/GroundHard/Water).
    ///     - Sends AircraftDestructionVfxPacket with position, rotation, and destruction reason.
    ///
    ///   Receiver (HandleAircraftDestructionVfx):
    ///     - Finds the remote clone by RemoteAircraftController.PlayerId.
    ///     - Sets clone transform to the crash position/rotation.
    ///     - Sets the private destroyedBy field to the packet's reason.
    ///     - Calls DestroyAircraft() directly on the clone via reflection.
    ///     - The game then handles ALL VFX natively:
    ///         GibAircraft()  → hides normal mesh, shows gibs, physicalizes debris with explosion force
    ///         switch(destroyedBy) → instantiates AirExplosionPrefab / GroundImpactPrefab / WaterImpactPrefab
    ///         SpawnCrater()  → if ground crash near terrain
    ///         Destroy(go)    → cleans up the clone
    ///
    ///   Echo prevention:
    ///     - DestroyAircraftPostfix skips remote clones (RemoteAircraftController != null).
    ///     - WorldDestructionPatches.SetProcessingFlag(true) wraps the DestroyAircraft() call
    ///       so that any SpawnCrater() triggered internally does NOT re-broadcast a CraterSpawn
    ///       packet (the crater packet was already sent separately by SpawnCraterPostfix on the
    ///       victim's machine).
    /// </summary>
    public static class AircraftDestructionPatches
    {
        private static bool _patchesApplied = false;
        private static Type _uniAircraftType;

        // Private field: DestructionReason destroyedBy (enum)
        private static FieldInfo _destroyedByField;
        private static Type _destructionReasonType;

        // UniAircraft.Player static field (local player's aircraft)
        private static FieldInfo _uniAircraftPlayerField;
        private static PropertyInfo _uniAircraftPlayerProp;

        // Cached method for invoking DestroyAircraft() on the clone
        private static MethodInfo _destroyAircraftMethod;

        // DestructionReason enum values (from decompiled code)
        private const byte REASON_AIR         = 0;
        private const byte REASON_GROUND_SOFT = 1;
        private const byte REASON_GROUND_HARD = 2;
        private const byte REASON_WATER       = 3;

        /// <summary>
        /// Apply Harmony patches manually. Called from Plugin.Awake().
        /// </summary>
        public static void ApplyPatches(Harmony harmony)
        {
            if (_patchesApplied) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                _uniAircraftType = ReflectionHelper.GetGameType("Falcon.UniversalAircraft.UniAircraft");
                if (_uniAircraftType == null)
                {
                    Plugin.Log?.LogWarning("[AircraftDestructionPatches] UniAircraft type not found - aircraft destruction sync disabled");
                    return;
                }

                // Private field: DestructionReason destroyedBy
                _destroyedByField = _uniAircraftType.GetField("destroyedBy", flags)
                    ?? _uniAircraftType.GetField("_destroyedBy", flags)
                    ?? _uniAircraftType.GetField("m_destroyedBy", flags);

                // Get the DestructionReason nested enum type
                _destructionReasonType = ReflectionHelper.GetGameType("Falcon.UniversalAircraft.UniAircraft+DestructionReason")
                    ?? ReflectionHelper.GetGameType("Falcon.UniversalAircraft.DestructionReason");

                // UniAircraft.Player — static reference to local player's aircraft
                _uniAircraftPlayerField = _uniAircraftType.GetField("Player", flags);
                _uniAircraftPlayerProp  = _uniAircraftType.GetProperty("Player", flags);

                // Cache DestroyAircraft() for use in HandleAircraftDestructionVfx
                _destroyAircraftMethod = _uniAircraftType.GetMethod("DestroyAircraft",
                    BindingFlags.Public | BindingFlags.Instance);

                Plugin.Log?.LogInfo($"[AircraftDestructionPatches] Reflection: " +
                    $"destroyedBy={_destroyedByField != null}, " +
                    $"DestructionReason={_destructionReasonType != null}, " +
                    $"Player={_uniAircraftPlayerField != null || _uniAircraftPlayerProp != null}, " +
                    $"DestroyAircraft={_destroyAircraftMethod != null}");

                // Patch UniAircraft.DestroyAircraft() — postfix to detect local player crash
                var destroyMethod = _uniAircraftType.GetMethod("DestroyAircraft",
                    BindingFlags.Public | BindingFlags.Instance);
                if (destroyMethod != null)
                {
                    var postfix = typeof(AircraftDestructionPatches).GetMethod("DestroyAircraftPostfix",
                        BindingFlags.Public | BindingFlags.Static);
                    harmony.Patch(destroyMethod, postfix: new HarmonyMethod(postfix));
                    Plugin.Log?.LogInfo("[AircraftDestructionPatches] Patched UniAircraft.DestroyAircraft()");
                }
                else
                {
                    Plugin.Log?.LogWarning("[AircraftDestructionPatches] UniAircraft.DestroyAircraft() method not found!");
                }

                _patchesApplied = true;
                Plugin.Log?.LogInfo("[AircraftDestructionPatches] Aircraft destruction patches applied");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[AircraftDestructionPatches] ApplyPatches error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        /// <summary>
        /// Postfix on UniAircraft.DestroyAircraft().
        /// Fires after the aircraft is destroyed locally — sends VFX packet to remote player.
        /// Only sends if this is the LOCAL player's aircraft (not AI, not remote clone).
        /// </summary>
        public static void DestroyAircraftPostfix(object __instance)
        {
            if (Plugin.Instance?.Network == null || !Plugin.Instance.Network.IsConnected) return;

            try
            {
                var aircraft = __instance as MonoBehaviour;
                if (aircraft == null) return;

                // Skip remote aircraft clones — they already have RemoteAircraftController
                var remoteCtrl = aircraft.GetComponent<RemoteAircraftController>();
                if (remoteCtrl != null) return;

                // Only send for the LOCAL player's aircraft (UniAircraft.Player)
                bool isLocalPlayer = false;
                try
                {
                    object playerRef = _uniAircraftPlayerField != null
                        ? _uniAircraftPlayerField.GetValue(null)
                        : _uniAircraftPlayerProp?.GetValue(null);

                    isLocalPlayer = playerRef != null
                        ? ReferenceEquals(playerRef, __instance)
                        : true; // Fallback: assume local if UniAircraft.Player is null
                }
                catch
                {
                    isLocalPlayer = true;
                }

                if (!isLocalPlayer) return;

                // Read destruction reason (private field — defaults to GroundSoft)
                byte destructionReason = REASON_GROUND_SOFT;
                if (_destroyedByField != null)
                {
                    try
                    {
                        var reason = _destroyedByField.GetValue(__instance);
                        if (reason != null)
                            destructionReason = (byte)Convert.ToInt32(reason);
                    }
                    catch { /* use default */ }
                }

                Vector3 worldPos = aircraft.transform.position;
                Quaternion worldRot = aircraft.transform.rotation;
                var absolutePos = FloatingOriginHelper.LocalToAbsolute(worldPos);

                var packet = new AircraftDestructionVfxPacket
                {
                    VictimId          = Plugin.Instance.Network.LocalPeerId,
                    PosX              = absolutePos.x,
                    PosY              = absolutePos.y,
                    PosZ              = absolutePos.z,
                    RotX              = worldRot.x,
                    RotY              = worldRot.y,
                    RotZ              = worldRot.z,
                    RotW              = worldRot.w,
                    DestructionReason = destructionReason
                };

                byte[] data = PacketSerializer.SerializeAircraftDestructionVfx(packet);
                Plugin.Instance.Network.SendPacket(PacketType.AircraftDestructionVfx, data, reliable: true);

                Plugin.Log?.LogInfo($"[AircraftDestructionPatches] Sent destruction: reason={destructionReason} " +
                    $"at ({absolutePos.x:F0},{absolutePos.y:F0},{absolutePos.z:F0})");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[AircraftDestructionPatches] DestroyAircraftPostfix error: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle received aircraft destruction packet.
        ///
        /// Finds the remote aircraft clone and calls DestroyAircraft() on it directly so the
        /// game runs its full native crash sequence:
        ///   1. GibAircraft()  — hides normal mesh, shows gibs, physicalizes all debris pieces
        ///                        with AddExplosionForce(5000, radius=25m, ForceMode.Acceleration)
        ///   2. Instantiates the correct explosion ParticleSystem based on destroyedBy:
        ///        Air       → AirExplosionPrefab
        ///        GroundSoft/GroundHard → GroundImpactPrefab
        ///        Water     → WaterImpactPrefab
        ///   3. Optionally SpawnCrater(Large) if near terrain and destroyedBy is Ground
        ///   4. FinalDestroyAircraft() → Destroy(clone.gameObject)
        ///
        /// Echo prevention:
        ///   - DestroyAircraftPostfix skips clones that have RemoteAircraftController.
        ///   - WorldDestructionPatches processing flag is set so any SpawnCrater() call from
        ///     inside DestroyAircraft() does NOT re-broadcast a CraterSpawn packet.
        /// </summary>
        public static void HandleAircraftDestructionVfx(AircraftDestructionVfxPacket packet)
        {
            try
            {
                var absolutePos = new Vector3d(packet.PosX, packet.PosY, packet.PosZ);
                Vector3 localPos = FloatingOriginHelper.AbsoluteToLocal(absolutePos);
                Quaternion rotation = new Quaternion(packet.RotX, packet.RotY, packet.RotZ, packet.RotW);

                Plugin.Log?.LogInfo($"[AircraftDestructionPatches] Received destruction: victim={packet.VictimId} " +
                    $"reason={packet.DestructionReason} at ({localPos.x:F0},{localPos.y:F0},{localPos.z:F0})");

                if (_uniAircraftType == null)
                {
                    Plugin.Log?.LogWarning("[AircraftDestructionPatches] UniAircraft type not cached — cannot find clone");
                    return;
                }

                if (_destroyAircraftMethod == null)
                {
                    Plugin.Log?.LogWarning("[AircraftDestructionPatches] DestroyAircraft method not cached!");
                    return;
                }

                // ── Find the remote aircraft clone ───────────────────────────────────────
                MonoBehaviour clone = null;
                var monoBehaviours = UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None);
                foreach (var mb in monoBehaviours)
                {
                    if (!_uniAircraftType.IsInstanceOfType(mb)) continue;

                    var remoteCtrl = mb.GetComponent<RemoteAircraftController>();
                    if (remoteCtrl == null) continue;
                    if (remoteCtrl.PlayerId != packet.VictimId) continue;

                    clone = mb;
                    break;
                }

                if (clone == null)
                {
                    Plugin.Log?.LogWarning($"[AircraftDestructionPatches] Remote clone not found for victim={packet.VictimId} — cannot run crash sequence");
                    return;
                }

                // ── Snap clone to crash position/rotation ───────────────────────────────
                // DestroyAircraft() reads transform.position for:
                //   - TerrainTools.GetTerrainHeightAtPosition → crater spawn decision
                //   - Instantiate(GroundImpactPrefab, position, ...) → explosion placement
                //   - Debris.SeparateAndPhysicalize → gib spawn origin
                clone.transform.SetPositionAndRotation(localPos, rotation);
                var rb = clone.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.position = localPos;
                    rb.rotation = rotation;
                }

                // ── Set destroyedBy so the correct explosion prefab is used ─────────────
                if (_destroyedByField != null && _destructionReasonType != null)
                {
                    try
                    {
                        object reason = Enum.ToObject(_destructionReasonType, (int)packet.DestructionReason);
                        _destroyedByField.SetValue(clone, reason);
                    }
                    catch (Exception ex)
                    {
                        Plugin.Log?.LogWarning($"[AircraftDestructionPatches] Could not set destroyedBy: {ex.Message}");
                    }
                }

                // ── Call DestroyAircraft() — game handles the full crash sequence ────────
                // Set the world-destruction processing flag so any SpawnCrater() call that
                // fires inside DestroyAircraft() (for ground crashes near terrain) does NOT
                // broadcast a duplicate CraterSpawn packet back.  The crater packet was
                // already sent by SpawnCraterPostfix running on the victim's machine.
                WorldDestructionPatches.SetProcessingFlag(true);
                try
                {
                    _destroyAircraftMethod.Invoke(clone, null);
                    Plugin.Log?.LogInfo($"[AircraftDestructionPatches] Called DestroyAircraft() on remote clone " +
                        $"(victim={packet.VictimId} reason={packet.DestructionReason})");
                }
                finally
                {
                    WorldDestructionPatches.SetProcessingFlag(false);
                }
            }
            catch (Exception ex)
            {
                WorldDestructionPatches.SetProcessingFlag(false);
                Plugin.Log?.LogError($"[AircraftDestructionPatches] HandleAircraftDestructionVfx error: {ex.Message}\n{ex.StackTrace}");
            }
        }
    }
}
