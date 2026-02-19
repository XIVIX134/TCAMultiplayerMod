using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Player
{
    /// <summary>
    /// Manages aircraft-to-aircraft collision detection for multiplayer.
    /// Uses host-authority model: only the host detects collisions and broadcasts results.
    /// 
    /// Remote aircraft use kinematic rigidbodies which don't trigger OnCollisionEnter,
    /// so we use manual distance-based collision detection.
    /// </summary>
    public class AircraftCollisionManager : MonoBehaviour
    {
        // Singleton instance
        public static AircraftCollisionManager Instance { get; private set; }

        // Collision configuration
        public const float COLLISION_RADIUS = 8f;              // Meters - approximate aircraft fuselage radius
        public const float MIN_IMPULSE_FOR_DAMAGE = 5000f;     // Minimum impulse to cause damage
        public const float DAMAGE_MULTIPLIER = 0.00005f;       // Damage = impulse * multiplier * speed
        public const float COLLISION_COOLDOWN = 0.5f;          // Seconds between collision checks for same pair
        public const float MIN_COLLISION_SPEED = 10f;          // Minimum relative speed to register collision

        // Collision tracking to prevent multiple hits
        private readonly Dictionary<(ulong, ulong), float> _lastCollisionTime = new Dictionary<(ulong, ulong), float>();

        // Cached reflection for damage application
        private static Type _damageableType;
        private static Type _damageSourceType;
        private static Type _targetType;
        private static MethodInfo _applyDamageMethod;
        private static ConstructorInfo _damageSourceConstructor;
        private static bool _reflectionInitialized = false;

        // Local player tracking
        private GameObject _localAircraft;
        private Vector3 _localVelocity;
        private ulong _localPlayerId;

        // Remote aircraft manager reference
        private RemoteAircraftManager _remoteAircraftManager;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);

            InitializeReflection();
        }

        private void InitializeReflection()
        {
            if (_reflectionInitialized) return;

            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

                // Damageable type
                _damageableType = ReflectionHelper.GetGameType("Falcon.Damage.Damageable");
                if (_damageableType != null)
                {
                    _applyDamageMethod = _damageableType.GetMethod("ApplyDamageFromImpact", flags);
                }

                // DamageSource type
                _damageSourceType = ReflectionHelper.GetGameType("Falcon.Damage.DamageSource");
                if (_damageSourceType != null)
                {
                    _damageSourceConstructor = _damageSourceType.GetConstructor(new Type[] {
                        typeof(int), typeof(int), typeof(int), typeof(int),
                        ReflectionHelper.GetGameType("Falcon.Targeting.Target"),
                        typeof(Collider), typeof(Vector3), typeof(bool), typeof(string)
                    });
                }

                // Target type
                _targetType = ReflectionHelper.GetGameType("Falcon.Targeting.Target");

                _reflectionInitialized = true;
                Plugin.Log?.LogInfo("[AircraftCollisionManager] Reflection initialized");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AircraftCollisionManager] Reflection init failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Set the remote aircraft manager reference
        /// </summary>
        public void SetRemoteAircraftManager(RemoteAircraftManager manager)
        {
            _remoteAircraftManager = manager;
        }

        /// <summary>
        /// Update local player info (called from FlightGamePatches)
        /// </summary>
        public void UpdateLocalPlayerInfo(GameObject aircraft, Vector3 velocity, ulong playerId)
        {
            _localAircraft = aircraft;
            _localVelocity = velocity;
            _localPlayerId = playerId;
        }

        private void FixedUpdate()
        {
            // Only host runs collision detection
            if (!GameStateMachine.Instance?.IsHost ?? true) return;

            // Check if aircraft collisions are enabled
            if (!LobbyManager.Instance?.AircraftCollisionsEnabled ?? false) return;

            // Need local aircraft and remote manager
            if (_localAircraft == null || _remoteAircraftManager == null) return;

            // Check if local aircraft is still valid
            if (!_localAircraft.activeInHierarchy) return;

            try
            {
                CheckCollisions();
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AircraftCollisionManager] Collision check error: {ex.Message}");
            }
        }

        private void CheckCollisions()
        {
            var remotePlayers = _remoteAircraftManager.GetAllRemotePlayers();
            if (remotePlayers == null) return;

            // Check local vs all remote players
            foreach (var remoteState in remotePlayers)
            {
                if (remoteState?.Aircraft == null || !remoteState.Aircraft.activeInHierarchy) continue;
                if (remoteState.NeedsRespawn) continue;

                // Check collision between local and this remote player
                CheckCollisionPair(
                    _localPlayerId,
                    _localAircraft.transform.position,
                    _localVelocity,
                    remoteState.PeerId,
                    remoteState.DisplayPosition,
                    remoteState.LastVelocity,
                    remoteState.Aircraft
                );
            }

            // Check remote vs remote collisions (for N-player support)
            CheckRemoteRemoteCollisions(remotePlayers);
        }

        private void CheckRemoteRemoteCollisions(IEnumerable<RemoteAircraftState> remotePlayers)
        {
            // Convert to list for indexing
            var remoteList = new List<RemoteAircraftState>();
            foreach (var rp in remotePlayers)
            {
                if (rp?.Aircraft != null && rp.Aircraft.activeInHierarchy && !rp.NeedsRespawn)
                {
                    remoteList.Add(rp);
                }
            }

            // Check all pairs (O(n²) but n is small - usually 1-3 remote players)
            for (int i = 0; i < remoteList.Count; i++)
            {
                for (int j = i + 1; j < remoteList.Count; j++)
                {
                    var playerA = remoteList[i];
                    var playerB = remoteList[j];

                    CheckCollisionPair(
                        playerA.PeerId,
                        playerA.DisplayPosition,
                        playerA.LastVelocity,
                        playerB.PeerId,
                        playerB.DisplayPosition,
                        playerB.LastVelocity,
                        playerB.Aircraft
                    );
                }
            }
        }

        private void CheckCollisionPair(
            ulong playerAId,
            Vector3 posA,
            Vector3 velA,
            ulong playerBId,
            Vector3 posB,
            Vector3 velB,
            GameObject aircraftB)
        {
            // Check cooldown
            var pairKey = playerAId < playerBId ? (playerAId, playerBId) : (playerBId, playerAId);
            if (_lastCollisionTime.TryGetValue(pairKey, out float lastTime))
            {
                if (Time.time - lastTime < COLLISION_COOLDOWN) return;
            }

            // Calculate distance
            float distance = Vector3.Distance(posA, posB);
            if (distance > COLLISION_RADIUS) return;

            // Calculate relative velocity
            Vector3 relativeVelocity = velA - velB;
            float relativeSpeed = relativeVelocity.magnitude;

            // Minimum speed check (ignore very slow collisions)
            if (relativeSpeed < MIN_COLLISION_SPEED) return;

            // Calculate collision normal (direction from A to B)
            Vector3 normal = (posB - posA).normalized;
            if (normal == Vector3.zero) normal = Vector3.up;

            // Calculate relative velocity along collision normal
            float closingSpeed = Vector3.Dot(relativeVelocity, normal);
            if (closingSpeed <= 0) return; // Moving apart

            // Calculate impulse (simplified - using mass of 10000 for typical aircraft)
            float combinedMass = 20000f; // Two aircraft
            float impulse = closingSpeed * combinedMass;

            // Minimum impulse check
            if (impulse < MIN_IMPULSE_FOR_DAMAGE) return;

            // Calculate damage using game's formula
            // damage = impulse * 0.00005 * speed
            int damageA = Mathf.CeilToInt(impulse * DAMAGE_MULTIPLIER * relativeSpeed);
            int damageB = damageA; // Symmetric damage for now

            // Update cooldown
            _lastCollisionTime[pairKey] = Time.time;

            // Collision position (midpoint)
            Vector3 collisionPos = (posA + posB) * 0.5f;

            Plugin.Log?.LogInfo($"[AircraftCollisionManager] Collision detected! " +
                $"Players {playerAId} vs {playerBId}, " +
                $"Distance: {distance:F1}m, " +
                $"Speed: {relativeSpeed:F1}m/s, " +
                $"Impulse: {impulse:F0}, " +
                $"Damage: {damageA}");

            // Apply damage locally (host's aircraft)
            ApplyCollisionDamage(_localAircraft, damageA, aircraftB.transform, collisionPos, normal);

            // Broadcast collision packet to all clients
            BroadcastCollision(playerAId, playerBId, collisionPos, normal, damageA, damageB, relativeSpeed);
        }

        private void ApplyCollisionDamage(GameObject targetAircraft, int damage, Transform sourceTransform, Vector3 contactPoint, Vector3 normal)
        {
            if (_damageableType == null || _applyDamageMethod == null)
            {
                Plugin.Log?.LogWarning("[AircraftCollisionManager] Cannot apply damage - reflection not initialized");
                return;
            }

            try
            {
                var damageable = targetAircraft.GetComponentInChildren(_damageableType);
                if (damageable == null)
                {
                    Plugin.Log?.LogWarning("[AircraftCollisionManager] No Damageable component found on target");
                    return;
                }

                // Get the Target component from source (for damage source tracking)
                object sourceTarget = null;
                if (_targetType != null && sourceTransform != null)
                {
                    sourceTarget = sourceTransform.GetComponentInChildren(_targetType);
                }

                // Create DamageSource
                object damageSource = null;
                if (_damageSourceConstructor != null)
                {
                    damageSource = _damageSourceConstructor.Invoke(new object[] {
                        damage,             // damage amount
                        int.MaxValue,       // penetration (infinite)
                        0,                  // critical hit chance
                        0,                  // max critical hits
                        sourceTarget,       // source target
                        null,               // hit collider
                        contactPoint,       // hit position
                        false,              // not weapon damage
                        "Collision"         // weapon name
                    });
                }

                if (damageSource != null)
                {
                    _applyDamageMethod?.Invoke(damageable, new object[] { damageSource });
                    Plugin.Log?.LogInfo($"[AircraftCollisionManager] Applied {damage} collision damage");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AircraftCollisionManager] Failed to apply damage: {ex.Message}");
            }
        }

        private void BroadcastCollision(ulong playerA, ulong playerB, Vector3 position, Vector3 normal, int damageA, int damageB, float relativeSpeed)
        {
            try
            {
                var packet = new AircraftCollisionPacket
                {
                    PlayerA = playerA,
                    PlayerB = playerB,
                    PosX = position.x,
                    PosY = position.y,
                    PosZ = position.z,
                    NormalX = normal.x,
                    NormalY = normal.y,
                    NormalZ = normal.z,
                    DamageA = damageA,
                    DamageB = damageB,
                    RelativeSpeed = relativeSpeed
                };

                byte[] payload = PacketSerializer.SerializeAircraftCollision(packet);
                Plugin.Instance?.Network?.SendPacket(PacketType.AircraftCollision, payload, reliable: true);

                Plugin.Log?.LogInfo($"[AircraftCollisionManager] Broadcast collision packet");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AircraftCollisionManager] Failed to broadcast collision: {ex.Message}");
            }
        }

        /// <summary>
        /// Handle received collision packet (called by NetworkManager on clients)
        /// </summary>
        public void HandleCollisionPacket(AircraftCollisionPacket packet)
        {
            // Don't process if we're the host (host already applied damage locally)
            if (GameStateMachine.Instance?.IsHost ?? false) return;

            ulong localId = Plugin.Instance?.Network?.GetLocalPlayerId() ?? 0;
            if (localId == 0) return;

            // Determine if this collision involves us
            bool isPlayerA = packet.PlayerA == localId;
            bool isPlayerB = packet.PlayerB == localId;

            if (!isPlayerA && !isPlayerB) return; // Not our collision

            int ourDamage = isPlayerA ? packet.DamageA : packet.DamageB;
            Vector3 collisionPos = new Vector3((float)packet.PosX, (float)packet.PosY, (float)packet.PosZ);
            Vector3 normal = new Vector3(packet.NormalX, packet.NormalY, packet.NormalZ);

            // Find our local aircraft
            if (_localAircraft == null || !_localAircraft.activeInHierarchy)
            {
                Plugin.Log?.LogWarning("[AircraftCollisionManager] Received collision but no local aircraft");
                return;
            }

            // Find the other player's aircraft for damage source
            ulong otherPlayerId = isPlayerA ? packet.PlayerB : packet.PlayerA;
            var otherState = _remoteAircraftManager?.GetRemotePlayer(otherPlayerId);
            Transform otherTransform = otherState?.Aircraft?.transform;

            Plugin.Log?.LogInfo($"[AircraftCollisionManager] Applying received collision damage: {ourDamage}");

            // Apply damage to our aircraft
            ApplyCollisionDamage(_localAircraft, ourDamage, otherTransform, collisionPos, normal);

            // Spawn collision effects
            SpawnCollisionEffects(collisionPos, normal, packet.RelativeSpeed);
        }

        private void SpawnCollisionEffects(Vector3 position, Vector3 normal, float relativeSpeed)
        {
            try
            {
                // Load impact effect prefab from game resources
                var impactPrefab = Resources.Load<ParticleSystem>("Effects/Collision/CollisionImpactHard");
                if (impactPrefab != null)
                {
                    var effect = UnityEngine.Object.Instantiate(impactPrefab);
                    effect.transform.position = position;
                    effect.transform.up = normal;
                    effect.Play();
                    UnityEngine.Object.Destroy(effect.gameObject, 5f);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[AircraftCollisionManager] Failed to spawn effects: {ex.Message}");
            }
        }

        /// <summary>
        /// Clear collision tracking (call on respawn/scene change)
        /// </summary>
        public void ClearCollisionTracking()
        {
            _lastCollisionTime.Clear();
            _localAircraft = null;
        }

        private void OnDestroy()
        {
            if (Instance == this)
            {
                Instance = null;
            }
        }

#if DEBUG
        /// <summary>
        /// Draw debug gizmos for collision visualization (only in DEBUG builds)
        /// </summary>
        private void OnDrawGizmos()
        {
            if (!GameStateMachine.Instance?.IsHost ?? true) return;

            // Draw local aircraft collision sphere
            if (_localAircraft != null && _localAircraft.activeInHierarchy)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawWireSphere(_localAircraft.transform.position, COLLISION_RADIUS);
            }

            // Draw remote aircraft collision spheres
            if (_remoteAircraftManager != null)
            {
                var remotePlayers = _remoteAircraftManager.GetAllRemotePlayers();
                foreach (var remote in remotePlayers)
                {
                    if (remote?.Aircraft != null && remote.Aircraft.activeInHierarchy && !remote.NeedsRespawn)
                    {
                        Gizmos.color = Color.cyan;
                        Gizmos.DrawWireSphere(remote.DisplayPosition, COLLISION_RADIUS);
                    }
                }
            }
        }
#endif
    }
}
