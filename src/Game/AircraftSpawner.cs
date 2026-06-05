using System;
using System.Collections.Generic;
using UnityEngine;
using Falcon;
using Falcon.UniversalAircraft;
using Falcon.Factions;
using Falcon.Targeting;
using Falcon.Damage;
using Falcon.Controls;
using TCAMultiplayer.Core;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Spawns and configures aircraft using the game's native API.
    /// NO REFLECTION — uses direct GameDataAircraft.SpawnAircraft().
    /// </summary>
    public class AircraftSpawner : IDisposable
    {
        private const string Tag = "SPAWN";
        private const string DefaultAircraftType = "F-16C";

        private readonly FloatingOriginService _originService;
        private readonly Dictionary<ulong, UniAircraft> _spawnedAircraft = new Dictionary<ulong, UniAircraft>();
        private Func<ulong, bool> _isFriendlyPeer;
        private JFaction _enemyFaction; // lazy-initialized on first spawn
        private JFaction _friendlyFaction; // lazy-initialized on first spawn

        public AircraftSpawner(FloatingOriginService originService)
        {
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
            // Faction lookup deferred to EnsureEnemyFaction() — game data not available during Start()
        }

        public Func<ulong, bool> IsFriendlyPeer
        {
            get => _isFriendlyPeer;
            set => _isFriendlyPeer = value;
        }

        private void EnsureFactions()
        {
            try
            {
                var mapName = Falcon.Game2.GameLogic.Instance?.LoadedMap?.MapName;
                if (!string.IsNullOrEmpty(mapName))
                {
                    var mapData = GameDataMaps.GetByName(mapName);
                    if (mapData != null)
                    {
                        if (_friendlyFaction == null)
                            _friendlyFaction = mapData.GetPrimaryBlueFaction();
                        if (_enemyFaction == null)
                            _enemyFaction = mapData.GetPrimaryRedFaction();
                        if (_friendlyFaction != null)
                            Log.Info(Tag, $"Using map blue faction: {_friendlyFaction.DisplayName} ({_friendlyFaction.Name})");
                        if (_enemyFaction != null)
                            Log.Info(Tag, $"Using map red faction: {_enemyFaction.DisplayName} ({_enemyFaction.Name})");
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Map red faction lookup failed: {ex.Message}");
            }
            if (_friendlyFaction == null)
            {
                string[] fallbackNames = { "USMC", "US", "USA", "NATO", "Blue", "Player", "Ally" };
                foreach (var name in fallbackNames)
                {
                    _friendlyFaction = GameDataFactions.GetByName(name);
                    if (_friendlyFaction != null) { Log.Info(Tag, $"Using fallback blue faction: {name}"); break; }
                }
            }
            if (_enemyFaction == null)
            {
                string[] fallbackNames = { "OPFOR", "Russia", "RUS", "Red", "Enemy", "Aggressor" };
                foreach (var name in fallbackNames)
                {
                    _enemyFaction = GameDataFactions.GetByName(name);
                    if (_enemyFaction != null) { Log.Info(Tag, $"Using fallback red faction: {name}"); break; }
                }
            }
            if (_enemyFaction == null)
                Log.Error(Tag, "No enemy faction found — remote spawns will fail!");
            if (_friendlyFaction == null)
                Log.Warning(Tag, "No friendly faction found — friendly remote teams will use red fallback");
        }

        // ── Public API ──────────────────────────────────────────────────────

        /// <summary>
        /// Spawn a remote player's aircraft using the native game API.
        /// </summary>
        /// <param name="peerId">Unique network peer ID</param>
        /// <param name="aircraftType">Aircraft type name (e.g. "F-16C", "MiG-29")</param>
        /// <param name="localPosition">Local Unity position (caller should convert from absolute)</param>
        /// <param name="rotation">Aircraft rotation</param>
        /// <returns>Spawned and configured UniAircraft, or null on total failure</returns>
        public UniAircraft SpawnRemoteAircraft(ulong peerId, string aircraftType,
            Vector3 localPosition, Quaternion rotation)
        {
            // Destroy any existing aircraft for this peer first
            // Lazy-init enemy faction (game data not available during plugin Start())
            EnsureFactions();

            // Destroy any existing aircraft for this peer first
            DestroyAircraft(peerId);

            // Try requested type
            UniAircraft aircraft = TrySpawn(aircraftType, peerId, localPosition, rotation);

            // Fallback: try default type if requested type unavailable (mod mismatch)
            if (aircraft == null && aircraftType != DefaultAircraftType)
            {
                Log.Warning(Tag, $"Aircraft type '{aircraftType}' not found, falling back to {DefaultAircraftType}");
                aircraft = TrySpawn(DefaultAircraftType, peerId, localPosition, rotation);
            }

            // Last resort: try first available aircraft in game data
            if (aircraft == null)
            {
                aircraft = TrySpawnFirstAvailable(peerId, localPosition, rotation);
            }

            if (aircraft == null)
            {
                Log.Error(Tag, $"Could not spawn any aircraft for peer {peerId}");
                return null;
            }

            // Configure for remote control (kinematic, enemy, no AI)
            ConfigureForRemoteControl(peerId, aircraft);

            // Track
            _spawnedAircraft[peerId] = aircraft;

            Log.Info(Tag, $"Spawned remote aircraft '{aircraft.name}' for peer {peerId}");
            return aircraft;
        }

        /// <summary>
        /// Apply a loadout to a peer's aircraft post-spawn.
        /// Call after SpawnRemoteAircraft when loadout info is available.
        /// </summary>
        public void ApplyLoadout(ulong peerId, string loadoutName)
        {
            if (string.IsNullOrEmpty(loadoutName)) return;

            if (!_spawnedAircraft.TryGetValue(peerId, out var aircraft)
                || aircraft == null) return;

            try
            {
                // UniAircraft.Stores is a public StoresManagement field.
                // StoresManagement.LoadLoadout(string) is public.
                if (aircraft.Stores != null)
                {
                    aircraft.Stores.LoadLoadout(loadoutName);
                    LoadDefaultAmmoBelt(aircraft);
                    Log.Info(Tag, $"Applied loadout '{loadoutName}' to peer {peerId}");
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
            Log.Info(Tag, $"Applied default ammo belt '{beltName}' to remote aircraft");
        }

        /// <summary>Destroy a specific peer's aircraft.</summary>
        public void DestroyAircraft(ulong peerId)
        {
            if (_spawnedAircraft.TryGetValue(peerId, out var aircraft))
            {
                if (aircraft != null && aircraft.gameObject != null)
                {
                    UnityEngine.Object.Destroy(aircraft.gameObject);
                }
                _spawnedAircraft.Remove(peerId);
                Log.Info(Tag, $"Destroyed aircraft for peer {peerId}");
            }
        }

        /// <summary>Get a peer's spawned aircraft, or null.</summary>
        public UniAircraft GetAircraft(ulong peerId)
        {
            _spawnedAircraft.TryGetValue(peerId, out var aircraft);
            return aircraft;
        }

        /// <summary>Check if a peer has a live spawned aircraft.</summary>
        public bool HasAircraft(ulong peerId)
        {
            return _spawnedAircraft.TryGetValue(peerId, out var aircraft)
                && aircraft != null
                && aircraft.gameObject != null;
        }

        public void Dispose()
        {
            foreach (var kvp in _spawnedAircraft)
            {
                if (kvp.Value != null && kvp.Value.gameObject != null)
                {
                    UnityEngine.Object.Destroy(kvp.Value.gameObject);
                }
            }
            _spawnedAircraft.Clear();
            Log.Info(Tag, "Disposed — all remote aircraft destroyed");
        }

        // ── Spawn helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Direct call to GameDataAircraft.SpawnAircraft — no reflection.
        /// Returns null on failure instead of throwing.
        /// </summary>
        private UniAircraft TrySpawn(string aircraftType, ulong peerId,
            Vector3 position, Quaternion rotation)
        {
            try
            {
                string name = $"MP_Remote_{peerId}_{aircraftType}";

                // GameDataAircraft.SpawnAircraft checks HasData() internally
                // and returns null + logs error if type not found.
                UniAircraft aircraft = GameDataAircraft.SpawnAircraft(
                    name,
                    aircraftType,
                    _enemyFaction,
                    PilotSkill.Ace,
                    position,
                    rotation,
                    true // isAirstart — remote aircraft are always airborne
                );

                return aircraft;
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"SpawnAircraft threw for '{aircraftType}': {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Last-resort fallback: try the first aircraft type available in game data.
        /// Handles case where even the default type is missing.
        /// </summary>
        private UniAircraft TrySpawnFirstAvailable(ulong peerId,
            Vector3 position, Quaternion rotation)
        {
            try
            {
                List<string> available = GameDataAircraft.GetListOfAllAircraft();
                if (available == null || available.Count == 0)
                {
                    Log.Error(Tag, "No aircraft types available in game data");
                    return null;
                }

                string fallbackType = available[0];
                Log.Warning(Tag, $"Last resort: trying first available aircraft '{fallbackType}'");
                return TrySpawn(fallbackType, peerId, position, rotation);
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to enumerate aircraft types: {ex.Message}");
                return null;
            }
        }

        // ── Remote aircraft configuration ───────────────────────────────────

        /// <summary>
        /// Configure a spawned aircraft for remote control.
        /// Makes it a dynamic network puppet, sets enemy faction, disables AI/input/flight model.
        /// Keeps: Target (radar), Damageable (damage), Signature (IR/RCS), visual renderers,
        /// gear animation, and all visual effects (trails, smoke, damage FX).
        /// </summary>
        private void ConfigureForRemoteControl(ulong peerId, UniAircraft aircraft)
        {
            var go = aircraft.gameObject;

            ConfigurePhysics(go);
            ConfigureTargeting(peerId, aircraft);
            ConfigureDamageable(go);
            DisableGameplayComponents(go);
            DisableRemoteMotionAndVisibilityComponents(go);
            DisableCockpitCam(go);
            ConfigureAnimators(go);

            // Bypass AI pilot to prevent autonomous weapon and countermeasure control.
            // When IsPilotBypassed is true, UniAircraft.FixedUpdate() takes the
            // ResetControls path instead of running UniPilot.Update(), which prevents
            // the AI from setting flare/chaff/gun firing flags via WeaponControls.
            // Without this, line 312 (WeaponControls = UniPilot.WeaponInput) overwrites
            // our WeaponControls replacement every FixedUpdate frame.
            aircraft.IsPilotBypassed = true;

            // Replace WeaponControls with a fresh instance disconnected from the AI pilot.
            // This ensures no residual AI decisions propagate through WeaponControls.
            aircraft.WeaponControls = new WeaponInput();

            DisableNativeAircraftLoop(aircraft);
        }

        /// <summary>
        /// Configure the rigidbody as a DYNAMIC network puppet.
        ///
        /// The body must stay non-kinematic so velocity writes are legal — native
        /// systems read Rigidbody.velocity directly (Target.Velocity drives missile
        /// proportional navigation, Viewable drives cameras, FreeWheel/WheelAudio/
        /// GearSlip read it too) and a kinematic body always reads zero velocity
        /// (Unity rejects writes on kinematic bodies). Nothing fights our control:
        /// UniAircraft (the only force source) is disabled on clones, gravity is off,
        /// and RemoteAircraftController re-asserts the full pose + velocity every
        /// FixedUpdate, so physics integration between corrections simply continues
        /// the networked motion.
        /// </summary>
        private static void ConfigurePhysics(GameObject go)
        {
            var rb = go.GetComponent<Rigidbody>();
            if (rb != null)
            {
                rb.isKinematic = false;
                rb.useGravity = false;
                rb.drag = 0f;
                rb.angularDrag = 0f;
                rb.velocity = Vector3.zero;
                rb.angularVelocity = Vector3.zero;
                // Interpolation must stay OFF to match native aircraft — TCA renders
                // its whole world on the physics timeline.
                rb.interpolation = RigidbodyInterpolation.None;
                rb.detectCollisions = true;
            }
        }

        /// <summary>
        /// Keep the UniAircraft data/component available for targeting, damage, and
        /// loadouts, but stop its native Update/FixedUpdate from moving remote clones.
        /// RemoteAircraftController manually drives the visual state from network samples.
        /// </summary>
        private static void DisableNativeAircraftLoop(UniAircraft aircraft)
        {
            if (aircraft == null)
                return;

            if (aircraft.enabled)
            {
                aircraft.enabled = false;
                Log.Info(Tag, $"Disabled UniAircraft native movement on {aircraft.gameObject.name}");
            }
        }

        /// <summary>
        /// Set enemy faction and coalition so the local player's radar can track this aircraft.
        /// Uses Target.SetFaction(JFaction) — public API, no reflection.
        /// </summary>
        private void ConfigureTargeting(ulong peerId, UniAircraft aircraft)
        {
            var target = aircraft.gameObject.GetComponentInChildren<Target>();
            if (target == null) return;

            bool friendly = _isFriendlyPeer != null && _isFriendlyPeer(peerId);
            var faction = friendly && _friendlyFaction != null
                ? _friendlyFaction
                : _enemyFaction;

            if (faction == null)
                return;

            // SetFaction sets both Faction string and Coalition from the JFaction
            target.SetFaction(faction);

            // Public writable fields
            target.TargetType = TargetType.Fighter;
            target.IsDestroyed = false;
            target.IsCriticalHP = false;
            target.DefaultCoalition = faction.Coalition;

            // Force re-registration with TargetManagement
            target.enabled = false;
            target.enabled = true;

            Log.Debug(Tag, $"Target configured: {faction.Coalition} coalition, Fighter type");
        }

        /// <summary>
        /// Ensure damageable is active and not invincible.
        /// Uses public HitPoints/MaxHitpoints/IsInvincible — no reflection.
        /// </summary>
        private static void ConfigureDamageable(GameObject go)
        {
            var damageable = go.GetComponentInChildren<Damageable>();
            if (damageable == null) return;

            damageable.enabled = true;
            damageable.IsInvincible = false;

            // Ensure full HP
            if (damageable.MaxHitpoints > 0)
            {
                damageable.HitPoints = damageable.MaxHitpoints;
            }
        }

        /// <summary>
        /// Disable gameplay components that shouldn't run on remote aircraft.
        /// Iterates MonoBehaviours by type name — NOT reflection, just GetType().Name.
        /// 
        /// Note: UniPilot is a plain C# class (not MonoBehaviour), so it won't appear
        /// here. It becomes inert because UniFlight and FlightInput are disabled,
        /// and the rigidbody is kinematic.
        /// </summary>
        private static void DisableGameplayComponents(GameObject go)
        {
            // Components to explicitly disable: AI, input, and the flight model (anything
            // that could apply forces or fight the network controller).
            // Visual effects (trails, smoke, gear animation, damage FX) and audio
            // (engine, flyby) stay ENABLED — they read FlightStats/Rigidbody.velocity,
            // which RemoteAircraftController feeds with network state every FixedUpdate,
            // so they behave exactly like they do on native AI aircraft.
            string[] disableNames =
            {
                "UniPilot",     // May appear as a MonoBehaviour wrapper
                "FlightInput",
                "WeaponInput",
                "UniAircraftDamage",
                "UniFlight",
                "StickAndRudder",
                "VehicleLauncher",
                "ReallySimpleWing",     // aero forces — would push the dynamic puppet
                "WingCollisionHandler",
                "CollisionEffects"
            };

            // Components to keep enabled for combat and visuals
            string[] keepNames =
            {
                "Target",
                "Damageable",
                "Signature",
                "FireControl",
                "StoresManagement"
            };

            var allComponents = go.GetComponentsInChildren<MonoBehaviour>(true);
            int disabledCount = 0;

            foreach (var comp in allComponents)
            {
                if (comp == null) continue;

                string typeName = comp.GetType().Name;

                // Never disable components we need for combat
                bool shouldKeep = false;
                foreach (var keep in keepNames)
                {
                    if (typeName.Contains(keep))
                    {
                        shouldKeep = true;
                        break;
                    }
                }
                if (shouldKeep) continue;

                // Disable if in explicit list
                bool shouldDisable = false;
                foreach (var name in disableNames)
                {
                    if (typeName.Contains(name))
                    {
                        shouldDisable = true;
                        break;
                    }
                }

                // Also disable anything that looks like AI or player input
                if (!shouldDisable)
                {
                    if (typeName.Contains("AI") || typeName.Contains("Input")
                        || typeName.Contains("Pilot"))
                    {
                        shouldDisable = true;
                    }
                }

                if (shouldDisable)
                {
                    comp.enabled = false;
                    disabledCount++;
                }
            }

            Log.Debug(Tag, $"Disabled {disabledCount} gameplay components");
        }

        private static void DisableRemoteMotionAndVisibilityComponents(GameObject go)
        {
            // Gear components (FreeWheel/GearTouchdown) stay ENABLED — FreeWheel owns
            // the gear retract/extend animation (lerps retractPercent + drives the
            // Animator). Its wheel physics forces only apply when grounded, on a body
            // whose pose is re-asserted every FixedUpdate anyway.
            int disabledSmartScaling = 0;
            bool disableSmartScaling = ModConfig.DisableRemoteSmartScaling?.Value ?? true;

            foreach (var comp in go.GetComponentsInChildren<MonoBehaviour>(true))
            {
                if (comp == null)
                    continue;

                string typeName = comp.GetType().Name;
                if (disableSmartScaling && typeName == "SmartScaling" && comp.enabled)
                {
                    comp.enabled = false;
                    disabledSmartScaling++;
                }
            }

            Log.Info(Tag, $"Disabled remote native visual/motion helpers: " +
                          $"smartScaling={disabledSmartScaling}");
        }

        /// <summary>
        /// Disable cockpit camera and audio listeners on remote aircraft.
        /// </summary>
        private static void DisableCockpitCam(GameObject go)
        {
            var cockpitCam = go.transform.Find("Model/CockpitCam");
            if (cockpitCam != null)
            {
                var cam = cockpitCam.GetComponent<Camera>();
                if (cam != null) cam.enabled = false;
                cockpitCam.gameObject.SetActive(false);
            }

            // Disable any AudioListener — only one can be active in a scene
            var listeners = go.GetComponentsInChildren<AudioListener>(true);
            foreach (var listener in listeners)
            {
                if (listener != null) listener.enabled = false;
            }
        }

        /// <summary>
        /// Disable root motion on animators to prevent transform drift on remote clones.
        /// </summary>
        private static void ConfigureAnimators(GameObject go)
        {
            var animators = go.GetComponentsInChildren<Animator>(true);
            foreach (var animator in animators)
            {
                if (animator == null) continue;
                animator.applyRootMotion = false;
                animator.updateMode = AnimatorUpdateMode.Normal;
            }
        }
    }
}
