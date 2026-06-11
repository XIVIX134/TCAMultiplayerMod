using System;
using System.Collections.Generic;
using UnityEngine;
using Falcon;
using Falcon.Buildings;
using Falcon.Damage;
using Falcon.Targeting;
using Falcon.UniversalAircraft;
using Falcon.Vehicles;
using Falcon.Weapons;
using GameCraterSize = Falcon.World.CraterSize;
using WorldCraters2 = Falcon.World.WorldCraters2;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Combat
{
    /// <summary>
    /// Synchronizes explosions, craters, building destruction, and aircraft destruction VFX
    /// across all connected peers. Dedup by position+time prevents echo loops.
    /// Created per GameSession, disposed with it. No static mutable state.
    /// </summary>
    public class ExplosionSyncSystem : IDisposable
    {
        private const string Tag = "EXPLOSION-SYNC";
        private const float DedupRadius = 5f;
        private const float DedupTimeWindow = 0.5f;
        private const float GroundCheckDistance = 10f;
        private const float WorldObjectSearchRadius = 90f;

        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly GameEventBridge _eventBridge;
        private readonly FloatingOriginService _originService;
        private readonly RemoteAircraftManager _remoteManager;

        private readonly List<(Vector3 pos, float time)> _recentExplosions = new List<(Vector3, float)>();
        private int _remoteWorldDestroyDepth;
        private bool _disposed;

        public ExplosionSyncSystem(
            GameSession session,
            ConnectionManager connection,
            PacketRouter router,
            GameEventBridge eventBridge,
            FloatingOriginService originService,
            RemoteAircraftManager remoteManager)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _eventBridge = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));

            _router.Register(PacketType.ExplosionSync, HandleExplosionSyncRaw);
            _router.Register(PacketType.CraterSpawn, HandleCraterSpawnRaw);
            _router.Register(PacketType.BuildingDestroy, HandleBuildingDestroyRaw);
            _router.Register(PacketType.AircraftDestructionVfx, HandleAircraftDestructionVfxRaw);

            _eventBridge.OnExplosion += HandleLocalExplosion;
            _eventBridge.OnAnythingDestroyed += HandleAnythingDestroyed;
            _eventBridge.OnAircraftDestroyed += HandleLocalAircraftDestroyed;
            Log.Info(Tag, "Initialized");
        }

        // ── Local Explosion Handling ─────────────────────────────────────

        /// <summary>
        /// Called when an explosion occurs locally via GameEventBridge.
        /// Only broadcasts if caused by the local player and not a duplicate.
        /// </summary>
        private void HandleLocalExplosion(ExplosionEventParams p)
        {
            if (_disposed || !_connection.HasSession) return;
            if (p.Damage.SourceTarget == null) return;
            if (IsRemoteTarget(p.Damage.SourceTarget)) return;
            if (IsBulletExplosion(p)) return;

            var localPos = p.Explosion.Position;
            if (IsDuplicate(localPos)) return;
            _recentExplosions.Add((localPos, Time.time));

            _originService.LocalToAbsolute(localPos, out double absX, out double absY, out double absZ);
            byte impactSurface = DetermineImpactSurface(localPos);

            var packet = new ExplosionSyncPacket
            {
                ShooterId = _session.LocalPeerId,
                PosX = absX,
                PosY = absY,
                PosZ = absZ,
                BlastRadius = p.Explosion.Radius,
                ImpactDamage = p.Damage.Damage,
                WeaponName = p.Damage.Weapon ?? "Unknown",
                EffectPath = "",
                ExplosionType = DetermineExplosionType(p.Explosion.Radius),
                ImpactSurface = impactSurface
            };

            var payload = PacketSerializer.SerializeExplosionSync(packet);
            var data = PacketSerializer.Serialize(PacketType.ExplosionSync, payload);
            _connection.BroadcastReliable(data);
            Log.Debug(Tag, $"Broadcast explosion at ({absX:F1}, {absY:F1}, {absZ:F1}) r={p.Explosion.Radius:F1}");

            if (impactSurface == 1)
                SendCraterPacket(absX, absY, absZ, p.Explosion.Radius);
        }

        private void SendCraterPacket(double absX, double absY, double absZ, float blastRadius)
        {
            var packet = new CraterSpawnPacket
            {
                PosX = absX,
                PosY = absY,
                PosZ = absZ,
                CraterSize = DetermineCraterSize(blastRadius)
            };
            var payload = PacketSerializer.SerializeCraterSpawn(packet);
            var data = PacketSerializer.Serialize(PacketType.CraterSpawn, payload);
            _connection.BroadcastReliable(data);
            Log.Debug(Tag, $"Broadcast crater size={packet.CraterSize}");
        }

        private void HandleAnythingDestroyed(DestroyedEvent evt)
        {
            if (_disposed || !_connection.HasSession) return;
            if (_remoteWorldDestroyDepth > 0) return;
            if (evt.Destroyed == null) return;
            if (_remoteManager.IsRemoteCloneDamageable(evt.Destroyed)) return;
            if (evt.Destroyed.GetComponentInParent<UniAircraft>() != null) return;

            var sourceTarget = evt.DamageSource.SourceTarget;
            if (sourceTarget == null) return;
            if (IsRemoteTarget(sourceTarget)) return;

            if (!TryCreateWorldDestroyPacket(evt, out var packet)) return;

            var payload = PacketSerializer.SerializeBuildingDestroy(packet);
            _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.BuildingDestroy, payload));
            Log.Debug(Tag, $"Broadcast world destroy kind={packet.ObjectKind} name='{packet.ObjectName}' " +
                           $"type={packet.TargetType} at ({packet.PosX:F1}, {packet.PosY:F1}, {packet.PosZ:F1})");
        }

        private void HandleLocalAircraftDestroyed(UniAircraft aircraft)
        {
            if (_disposed || !_connection.HasSession || aircraft == null) return;

            var target = aircraft.GetComponentInChildren<Target>();
            if (IsRemoteTarget(target)) return;

            _originService.LocalToAbsolute(aircraft.transform.position,
                out double absX, out double absY, out double absZ);
            Quaternion rot = aircraft.transform.rotation;
            Vector3 vel = aircraft.Rigidbody != null ? aircraft.Rigidbody.velocity : Vector3.zero;
            var packet = new AircraftDestructionVfxPacket
            {
                VictimId = _session.LocalPeerId,
                PosX = absX,
                PosY = absY,
                PosZ = absZ,
                RotX = rot.x,
                RotY = rot.y,
                RotZ = rot.z,
                RotW = rot.w,
                DestructionReason = GetAircraftDestructionReason(aircraft),
                VelX = vel.x,
                VelY = vel.y,
                VelZ = vel.z
            };

            BroadcastDestructionVfxDeferred(aircraft, packet).Forget();
        }

        /// <summary>
        /// OnAircraftDestroyed fires both when the aircraft goes critical
        /// (burning, HP > 0) and when it fully explodes (HP 0 → native
        /// DestroyAircraft with gibs + explosion). EjectPilots() also raises it
        /// before setting ArePilotsEjected. All three can only be told apart
        /// after the current call stack unwinds, so defer one frame and tag the
        /// packet with what actually happened:
        ///   0-3 = fully exploded (native reason), 4 = ejected, 5 = burning.
        /// </summary>
        private async Cysharp.Threading.Tasks.UniTaskVoid BroadcastDestructionVfxDeferred(
            UniAircraft aircraft, AircraftDestructionVfxPacket packet)
        {
            await Cysharp.Threading.Tasks.UniTask.Yield();
            if (_disposed || !_connection.HasSession) return;

            if (aircraft != null && aircraft.ArePilotsEjected)
            {
                packet.DestructionReason = AircraftDestructionVfxPacket.ReasonPilotsEjected;
            }
            else if (aircraft != null && aircraft.Damage != null && !aircraft.Damage.IsDestroyed)
            {
                // Still in the critical burn phase — clones burn and fall.
                // (A destroyed aircraft is gone by now: FinalDestroyAircraft
                // destroys the GameObject, making the reference compare null.)
                packet.DestructionReason = AircraftDestructionVfxPacket.ReasonCriticalBurning;
            }

            var payload = PacketSerializer.SerializeAircraftDestructionVfx(packet);
            _connection.BroadcastReliable(PacketSerializer.Serialize(PacketType.AircraftDestructionVfx, payload));
            Log.Debug(Tag, $"Broadcast aircraft destruction VFX for peer {_session.LocalPeerId} " +
                           $"reason={packet.DestructionReason}");
        }

        // ── Remote Packet Handlers ───────────────────────────────────────

        private void HandleExplosionSyncRaw(ulong fromPeerId, byte[] rawData)
        {
            var (_, payload) = PacketSerializer.Deserialize(rawData);
            if (payload == null) return;

            var packet = PacketSerializer.DeserializeExplosionSync(payload);
            if (_session.IsHost && fromPeerId != _session.LocalPeerId && packet.ShooterId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected ExplosionSync from peer {fromPeerId} for shooter {packet.ShooterId}");
                return;
            }
            var localPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);

            if (IsDuplicate(localPos)) return;
            _recentExplosions.Add((localPos, Time.time));

            SpawnExplosionVfx(localPos, packet.BlastRadius, packet.EffectPath,
                packet.ExplosionType, packet.ImpactSurface);
            Log.Debug(Tag, $"Remote explosion from {fromPeerId} at {localPos} r={packet.BlastRadius:F1}");
        }

        private void HandleCraterSpawnRaw(ulong fromPeerId, byte[] rawData)
        {
            var (_, payload) = PacketSerializer.Deserialize(rawData);
            if (payload == null) return;

            var packet = PacketSerializer.DeserializeCraterSpawn(payload);
            var localPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);
            SpawnCrater(localPos, packet.CraterSize);
            Log.Debug(Tag, $"Remote crater size={packet.CraterSize} from {fromPeerId}");
        }

        private void HandleBuildingDestroyRaw(ulong fromPeerId, byte[] rawData)
        {
            var (_, payload) = PacketSerializer.Deserialize(rawData);
            if (payload == null) return;

            var packet = PacketSerializer.DeserializeBuildingDestroy(payload);
            var localPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);
            DestroyWorldObject(packet, localPos);
            Log.Debug(Tag, $"World object destroyed from {fromPeerId}: kind={packet.ObjectKind} " +
                           $"name='{packet.ObjectName}' id={packet.BuildingInstanceId}");
        }

        private void HandleAircraftDestructionVfxRaw(ulong fromPeerId, byte[] rawData)
        {
            var (_, payload) = PacketSerializer.Deserialize(rawData);
            if (payload == null) return;

            var packet = PacketSerializer.DeserializeAircraftDestructionVfx(payload);
            if (_session.IsHost && fromPeerId != _session.LocalPeerId && packet.VictimId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected AircraftDestructionVfx from peer {fromPeerId} for victim {packet.VictimId}");
                return;
            }
            var localPos = _originService.AbsoluteToLocal(packet.PosX, packet.PosY, packet.PosZ);
            var rotation = new Quaternion(packet.RotX, packet.RotY, packet.RotZ, packet.RotW);
            var velocity = new Vector3(packet.VelX, packet.VelY, packet.VelZ);
            _remoteManager.PlayPeerDeath(packet.VictimId, localPos, rotation, packet.DestructionReason, velocity);
            Log.Debug(Tag, $"Destruction VFX for {packet.VictimId} from {fromPeerId}");
        }

        // ── VFX Spawning ─────────────────────────────────────────────────

        private void SpawnExplosionVfx(Vector3 position, float blastRadius, string effectPath,
            byte explosionType, byte impactSurface)
        {
            string effectName = ResolveExplosionEffect(effectPath, explosionType, blastRadius, impactSurface);
            SpawnEffect(effectName, position, Quaternion.identity, 10f);
        }

        private void SpawnCrater(Vector3 position, byte craterSize)
        {
            try
            {
                if (WorldCraters2.Instance != null)
                {
                    WorldCraters2.Instance.SpawnCrater(position, ToGameCraterSize(craterSize), false);
                    return;
                }
                Log.Warning(Tag, "WorldCraters2.Instance missing; crater not spawned");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to spawn crater: {ex.Message}");
            }
        }

        /// <summary>Spawns a pooled or loaded VFX. autoDestroyTime=0 means permanent.</summary>
        private void SpawnEffect(string effectName, Vector3 position, Quaternion rotation, float autoDestroyTime)
        {
            try
            {
                if (Bullet2Manager.Instance != null)
                {
                    Bullet2Manager.Instance.SpawnEffect(effectName, position, rotation);
                    if (!effectName.StartsWith("Effects/", StringComparison.OrdinalIgnoreCase))
                        return;
                }
                if (TrySpawnResourceEffect(effectName, position, rotation, autoDestroyTime))
                    return;

                Log.Warning(Tag, $"Could not load VFX: {effectName}");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to spawn VFX '{effectName}': {ex.Message}");
            }
        }

        private static bool TrySpawnResourceEffect(string effectName, Vector3 position, Quaternion rotation, float autoDestroyTime)
        {
            var psPrefab = GameData.GetResource<ParticleSystem>(effectName);
            if (psPrefab != null)
            {
                var ps = UnityEngine.Object.Instantiate(psPrefab, position, rotation);
                ps.Play();
                if (autoDestroyTime > 0f)
                    UnityEngine.Object.Destroy(ps.gameObject, autoDestroyTime);
                return true;
            }

            var prefab = GameData.GetResource(effectName);
            if (prefab != null)
            {
                var go = UnityEngine.Object.Instantiate(prefab, position, rotation);
                go.SetActive(true);
                if (autoDestroyTime > 0f)
                    UnityEngine.Object.Destroy(go, autoDestroyTime);
                return true;
            }

            return false;
        }

        private static string ResolveExplosionEffect(string effectPath, byte explosionType, float blastRadius, byte impactSurface)
        {
            if (!string.IsNullOrEmpty(effectPath)) return effectPath;
            bool large = explosionType >= 1 || blastRadius >= 30f;
            switch (impactSurface)
            {
                case 1:
                    return large ? "Effects/Explosion/ExplosionGroundLarge" : "Effects/Explosion/ExplosionGroundMedium";
                case 2:
                    return large ? "Effects/Explosion/ExplosionWaterLarge" : "Effects/Explosion/ExplosionWaterMedium";
                default:
                    return large ? "Effects/Explosion/ExplosionAirLarge" : "Effects/Explosion/ExplosionAirMedium";
            }
        }

        private static byte DetermineAircraftDestructionReason(Vector3 position)
        {
            byte surface = DetermineImpactSurface(position);
            if (surface == 2) return 3;
            if (surface == 1) return 1;
            return 0;
        }

        private static byte GetAircraftDestructionReason(UniAircraft aircraft)
        {
            if (aircraft == null)
                return 0;

            try
            {
                return (byte)aircraft.DestroyedBy;
            }
            catch
            {
                return DetermineAircraftDestructionReason(aircraft.transform.position);
            }
        }

        private bool TryCreateWorldDestroyPacket(DestroyedEvent evt, out BuildingDestroyPacket packet)
        {
            packet = default;
            var destroyed = evt.Destroyed;
            if (destroyed == null) return false;

            var building = destroyed.GetComponentInParent<Building>();
            var vehicle = destroyed.GetComponentInParent<JVehicle2>();
            var target = destroyed.Target ?? destroyed.GetComponentInParent<Target>();

            if (building == null && vehicle == null && target == null) return false;

            Vector3 localPos = target != null ? target.transform.position : destroyed.transform.position;
            _originService.LocalToAbsolute(localPos, out double absX, out double absY, out double absZ);

            packet = new BuildingDestroyPacket
            {
                BuildingInstanceId = destroyed.gameObject.GetInstanceID(),
                PosX = absX,
                PosY = absY,
                PosZ = absZ,
                ObjectName = GetWorldObjectName(building, vehicle, target, destroyed),
                TargetType = target != null ? (byte)target.TargetType : (byte)255,
                ObjectKind = building != null ? (byte)1 : vehicle != null ? (byte)2 : (byte)3
            };
            return true;
        }

        private void DestroyWorldObject(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            try
            {
                _remoteWorldDestroyDepth++;

                if (packet.ObjectKind == 1 || packet.TargetType == (byte)TargetType.Structure)
                {
                    if (TryDestroyBuilding(packet, expectedPosition)) return;
                }

                if (packet.ObjectKind == 2 || IsVehicleTargetType(packet.TargetType))
                {
                    if (TryDestroyVehicle(packet, expectedPosition)) return;
                }

                if (TryDestroyDamageable(packet, expectedPosition)) return;

                Log.Debug(Tag, $"World object not found near {expectedPosition} " +
                               $"kind={packet.ObjectKind} name='{packet.ObjectName}'");
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to destroy world object: {ex.Message}");
            }
            finally
            {
                _remoteWorldDestroyDepth--;
            }
        }

        private bool TryDestroyBuilding(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            var building = FindBestBuilding(packet, expectedPosition);
            if (building == null) return false;

            var damageable = building.GetComponentInChildren<Damageable>(true);
            if (damageable != null && !damageable.IsDestroyed)
                damageable.InstantlyDestroy();
            else if (!building.IsDestroyed)
                building.DestroyBuilding();

            Log.Debug(Tag, $"Destroyed building '{building.name}' via world sync");
            return true;
        }

        private bool TryDestroyVehicle(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            var vehicle = FindBestVehicle(packet, expectedPosition);
            if (vehicle == null) return false;

            if (vehicle.Damage != null && !vehicle.Damage.IsDestroyed)
                vehicle.Damage.InstantlyDestroy();
            else if (vehicle.Target != null && !vehicle.Target.IsDestroyed)
                vehicle.DestroyVehicle();

            Log.Debug(Tag, $"Destroyed vehicle '{vehicle.name}' via world sync");
            return true;
        }

        private bool TryDestroyDamageable(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            var damageable = FindBestDamageable(packet, expectedPosition);
            if (damageable == null) return false;

            damageable.InstantlyDestroy();
            Log.Debug(Tag, $"Destroyed damageable '{damageable.name}' via world sync");
            return true;
        }

        private static Building FindBestBuilding(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            var buildings = UnityEngine.Object.FindObjectsByType<Building>(FindObjectsSortMode.None);
            return FindNearest(buildings, expectedPosition, packet.ObjectName,
                b => b != null && !b.IsDestroyed, b => b.transform.position, b => b.name);
        }

        private static JVehicle2 FindBestVehicle(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            var vehicles = UnityEngine.Object.FindObjectsByType<JVehicle2>(FindObjectsSortMode.None);
            return FindNearest(vehicles, expectedPosition, packet.ObjectName,
                v => v != null && v.Target != null && !v.Target.IsDestroyed,
                v => v.transform.position,
                v => v.name);
        }

        private Damageable FindBestDamageable(BuildingDestroyPacket packet, Vector3 expectedPosition)
        {
            Damageable best = null;
            float bestScore = float.MaxValue;

            var colliders = Physics.OverlapSphere(expectedPosition, WorldObjectSearchRadius);
            foreach (var col in colliders)
            {
                var damageable = col.GetComponentInParent<Damageable>();
                if (damageable == null || damageable.IsDestroyed) continue;
                if (_remoteManager.IsRemoteCloneDamageable(damageable)) continue;
                if (damageable.GetComponentInParent<UniAircraft>() != null) continue;
                if (!TargetTypeMatches(packet.TargetType, damageable.Target)) continue;

                float score = ScoreCandidate(damageable.transform.position, expectedPosition, damageable.name, packet.ObjectName);
                if (score >= bestScore) continue;
                bestScore = score;
                best = damageable;
            }

            return best;
        }

        private static T FindNearest<T>(
            IEnumerable<T> candidates,
            Vector3 expectedPosition,
            string expectedName,
            Func<T, bool> predicate,
            Func<T, Vector3> positionSelector,
            Func<T, string> nameSelector)
        {
            T best = default;
            float bestScore = float.MaxValue;
            foreach (var candidate in candidates)
            {
                if (!predicate(candidate)) continue;
                float score = ScoreCandidate(positionSelector(candidate), expectedPosition, nameSelector(candidate), expectedName);
                if (score >= bestScore) continue;
                bestScore = score;
                best = candidate;
            }
            return bestScore <= WorldObjectSearchRadius * WorldObjectSearchRadius + 10000f ? best : default;
        }

        private static float ScoreCandidate(Vector3 candidatePosition, Vector3 expectedPosition, string candidateName, string expectedName)
        {
            float score = (candidatePosition - expectedPosition).sqrMagnitude;
            if (!string.IsNullOrEmpty(expectedName) && !NamesMatch(candidateName, expectedName))
                score += 10000f;
            return score;
        }

        // ── Dedup Logic ──────────────────────────────────────────────────

        private bool IsDuplicate(Vector3 position)
        {
            float now = Time.time;
            _recentExplosions.RemoveAll(e => (now - e.time) > DedupTimeWindow);

            for (int i = 0; i < _recentExplosions.Count; i++)
            {
                if (Vector3.Distance(_recentExplosions[i].pos, position) < DedupRadius)
                    return true;
            }
            return false;
        }

        // ── Classification Helpers ───────────────────────────────────────

        /// <summary>Checks if a Target belongs to a remote peer's aircraft.</summary>
        private bool IsRemoteTarget(Target target)
        {
            if (target == null) return false;
            var ownerAircraft = target.GetComponentInParent<UniAircraft>();
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var peerAircraft = _remoteManager.GetAircraft(peerId);
                if (peerAircraft == null) continue;
                if (ownerAircraft == peerAircraft)
                    return true;
            }
            return false;
        }

        private static byte DetermineImpactSurface(Vector3 position)
        {
            if (Physics.Raycast(position, Vector3.down, out RaycastHit hit, GroundCheckDistance))
            {
                if (hit.collider.gameObject.layer == LayerMask.NameToLayer("Water"))
                    return 2; // water
                return 1; // ground/terrain
            }
            if (Physics.Raycast(position + Vector3.down * 2f, Vector3.up, GroundCheckDistance))
                return 1; // ground/terrain
            return 0; // air
        }

        private static byte DetermineExplosionType(float blastRadius)
        {
            if (blastRadius >= 100f) return 2;
            if (blastRadius >= 30f) return 1;
            return 0;
        }

        private static bool IsBulletExplosion(ExplosionEventParams p)
        {
            // Bullet2 already runs native tracer/impact/explosion behavior locally.
            // Broadcasting those tiny Explosion.Trigger events makes normal gunfire
            // appear as extra network explosion spam.
            return p.Explosion.Radius <= 10f
                && p.Damage.Damage <= 50;
        }

        private static byte DetermineCraterSize(float blastRadius)
        {
            if (blastRadius >= 100f) return 4;
            if (blastRadius >= 50f) return 3;
            if (blastRadius >= 25f) return 2;
            if (blastRadius >= 10f) return 1;
            return 0;
        }

        private static GameCraterSize ToGameCraterSize(byte craterSize)
        {
            if (craterSize > 4) craterSize = 0;
            return (GameCraterSize)craterSize;
        }

        private static string GetWorldObjectName(Building building, JVehicle2 vehicle, Target target, Damageable damageable)
        {
            if (building != null) return building.name ?? "";
            if (vehicle != null) return vehicle.name ?? "";
            if (target != null) return target.name ?? "";
            return damageable != null ? damageable.name ?? "" : "";
        }

        private static bool IsVehicleTargetType(byte targetType)
        {
            return targetType == (byte)TargetType.AAA
                || targetType == (byte)TargetType.SAM
                || targetType == (byte)TargetType.Radar
                || targetType == (byte)TargetType.Armor
                || targetType == (byte)TargetType.Vehicle
                || targetType == (byte)TargetType.Ship;
        }

        private static bool TargetTypeMatches(byte expectedType, Target target)
        {
            if (expectedType == 255 || target == null) return true;
            return (byte)target.TargetType == expectedType;
        }

        private static bool NamesMatch(string candidate, string expected)
        {
            if (string.IsNullOrEmpty(candidate) || string.IsNullOrEmpty(expected)) return false;
            return string.Equals(NormalizeName(candidate), NormalizeName(expected), StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeName(string name)
        {
            return (name ?? "").Replace("(Clone)", "").Trim();
        }

        // ── Cleanup ──────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _eventBridge.OnExplosion -= HandleLocalExplosion;
            _eventBridge.OnAnythingDestroyed -= HandleAnythingDestroyed;
            _eventBridge.OnAircraftDestroyed -= HandleLocalAircraftDestroyed;
            _router.Unregister(PacketType.ExplosionSync, HandleExplosionSyncRaw);
            _router.Unregister(PacketType.CraterSpawn, HandleCraterSpawnRaw);
            _router.Unregister(PacketType.BuildingDestroy, HandleBuildingDestroyRaw);
            _router.Unregister(PacketType.AircraftDestructionVfx, HandleAircraftDestructionVfxRaw);
            _recentExplosions.Clear();

            Log.Info(Tag, "Disposed");
        }
    }
}
