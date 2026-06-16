using System;
using System.Collections.Generic;
using UnityEngine;
using Falcon;
using Falcon.UniversalAircraft;
using Falcon.Factions;
using Falcon.Utilities;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Manages LOCAL player aircraft spawning using direct game APIs.
    /// For remote aircraft, see <see cref="AircraftSpawner"/>.
    /// NO REFLECTION — uses GameDataAircraft.SpawnAircraft() directly.
    /// </summary>
    public class SpawnManager : IDisposable
    {
        private const string Tag = "SPAWN-MGR";
        private const string DefaultAircraftType = "F-16C";

        private static readonly Vector3 AirSpawnDefault = new Vector3(0f, 1000f, 0f);
        private static readonly Quaternion AirSpawnRotation = Quaternion.Euler(0f, 0f, 0f);

        private readonly GameSession _session;
        private readonly AircraftSpawner _spawner;
        private readonly FloatingOriginService _originService;
        private readonly GameEventBridge _eventBridge;
        private JFaction _localFaction; // lazy-initialized in EnsureLocalFaction()

        private UniAircraft _localAircraft;
        private UniAircraft _deadHusk; // previous life's airframe (lingers after ejection)
        private bool _disposed;

        /// <summary>Fired after the local player's aircraft is spawned and configured.</summary>
        public event Action<UniAircraft> OnPlayerSpawned;

        /// <summary>Fired when the local player's aircraft is destroyed.</summary>
        public event Action OnPlayerDied;

        /// <summary>The local player's currently active aircraft, or null.</summary>
        public UniAircraft LocalAircraft => _localAircraft;

        public SpawnManager(
            GameSession session,
            AircraftSpawner spawner,
            FloatingOriginService originService,
            GameEventBridge eventBridge)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _spawner = spawner ?? throw new ArgumentNullException(nameof(spawner));
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            _eventBridge = eventBridge ?? throw new ArgumentNullException(nameof(eventBridge));
            Log.Info(Tag, "Initialized");
        }

        private void EnsureLocalFaction()
        {
            if (_localFaction != null) return;
            try
            {
                var mapName = Falcon.Game2.GameLogic.Instance?.LoadedMap?.MapName;
                if (!string.IsNullOrEmpty(mapName))
                {
                    var mapData = GameDataMaps.GetByName(mapName);
                    if (mapData != null)
                    {
                        _localFaction = mapData.GetPrimaryBlueFaction();
                        if (_localFaction != null)
                            Log.Info(Tag, $"Using map faction: {_localFaction.DisplayName} ({_localFaction.Name})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Map faction lookup failed: {ex.Message}");
            }
            if (_localFaction == null)
            {
                string[] fallbackNames = { "USMC", "US", "USA", "NATO", "Blue", "Player", "Ally" };
                foreach (var name in fallbackNames)
                {
                    _localFaction = GameDataFactions.GetByName(name);
                    if (_localFaction != null) { Log.Info(Tag, $"Using fallback faction: {name}"); break; }
                }
            }
            if (_localFaction == null)
                Log.Error(Tag, "No faction found — spawn will fail!");
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Spawn the local player's aircraft using session settings.
        /// Reads aircraft type, loadout, and airfield from <see cref="GameSession.GetLocalPlayer"/>.
        /// </summary>
        /// <returns>Spawned aircraft, or null on failure.</returns>
        public UniAircraft SpawnLocalPlayer()
        {
            ThrowIfDisposed();
            EnsureLocalFaction();

            // Clean up any existing local aircraft first
            DespawnLocalPlayer();
            DestroyDeadHusk();

            var player = _session.GetLocalPlayer();
            if (player == null)
            {
                Log.Error(Tag, "Cannot spawn — local player not registered in session");
                return null;
            }

            // Get aircraft from player selection, ModConfig, or first available from game data
            string aircraftType = player.SelectedAircraft;
            if (string.IsNullOrEmpty(aircraftType))
                aircraftType = ModConfig.LastAircraft?.Value;
            if (string.IsNullOrEmpty(aircraftType))
            {
                var allAircraft = LoadoutHelper.GetAircraftNames();
                aircraftType = allAircraft.Count > 0 ? allAircraft[0] : "F16C";
                Log.Warning(Tag, $"No aircraft selected — using first available: {aircraftType}");
            }
            string resolvedAircraft = LoadoutHelper.ResolveAvailableAircraft(aircraftType, DefaultAircraftType);
            if (!string.Equals(aircraftType, resolvedAircraft, StringComparison.Ordinal))
            {
                Log.Warning(Tag, $"Selected aircraft '{aircraftType}' unavailable, using '{resolvedAircraft}'");
                aircraftType = resolvedAircraft;
                player.SelectedAircraft = resolvedAircraft;
            }
            string loadoutName = LoadoutHelper.ResolveLoadoutForAircraft(aircraftType, player.SelectedLoadout);
            if (!string.Equals(player.SelectedLoadout, loadoutName, StringComparison.Ordinal))
            {
                Log.Warning(Tag, $"Selected loadout '{player.SelectedLoadout}' unavailable for '{aircraftType}', using '{loadoutName}'");
                player.SelectedLoadout = loadoutName;
            }
            Log.Info(Tag, $"Spawning aircraft: {aircraftType}, airfield: {player.SelectedAirfield}, spawn: {_session.SpawnType}");

            // Resolve spawn position from airfield or session spawn type
            bool isAirStart = _session.SpawnType == LobbySpawnType.InAir;
            Vector3 position;
            Quaternion rotation;
            int spawnSlot = GetSpawnSlot(player.PeerId, player.SelectedAirfield);
            int spawnCount = GetSpawnSlotCount(player.SelectedAirfield);
            if (!TryGetSpawnPosition(player.SelectedAirfield, isAirStart, spawnSlot, spawnCount, out position, out rotation))
            {
                Log.Error(Tag, $"Cannot spawn on ground: no native spawn point for airfield '{player.SelectedAirfield}'");
                return null;
            }

            if (isAirStart)
                ApplyMultiplayerSpawnOffset(player.PeerId, spawnSlot, ref position, rotation);

            // Spawn via direct game API
            UniAircraft aircraft = TrySpawn(aircraftType, position, rotation, isAirStart);

            // Fallback to default type if requested type unavailable
            if (aircraft == null && aircraftType != DefaultAircraftType)
            {
                Log.Warning(Tag, $"Aircraft type '{aircraftType}' unavailable, falling back to {DefaultAircraftType}");
                aircraft = TrySpawn(DefaultAircraftType, position, rotation, isAirStart);
            }

            if (aircraft == null)
            {
                Log.Error(Tag, "Failed to spawn local player aircraft");
                return null;
            }

            // Apply loadout post-spawn
            ApplyLoadout(aircraft, loadoutName);

            // Subscribe to destruction event for death detection
            aircraft.OnAircraftDestroyed += HandleAircraftDestroyed;
            _eventBridge.SubscribeToAircraft(aircraft);

            _localAircraft = aircraft;
            uint lifeId = player.LifeId;
            if (lifeId == 0 || player.IsAwaitingRespawn)
                lifeId = _session.BeginPlayerLife(player.PeerId);

            Log.Info(Tag, $"Spawned local player: {aircraftType} at {position} life={lifeId}");
            OnPlayerSpawned?.Invoke(aircraft);

            return aircraft;
        }

        /// <summary>
        /// Remove the local plane and any leftover dead husk when the session
        /// returns to the lobby (unlike SpawnLocalPlayer, no new spawn follows).
        /// </summary>
        public void CleanupForLobbyReturn()
        {
            DespawnLocalPlayer();
            DestroyDeadHusk();
        }

        /// <summary>
        /// Destroy the local player's current aircraft.
        /// Cleans up subscriptions and nulls the reference.
        /// </summary>
        public void DespawnLocalPlayer()
        {
            if (_localAircraft == null) return;

            // Unsubscribe before destroying
            _localAircraft.OnAircraftDestroyed -= HandleAircraftDestroyed;
            _eventBridge.UnsubscribeFromAircraft(_localAircraft);

            if (_localAircraft.gameObject != null)
            {
                UnityEngine.Object.Destroy(_localAircraft.gameObject);
            }

            _localAircraft = null;
            Log.Info(Tag, "Despawned local player aircraft");
        }

        /// <summary>
        /// Destroy the previous life's airframe. After an ejection the pilotless
        /// plane keeps flying (the mod only nulls its reference on death), so
        /// without this the old husk survives into the next life. Remote peers
        /// already despawn our clone on death — this aligns the local view.
        /// Explosion wrecks that finished their native death sequence are already
        /// destroyed and compare as null, so this is a no-op for them.
        /// </summary>
        private void DestroyDeadHusk()
        {
            if (_deadHusk == null) return;

            _eventBridge.UnsubscribeFromAircraft(_deadHusk);
            if (_deadHusk.gameObject != null)
            {
                UnityEngine.Object.Destroy(_deadHusk.gameObject);
                Log.Info(Tag, "Destroyed previous life's aircraft husk");
            }
            _deadHusk = null;
        }

        // ── Spawn position resolution ───────────────────────────────────────

        /// <summary>
        /// Resolve an airfield name and spawn type to a world position and rotation.
        /// Uses hardcoded positions for known airfields with fallback defaults.
        /// </summary>
        public void GetSpawnPosition(string airfieldName, bool isAirStart,
            out Vector3 position, out Quaternion rotation)
        {
            TryGetSpawnPosition(airfieldName, isAirStart, 0, 1, out position, out rotation);
        }

        private bool TryGetSpawnPosition(
            string airfieldName,
            bool isAirStart,
            int spawnSlot,
            int spawnCount,
            out Vector3 position,
            out Quaternion rotation)
        {
            // Use AirfieldHelper to resolve real airfield positions from game scene data.
            // This finds the actual Airfield2 component and uses native runway/ramp slots.
            var spawnType = _session.SpawnType;

            if (!string.IsNullOrEmpty(airfieldName))
            {
                try
                {
                    if (AirfieldHelper.TryGetSpawnPoint(
                            airfieldName,
                            spawnType,
                            spawnSlot,
                            spawnCount,
                            out var pos,
                            out var rot))
                    {
                        position = pos;
                        rotation = rot;
                        // NOTE: AirfieldHelper.GetSpawnPoint already applies InAir offset (+300m up, -2000m back)
                        // Do NOT apply it again here — was previously causing double offset

                        Log.Info(Tag, $"Resolved airfield '{airfieldName}' slot {spawnSlot + 1}/{spawnCount} → pos={position}, rot={rotation.eulerAngles}");
                        return true;
                    }
                }
                catch (System.Exception ex)
                {
                    Log.Warning(Tag, $"AirfieldHelper failed for '{airfieldName}': {ex.Message}");
                }
            }

            if (isAirStart)
            {
                position = AirSpawnDefault;
                rotation = AirSpawnRotation;
                return true;
            }

            Log.Warning(Tag, $"Could not resolve native ground spawn for airfield '{airfieldName}'");
            position = Vector3.zero;
            rotation = Quaternion.identity;
            return false;
        }

        private void ApplyMultiplayerSpawnOffset(
            ulong peerId,
            int slot,
            ref Vector3 position,
            Quaternion rotation)
        {
            if (slot == 0)
                return;

            float side = (slot % 2 == 0) ? -1f : 1f;
            int row = (slot + 1) / 2;
            const float lateralSpacing = 250f;
            const float trailSpacing = 450f;

            Vector3 right = rotation * Vector3.right;
            Vector3 back = -(rotation * Vector3.forward);
            position += (right * side * lateralSpacing * row) + (back * trailSpacing * row);
            Log.Info(Tag, $"Applied spawn slot {slot} offset for peer {peerId}: pos={position}");
        }

        private int GetSpawnSlot(ulong peerId, string airfieldName)
        {
            var peerIds = GetSpawnPeerIdsForAirfield(airfieldName);
            peerIds.Sort();
            int slot = peerIds.IndexOf(peerId);
            return slot >= 0 ? slot : 0;
        }

        private int GetSpawnSlotCount(string airfieldName)
        {
            int count = GetSpawnPeerIdsForAirfield(airfieldName).Count;
            return Mathf.Max(1, count);
        }

        private List<ulong> GetSpawnPeerIdsForAirfield(string airfieldName)
        {
            var peerIds = new List<ulong>();
            string normalizedAirfield = NormalizeAirfieldName(airfieldName);
            foreach (var player in _session.Players.Values)
            {
                if (player == null) continue;
                if (NormalizeAirfieldName(player.SelectedAirfield) != normalizedAirfield) continue;
                peerIds.Add(player.PeerId);
            }

            if (peerIds.Count == 0)
                peerIds.Add(_session.LocalPeerId);
            return peerIds;
        }

        private static string NormalizeAirfieldName(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;
            var chars = new List<char>(value.Length);
            foreach (char c in value)
            {
                if (char.IsWhiteSpace(c) || c == '_' || c == '-') continue;
                chars.Add(char.ToLowerInvariant(c));
            }
            return new string(chars.ToArray());
        }

        // ── Private helpers ─────────────────────────────────────────────────

        /// <summary>
        /// Direct call to GameDataAircraft.SpawnAircraft — no reflection.
        /// Returns null on failure instead of throwing.
        /// </summary>
        private UniAircraft TrySpawn(string aircraftType, Vector3 position,
            Quaternion rotation, bool isAirStart)
        {
            try
            {
                string name = $"MP_Local_{aircraftType}";
                Vector3 spawnPosition = GetNativeSpawnPosition(aircraftType, position, isAirStart);

                UniAircraft aircraft = GameDataAircraft.SpawnAircraft(
                    name,
                    aircraftType,
                    _localFaction,
                    PilotSkill.Ace,
                    spawnPosition,
                    rotation,
                    isAirStart
                );

                if (aircraft != null && !isAirStart)
                    ApplyNativeGroundSpawnState(aircraft);

                return aircraft;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"SpawnAircraft threw for '{aircraftType}': {ex.Message}");
                return null;
            }
        }

        private static Vector3 GetNativeSpawnPosition(string aircraftType, Vector3 position, bool isAirStart)
        {
            if (isAirStart)
                return position;

            var aircraftData = GameDataAircraft.GetByName(aircraftType);
            if (aircraftData == null)
                return position;

            position.y = TerrainTools.GetTerrainHeightAtPosition(position);
            position.y -= aircraftData.SpawnOffset;
            return position;
        }

        private static void ApplyNativeGroundSpawnState(UniAircraft aircraft)
        {
            if (aircraft == null)
                return;

            try
            {
                if (aircraft.Data != null)
                    aircraft.transform.Rotate(Vector3.right * (0f - aircraft.Data.SpawnRotation), Space.Self);
                if (aircraft.Rigidbody != null)
                    aircraft.Rigidbody.velocity = Vector3.zero;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to apply native ground spawn state: {ex.Message}");
            }
        }

        /// <summary>
        /// Apply a loadout to the spawned aircraft via Stores.LoadLoadout.
        /// </summary>
        private static void ApplyLoadout(UniAircraft aircraft, string loadoutName)
        {
            if (string.IsNullOrEmpty(loadoutName)) return;

            try
            {
                if (aircraft.Stores != null)
                {
                    aircraft.Stores.LoadLoadout(loadoutName);
                    LoadDefaultAmmoBelt(aircraft);
                    Log.Info(Tag, $"Applied loadout '{loadoutName}'");
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Failed to apply loadout '{loadoutName}': {ex.Message}");
            }
        }

        private static void LoadDefaultAmmoBelt(UniAircraft aircraft)
        {
            if (aircraft?.Stores == null || aircraft.FireControl?.Gun == null)
                return;

            string beltName = GameDataLoadouts.GetDefaultAmmoBeltName(aircraft.Data?.Name ?? "");
            if (string.IsNullOrEmpty(beltName))
                return;

            aircraft.Stores.LoadAmmoBelt(beltName);
            Log.Info(Tag, $"Applied default ammo belt '{beltName}'");
        }

        /// <summary>
        /// Handles the local aircraft being destroyed — fires OnPlayerDied
        /// and updates session state.
        /// </summary>
        private void HandleAircraftDestroyed(UniAircraft aircraft)
        {
            if (_disposed) return;

            // Only handle our own aircraft
            if (!ReferenceEquals(aircraft, _localAircraft)) return;

            Log.Info(Tag, "Local player aircraft destroyed");

            // Unsubscribe our own handler, but keep the GameEventBridge attached:
            // this first event is usually the CRITICAL (burning) phase, and the
            // wreck fires OnAircraftDestroyed again when it finally explodes
            // (HP 0 → DestroyAircraft). ExplosionSyncSystem needs that second
            // event to broadcast the final native explosion to remote peers —
            // unsubscribing the bridge here made clones burn forever and then
            // vanish instead of exploding. The bridge is detached in
            // DestroyDeadHusk/DespawnLocalPlayer once the airframe is gone.
            aircraft.OnAircraftDestroyed -= HandleAircraftDestroyed;

            // Keep a handle on the dead airframe so the next spawn can remove it
            // (an ejected husk keeps flying; see DestroyDeadHusk)
            _deadHusk = aircraft;
            _localAircraft = null;

            // Update player state
            var player = _session.GetLocalPlayer();
            uint lifeId = player != null ? _session.EndPlayerLife(player.PeerId) : 0;

            Log.Info(Tag, $"Local player aircraft life ended life={lifeId}");
            OnPlayerDied?.Invoke();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(SpawnManager));
        }

        // ── Dispose ─────────────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            DespawnLocalPlayer();
            DestroyDeadHusk();

            OnPlayerSpawned = null;
            OnPlayerDied = null;

            Log.Info(Tag, "Disposed");
        }
    }
}
