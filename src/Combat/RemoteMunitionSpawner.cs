using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Falcon;
using Falcon.Stores;
using Falcon.Targeting;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Game;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Combat
{
    /// <summary>
    /// Encapsulates shared spawn → configure → Launch() logic for remote missiles and bombs.
    /// Session-scoped: created per GameSession, not static.
    /// </summary>
    public class RemoteMunitionSpawner
    {
        private const string Tag = "MUNITION-SPAWN";
        private static readonly PropertyInfo SeekerIsTrackingProperty =
            typeof(Seeker).GetProperty("IsTracking", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly FloatingOriginService _originService;
        private readonly RemoteAircraftManager _remoteManager;
        private readonly Func<ulong, Target> _targetResolver;

        public RemoteMunitionSpawner(
            FloatingOriginService originService,
            RemoteAircraftManager remoteManager,
            Func<ulong, Target> targetResolver = null)
        {
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));
            _targetResolver = targetResolver;
        }

        /// <summary>
        /// Spawns a missile from a MissileLaunchPacket, configures it, calls Launch(), sets Ownship.
        /// Returns the configured Munition, or null on failure.
        /// </summary>
        public Munition SpawnAndConfigureMissile(MissileLaunchPacket packet, UniAircraft shooterClone)
        {
            try
            {
                var missile = ReleaseLoadedMunition(shooterClone, packet.MissileType)
                    ?? SpawnDetachedMunition(packet.MissileType);
                if (missile == null)
                {
                    Log.Warning(Tag, $"No munition available for '{packet.MissileType}'");
                    return null;
                }

                // Position and orientation from packet
                missile.transform.position = _originService.AbsoluteToLocal(
                    packet.LaunchPosX, packet.LaunchPosY, packet.LaunchPosZ);
                Vector3 launchDir = new Vector3(packet.LaunchDirX, packet.LaunchDirY, packet.LaunchDirZ);
                if (launchDir.sqrMagnitude > 0.001f)
                    missile.transform.forward = launchDir.normalized;

                // Assign target before Launch() so seeker gets it during Launch()'s AssignTarget call
                Target target = null;
                if (packet.TargetId != 0)
                {
                    target = _targetResolver?.Invoke(packet.TargetId);
                    if (target != null) missile.Target = target;
                }

                Vector3 velocity = GetNativeInheritedVelocity(shooterClone, missile.transform.position);

                // CRITICAL: Launch() initializes motors, sets IsLaunched, adds to LaunchedMunitions/Missiles
                missile.Launch(velocity);
                RemoveLoadedStoreAfterLaunch(shooterClone, missile);

                // CRITICAL: Set Ownship AFTER Launch() — Launch() does GetComponentInParent<Target>()
                // which returns null for fallback parentless munitions.
                if (missile.Ownship == null)
                    missile.Ownship = shooterClone?.GetComponentInChildren<Target>();

                // For guided missiles, ensure seeker tracks the assigned target
                if (missile.HasSeeker && missile.Target != null)
                    missile.Seeker.AssignTarget(missile.Target);
                if (missile.HasSeeker && (!packet.IsTracking || target == null))
                    SetSeekerTracking(missile, false);

                Log.Info(Tag, $"Remote missile configured: target={missile.Target?.name}, " +
                              $"seeker.IsTracking={missile.Seeker?.IsTracking}, enabled={missile.enabled}");

                return missile;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed spawning remote missile '{packet.MissileType}': {ex.Message}");
                return null;
            }
        }

        private static void SetSeekerTracking(Munition munition, bool isTracking)
        {
            try
            {
                SeekerIsTrackingProperty?.SetValue(munition.Seeker, isTracking, null);
            }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"Could not set remote seeker tracking={isTracking}: {ex.Message}");
            }
        }

        /// <summary>
        /// Spawns a bomb from a BombDropPacket, configures it, calls Launch(), sets Ownship.
        /// Returns the configured Munition, or null on failure.
        /// </summary>
        public Munition SpawnAndConfigureBomb(BombDropPacket packet, UniAircraft shooterClone)
        {
            try
            {
                var bomb = ReleaseLoadedMunition(shooterClone, packet.BombType)
                    ?? SpawnDetachedMunition(packet.BombType);
                if (bomb == null)
                {
                    Log.Warning(Tag, $"No munition available for '{packet.BombType}'");
                    return null;
                }

                // Position from packet
                bomb.transform.position = _originService.AbsoluteToLocal(
                    packet.LaunchPosX, packet.LaunchPosY, packet.LaunchPosZ);

                // Rotation and velocity from packet velocity vector
                Vector3 velocity = new Vector3(packet.VelX, packet.VelY, packet.VelZ);
                if (velocity.sqrMagnitude > 0.01f)
                    bomb.transform.forward = velocity.normalized;

                // CRITICAL: Launch() initializes physics, sets IsLaunched, adds to LaunchedMunitions
                bomb.Launch(velocity);
                RemoveLoadedStoreAfterLaunch(shooterClone, bomb);

                // CRITICAL: Set Ownship AFTER Launch() — same reason as missiles
                if (bomb.Ownship == null)
                    bomb.Ownship = shooterClone?.GetComponentInChildren<Target>();

                return bomb;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed spawning remote bomb '{packet.BombType}': {ex.Message}");
                return null;
            }
        }

        /// <summary>Legacy helper kept for unit tests/old callers; native launch uses point velocity only.</summary>
        public static Vector3 CalculateLaunchVelocity(
            Vector3 launchDir, Vector3 shooterVelocity, float defaultSpeed = 250f)
        {
            return launchDir.normalized * defaultSpeed + shooterVelocity;
        }

        private static Vector3 GetNativeInheritedVelocity(UniAircraft shooterClone, Vector3 launchPosition)
        {
            var rb = shooterClone?.GetComponent<Rigidbody>();
            return rb != null ? rb.GetPointVelocity(launchPosition) : Vector3.zero;
        }

        private static Munition SpawnDetachedMunition(string storeName)
        {
            if (string.IsNullOrWhiteSpace(storeName))
                return null;

            Store store;
            try
            {
                store = GameDataStores.SpawnStore(storeName);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"SpawnStore('{storeName}') failed: {ex.Message}");
                return null;
            }

            var munition = store as Munition;
            if (munition == null)
            {
                Log.Warning(Tag, $"SpawnStore('{storeName}') did not return a Munition");
                if (store != null) UnityEngine.Object.Destroy(store.gameObject);
                return null;
            }

            munition.transform.SetParent(null);
            munition.transform.localScale = Vector3.one;
            EnableRenderersAndColliders(munition.gameObject);
            return munition;
        }

        private static Munition ReleaseLoadedMunition(UniAircraft shooterClone, string storeName)
        {
            if (shooterClone?.Stores?.StationToLaunchers == null || string.IsNullOrEmpty(storeName))
                return null;

            foreach (var kvp in shooterClone.Stores.StationToLaunchers)
            {
                var launcher = kvp.Value;
                if (launcher == null || launcher.LoadedWeaponCount <= 0)
                    continue;
                if (!NamesMatch(launcher.LoadedWeaponName, storeName))
                    continue;

                var munition = launcher.ReleaseNextWeapon();
                if (munition == null)
                    continue;

                EnableRenderersAndColliders(munition.gameObject);
                Log.Debug(Tag, $"Released native loaded store '{munition.name}' from station {kvp.Key}");
                return munition;
            }

            return null;
        }

        private static void RemoveLoadedStoreAfterLaunch(UniAircraft shooterClone, Munition munition)
        {
            if (shooterClone?.Stores == null || munition == null)
                return;

            try
            {
                shooterClone.Stores.RemoveStoreFromStation(munition);
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed removing launched store '{munition.name}': {ex.Message}");
            }
        }

        /// <summary>Enable all Renderers and Colliders in a munition's hierarchy.</summary>
        private static void EnableRenderersAndColliders(GameObject root)
        {
            foreach (var r in root.GetComponentsInChildren<Renderer>(true))
                r.enabled = true;
            foreach (var c in root.GetComponentsInChildren<Collider>(true))
                c.enabled = true;
        }

        private static bool NamesMatch(string candidate, string expected)
        {
            string a = NormalizeStoreName(candidate);
            string b = NormalizeStoreName(expected);
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
                return false;
            return a == b || a.Contains(b) || b.Contains(a);
        }

        private static string NormalizeStoreName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            int cloneIndex = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (cloneIndex >= 0)
                name = name.Substring(0, cloneIndex);

            char[] buffer = new char[name.Length];
            int length = 0;
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (char.IsLetterOrDigit(ch))
                    buffer[length++] = char.ToUpperInvariant(ch);
            }
            return new string(buffer, 0, length);
        }
    }
}
