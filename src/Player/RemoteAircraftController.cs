using UnityEngine;
using TCAMultiplayer.Networking;
using TCAMultiplayer.Patches;
using System;
using System.Collections.Generic;
using System.Reflection;
using TCAMultiplayer;

namespace TCAMultiplayer.Player
{
    /// <summary>
    /// Component attached to cloned remote player aircraft
    /// Handles syncing visual state: gear, flaps, control surfaces, effects
    /// 
    /// Based on AV8B.json configuration:
    /// - Control surfaces use Y-axis rotation (0,1,0) except nozzles/speedbrake which use X-axis (1,0,0)
    /// - Ailerons: AngleByRoll [-1: -20°, 1: +20°] (same for both L and R)
    /// - Elevators: AngleByPitch [-1: -20°, 0: 0°, 1: +10°]
    /// - Rudder: AngleByYaw [-1: +30°, 1: -30°] (inverted)
    /// - Flaps: FlapInfluence with flap angle (25° normal, 62° VTOL)
    /// - Nozzles: X-axis rotation, driven by NozzleAnalog (0-1 maps to 0-100°)
    /// </summary>
    public class RemoteAircraftController : MonoBehaviour
    {
        public ulong PlayerId { get; set; }
        
        /// <summary>
        /// True when the remote aircraft has been destroyed. 
        /// Used to prevent multiple destroy calls and damage after destruction.
        /// </summary>
        public bool IsDestroyed { get; private set; } = false;
        
        // Current state
        private bool _afterburnerActive = false;
        private bool _gearDown = true;
        private bool _flapsDown = false;
        private bool _isFiring = false;
        
        /// <summary>
        /// Whether the remote player is firing their gun (read by FireControlPatches)
        /// </summary>
        public bool IsFiring => _isFiring;
        private float _throttle = 0f;
        private float _pitch = 0f;
        private float _roll = 0f;
        private float _yaw = 0f;
        private float _nozzleAngle = 0f;
        
        // Gear animation state (0 = down, 1 = up)
        private float _gearAnimState = 0f;
        private float _targetGearState = 0f;
        private const float GEAR_ANIM_SPEED = 0.33f; // Takes ~3 seconds to animate (matching RetractTime: 3.0)
        
        // Cached components
        private Animator _animator;
        
        // Control surface transforms and their rotation config
        private class ControlSurface
        {
            public string Name;
            public Transform Transform;
            public Quaternion StartRotation;
            public Vector3 LocalAxis;
            public float CurrentAngle;
            public ControlType Type;
            public bool IsLeftSide; // For flaps with opposite rotation
        }
        
        private enum ControlType { Aileron, Elevator, Rudder, Flap, Nozzle, SpeedBrake }
        
        private List<ControlSurface> _controlSurfaces = new List<ControlSurface>();
        
        // Engine effects (EngineFX components)
        private List<Component> _engineFXComponents = new List<Component>();
        
        // Gun effects (muzzle flash particle systems)
        private List<ParticleSystem> _muzzleFlashSystems = new List<ParticleSystem>();
        private bool _wasFirePreviousFrame = false;
        
        // We use the game's Gun2 system directly - no need for manual bullet management
        
        // Reflection cache for game types
        private static Type _engineFXType;
        private static FieldInfo _engineFXIsRunningField;
        private static FieldInfo _engineFXTargetAfterburnField;
        private static FieldInfo _engineFXJetThrottleField;
        
        private bool _reflectionInitialized = false;

        /// <summary>
        /// Initialize the controller after the aircraft has been cloned
        /// </summary>
        public void Initialize()
        {
            try
            {
                Plugin.Log.LogInfo("[RemoteAircraftController] Initializing...");
                
                // CRITICAL: Register with RemoteAircraftRegistry so patches know this is remote
                RemoteAircraftRegistry.RegisterRemote(gameObject);
                
                InitializeReflection();
                
                // Get animator for gear animation
                _animator = GetComponentInChildren<Animator>();
                if (_animator != null)
                {
                    Plugin.Log.LogInfo($"[RemoteAircraftController] Found Animator: {_animator.gameObject.name}");
                    
                    // Log animator parameters
                    foreach (var param in _animator.parameters)
                    {
                        Plugin.Log.LogInfo($"[RemoteAircraftController] Animator param: {param.name} ({param.type})");
                    }
                }
                
                // Find EngineFX components (these handle afterburner visuals)
                FindEngineFXComponents();
                
                // Find muzzle flash particle systems for gun effects
                FindMuzzleFlashSystems();
                
                // Set up radar for RWR detection (makes local player's RWR detect this aircraft's lock)
                SetupRadarForThreatWarning();
                
                // Find control surface transforms - only target the actual animated bones
                FindControlSurfaces();
                
                // Initialize gear state based on what we expect
                // Don't force a state - let the first packet determine it
                SetAfterburnerState(false);
                
                Plugin.Log.LogInfo("[RemoteAircraftController] Initialization complete");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftController] Init error: {ex.Message}\n{ex.StackTrace}");
            }
        }
        
        private void InitializeReflection()
        {
            if (_reflectionInitialized) return;
            
            try
            {
                var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                
                // Get EngineFX type
                _engineFXType = Type.GetType("Falcon.Effects.EngineFX, Assembly-CSharp");
                if (_engineFXType != null)
                {
                    _engineFXIsRunningField = _engineFXType.GetField("isRunning", flags);
                    _engineFXTargetAfterburnField = _engineFXType.GetField("targetAfterburn", flags);
                    _engineFXJetThrottleField = _engineFXType.GetField("jetThrottle", flags);
                    Plugin.Log.LogInfo($"[RemoteAircraftController] EngineFX type found");
                }
                
                _reflectionInitialized = true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[RemoteAircraftController] Reflection init error: {ex.Message}");
            }
        }
        
        private void FindEngineFXComponents()
        {
            try
            {
                if (_engineFXType == null) return;
                
                var allComponents = GetComponentsInChildren<Component>(true);
                foreach (var comp in allComponents)
                {
                    if (comp != null && comp.GetType() == _engineFXType)
                    {
                        _engineFXComponents.Add(comp);
                        Plugin.Log.LogInfo($"[RemoteAircraftController] Found EngineFX: {comp.gameObject.name}");
                    }
                }
                
                Plugin.Log.LogInfo($"[RemoteAircraftController] Found {_engineFXComponents.Count} EngineFX components");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] FindEngineFX error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Find muzzle flash particle systems for gun fire effects
        /// </summary>
        private void FindMuzzleFlashSystems()
        {
            try
            {
                // Look for particle systems that are children of objects with "gun", "barrel", "muzzle" in name
                var allParticles = GetComponentsInChildren<ParticleSystem>(true);
                foreach (var ps in allParticles)
                {
                    string name = ps.gameObject.name.ToLower();
                    string parentName = ps.transform.parent?.name.ToLower() ?? "";
                    
                    // Check for muzzle flash related names
                    if (name.Contains("muzzle") || name.Contains("flash") || 
                        parentName.Contains("gun") || parentName.Contains("barrel") ||
                        parentName.Contains("firepoint"))
                    {
                        _muzzleFlashSystems.Add(ps);
                        Plugin.Log.LogInfo($"[RemoteAircraftController] Found muzzle flash: {ps.gameObject.name} (parent: {ps.transform.parent?.name})");
                    }
                }
                
                Plugin.Log.LogInfo($"[RemoteAircraftController] Found {_muzzleFlashSystems.Count} muzzle flash systems");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] FindMuzzleFlash error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Set up the aircraft's radar for RWR threat detection
        /// </summary>
        private void SetupRadarForThreatWarning()
        {
            try
            {
                // Use RealCombatSync to set up the radar
                RealCombatSync.SetupRemoteRadar(gameObject, PlayerId);
                Plugin.Log.LogInfo($"[RemoteAircraftController] Set up radar for RWR detection");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] SetupRadarForThreatWarning error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Find control surface transforms by exact bone names from AV8B.json
        /// </summary>
        private void FindControlSurfaces()
        {
            try
            {
                var allTransforms = GetComponentsInChildren<Transform>(true);
                
                // Create a dictionary for fast lookup
                var transformDict = new Dictionary<string, Transform>();
                foreach (var t in allTransforms)
                {
                    if (!transformDict.ContainsKey(t.name))
                        transformDict[t.name] = t;
                }
                
                // Ailerons - Y-axis rotation, AngleByRoll: [-1: -20°, 1: +20°]
                TryAddControlSurface(transformDict, "BAileronL", ControlType.Aileron, Vector3.up, true);
                TryAddControlSurface(transformDict, "BAileronR", ControlType.Aileron, Vector3.up, false);
                
                // Elevators - Y-axis rotation, AngleByPitch: [-1: -20°, 0: 0°, 1: +10°]
                TryAddControlSurface(transformDict, "BElevators", ControlType.Elevator, Vector3.up, false);
                
                // Rudder - Y-axis rotation, AngleByYaw: [-1: +30°, 1: -30°]
                TryAddControlSurface(transformDict, "BRudder", ControlType.Rudder, Vector3.up, false);
                
                // Flaps - Y-axis rotation, FlapInfluence: 1 (left) and -1 (right, opposite direction)
                TryAddControlSurface(transformDict, "BFlapL", ControlType.Flap, Vector3.up, true);
                TryAddControlSurface(transformDict, "BFlapR", ControlType.Flap, Vector3.up, false);
                
                // Speed brake - X-axis rotation
                TryAddControlSurface(transformDict, "SpeedBrake", ControlType.SpeedBrake, Vector3.right, false);
                
                // Nozzles - X-axis rotation, driven by NozzleAnalog (0-1 maps to 0-100°)
                TryAddControlSurface(transformDict, "NozzleFrontLeft", ControlType.Nozzle, Vector3.right, false);
                TryAddControlSurface(transformDict, "NozzleFrontRight", ControlType.Nozzle, Vector3.right, false);
                TryAddControlSurface(transformDict, "NozzleRearLeft", ControlType.Nozzle, Vector3.right, false);
                TryAddControlSurface(transformDict, "NozzleRearRight", ControlType.Nozzle, Vector3.right, false);
                
                Plugin.Log.LogInfo($"[RemoteAircraftController] Found {_controlSurfaces.Count} control surfaces");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] FindControlSurfaces error: {ex.Message}");
            }
        }
        
        private void TryAddControlSurface(Dictionary<string, Transform> dict, string name, ControlType type, Vector3 axis, bool isLeft)
        {
            if (dict.TryGetValue(name, out Transform t))
            {
                var surface = new ControlSurface
                {
                    Name = name,
                    Transform = t,
                    StartRotation = t.localRotation,
                    LocalAxis = axis,
                    CurrentAngle = 0f,
                    Type = type,
                    IsLeftSide = isLeft
                };
                _controlSurfaces.Add(surface);
                Plugin.Log.LogInfo($"[RemoteAircraftController] Added control surface: {name} ({type}, axis={axis})");
            }
            else
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] Control surface not found: {name}");
            }
        }

        /// <summary>
        /// Update all visual state from network packet
        /// </summary>
        public void UpdateFromState(AircraftStatePacket state)
        {
            // Update control inputs
            _throttle = state.Throttle;
            _pitch = state.Pitch;
            _roll = state.Roll;
            _yaw = state.Yaw;
            _nozzleAngle = state.NozzleAngle;
            
            // Update flags
            if (state.Afterburner != _afterburnerActive)
            {
                _afterburnerActive = state.Afterburner;
                SetAfterburnerState(_afterburnerActive);
            }
            
            if (state.GearDown != _gearDown)
            {
                _gearDown = state.GearDown;
                _targetGearState = _gearDown ? 0f : 1f;
                Plugin.Log.LogInfo($"[RemoteAircraftController] Gear target: {(_gearDown ? "DOWN (0)" : "UP (1)")}");
            }
            
            if (state.FlapsDown != _flapsDown)
            {
                _flapsDown = state.FlapsDown;
                Plugin.Log.LogInfo($"[RemoteAircraftController] Flaps: {(_flapsDown ? "DOWN" : "UP")}");
            }
            
            _isFiring = state.IsFiring;
            
            if (LogHelper.IsEnabled(LogCategory.Player) &&
                LogHelper.ShouldSample("RemoteAircraftController.UpdateFromState", LogHelper.HighFreqSampleRate))
            {
                LogHelper.Info(LogCategory.Player,
                    $"[RemoteAircraftController] State t={state.Timestamp:F2} throttle={_throttle:F2} " +
                    $"pitch={_pitch:F2} roll={_roll:F2} yaw={_yaw:F2} nozzle={_nozzleAngle:F1} " +
                    $"flags=AB:{_afterburnerActive} Gear:{_gearDown} Flaps:{_flapsDown} Fire:{_isFiring}");
            }
        }
        
        private void Update()
        {
            // Animate gear smoothly
            UpdateGearAnimation();
            
            // Update control surface rotations
            UpdateControlSurfaces();
            
            // Update engine throttle display
            UpdateEngineThrottle();
            
            // Update gun fire effects
            UpdateGunFireEffects();
        }
        
        private void UpdateGunFireEffects()
        {
            // Detect when firing starts or stops for muzzle flash effects
            if (_isFiring && !_wasFirePreviousFrame)
            {
                // Just started firing - play muzzle flash
                foreach (var ps in _muzzleFlashSystems)
                {
                    if (ps != null && !ps.isPlaying)
                    {
                        ps.Play();
                    }
                }
                LogHelper.Info(LogCategory.Player, "[RemoteAircraftController] Gun fire started");
            }
            else if (!_isFiring && _wasFirePreviousFrame)
            {
                // Stopped firing - stop muzzle flash
                foreach (var ps in _muzzleFlashSystems)
                {
                    if (ps != null && ps.isPlaying)
                    {
                        ps.Stop();
                    }
                }
                LogHelper.Info(LogCategory.Player, "[RemoteAircraftController] Gun fire stopped");
            }
            
            // NOTE: Actual bullet firing is handled by FireControlPatches.Update_Prefix
            // which reads IsFiring from this controller and calls Gun.Update() on the native Gun2 system
            // This is the same system AI enemies use - we just feed it network state instead of local input
            
            _wasFirePreviousFrame = _isFiring;
        }
        
        private void UpdateGearAnimation()
        {
            // Smoothly animate gear state
            if (Mathf.Abs(_gearAnimState - _targetGearState) > 0.001f)
            {
                _gearAnimState = Mathf.MoveTowards(_gearAnimState, _targetGearState, GEAR_ANIM_SPEED * Time.deltaTime);
                SetGearAnimatorState(_gearAnimState);
            }
        }
        
        private void SetGearAnimatorState(float state)
        {
            if (_animator == null) return;
            
            try
            {
                // The game uses "GearUp" float parameter: 0 = down, 1 = up
                _animator.SetFloat("GearUp", state);
            }
            catch { }
        }
        
        private void UpdateControlSurfaces()
        {
            // Flap angle: 25° normal flight, could be 62° for VTOL mode
            // We'll use 25° for now since we don't track VTOL mode separately
            float flapAngle = 25f;
            
            foreach (var surface in _controlSurfaces)
            {
                if (surface.Transform == null) continue;
                
                float targetAngle = 0f;
                
                switch (surface.Type)
                {
                    case ControlType.Aileron:
                        // From AV8B.json: AngleByRoll: [{-1: -20°}, {1: +20°}]
                        // Both ailerons move the same direction based on the JSON
                        // roll input -1 to 1 maps to angle -20 to +20
                        targetAngle = _roll * 20f;
                        break;
                        
                    case ControlType.Elevator:
                        // From AV8B.json: AngleByPitch: [{-1: -20°}, {0: 0°}, {1: +10°}]
                        // Asymmetric: -1 pitch = -20°, +1 pitch = +10°
                        if (_pitch < 0)
                            targetAngle = _pitch * 20f; // -1 to 0 maps to -20 to 0
                        else
                            targetAngle = _pitch * 10f; // 0 to 1 maps to 0 to +10
                        break;
                        
                    case ControlType.Rudder:
                        // From AV8B.json: AngleByYaw: [{-1: +30°}, {1: -30°}]
                        // INVERTED: yaw input -1 to 1 maps to angle +30 to -30
                        targetAngle = -_yaw * 30f;
                        break;
                        
                    case ControlType.Flap:
                        // From AV8B.json: FlapInfluence: 1 for left, -1 for right (opposite rotation)
                        // RotationAxis: [0,1,0] for left, [0,-1,0] for right
                        if (_flapsDown)
                        {
                            // Left flap: positive angle, Right flap: negative angle
                            targetAngle = surface.IsLeftSide ? flapAngle : -flapAngle;
                        }
                        break;
                        
                    case ControlType.Nozzle:
                        // From AV8B.json: VTOLNozzleMax: 100, driven by NozzleAnalog
                        // NozzleAngle from packet is in degrees (0-100)
                        targetAngle = _nozzleAngle;
                        break;
                        
                    case ControlType.SpeedBrake:
                        // From AV8B.json: AngleByBrake: [{0: 0°}, {1: -50°}]
                        // We don't have brake state in the packet, skip for now
                        targetAngle = 0f;
                        break;
                }
                
                // Smoothly interpolate to target (Speed: 5 from JSON = 5 degrees per second base rate)
                // We'll use a faster rate for responsiveness
                float speed = surface.Type == ControlType.Nozzle ? 180f : 120f; // Nozzles are fast (180 deg/s from JSON)
                surface.CurrentAngle = Mathf.MoveTowards(surface.CurrentAngle, targetAngle, speed * Time.deltaTime);
                
                // Apply rotation around the correct local axis
                Quaternion rotation = Quaternion.AngleAxis(surface.CurrentAngle, surface.LocalAxis);
                surface.Transform.localRotation = surface.StartRotation * rotation;
            }
        }
        
        private void UpdateEngineThrottle()
        {
            // Update engine throttle visuals
            foreach (var engineFX in _engineFXComponents)
            {
                if (engineFX == null) continue;
                
                try
                {
                    if (_engineFXJetThrottleField != null)
                        _engineFXJetThrottleField.SetValue(engineFX, _throttle);
                }
                catch { }
            }
        }
        
        private void SetAfterburnerState(bool active)
        {
            try
            {
                foreach (var engineFX in _engineFXComponents)
                {
                    if (engineFX == null) continue;
                    
                    // Set isRunning to true
                    if (_engineFXIsRunningField != null)
                        _engineFXIsRunningField.SetValue(engineFX, true);
                    
                    // Set targetAfterburn
                    if (_engineFXTargetAfterburnField != null)
                        _engineFXTargetAfterburnField.SetValue(engineFX, active ? 1f : 0f);
                    
                    // Set jetThrottle
                    if (_engineFXJetThrottleField != null)
                        _engineFXJetThrottleField.SetValue(engineFX, _throttle);
                }
                
                Plugin.Log.LogInfo($"[RemoteAircraftController] Afterburner: {(active ? "ON" : "OFF")}");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[RemoteAircraftController] SetAfterburner error: {ex.Message}");
            }
        }

        /// <summary>
        /// Called when the remote player's aircraft is destroyed
        /// Hide/disable visuals but don't destroy the object (may respawn)
        /// </summary>
        public void OnDestroyed()
        {
            try
            {
                // Prevent multiple destroy calls
                if (IsDestroyed)
                {
                    Plugin.Log?.LogInfo("[RemoteAircraftController] OnDestroyed already called, ignoring");
                    return;
                }
                
                IsDestroyed = true;
                Plugin.Log?.LogInfo($"[RemoteAircraftController] Remote aircraft destroyed!");
                
                // Could play explosion effect here
                // For now, just disable renderers temporarily
                var renderers = GetComponentsInChildren<Renderer>(true);
                foreach (var r in renderers)
                {
                    if (r != null)
                        r.enabled = false;
                }
                
                // Stop any particle effects
                foreach (var ps in _muzzleFlashSystems)
                {
                    if (ps != null)
                        ps.Stop();
                }
                
                // Stop afterburner
                SetAfterburnerState(false);
                
                // Disable colliders to prevent further damage detection
                var colliders = GetComponentsInChildren<Collider>(true);
                foreach (var col in colliders)
                {
                    if (col != null)
                        col.enabled = false;
                }
                
                Plugin.Log?.LogInfo("[RemoteAircraftController] Disabled all renderers and colliders");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[RemoteAircraftController] OnDestroyed error: {ex.Message}");
            }
        }
        
        /// <summary>
        /// Reset destroyed state (called when respawning)
        /// </summary>
        public void ResetDestroyedState()
        {
            IsDestroyed = false;
            
            // Re-enable renderers
            var renderers = GetComponentsInChildren<Renderer>(true);
            foreach (var r in renderers)
            {
                if (r != null)
                    r.enabled = true;
            }
            
            // Re-enable colliders
            var colliders = GetComponentsInChildren<Collider>(true);
            foreach (var col in colliders)
            {
                if (col != null)
                {
                    string colName = col.gameObject.name.ToLower();
                    // Keep wheel/gear colliders disabled
                    if (!colName.Contains("wheel") && !colName.Contains("gear") && !colName.Contains("tire"))
                    {
                        col.enabled = true;
                    }
                }
            }
            
            Plugin.Log?.LogInfo("[RemoteAircraftController] Reset destroyed state, re-enabled renderers and colliders");
        }

        private void OnDestroy()
        {
            // Unregister from RemoteAircraftRegistry
            RemoteAircraftRegistry.UnregisterRemote(gameObject);
            
            Plugin.Log?.LogInfo($"[RemoteAircraftController] Destroyed for player {PlayerId}");
        }
    }
}
