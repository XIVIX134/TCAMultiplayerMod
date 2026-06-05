using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using Falcon.UniversalAircraft;
using Falcon.Weapons;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Game;

namespace TCAMultiplayer.Sync
{
    /// <summary>
    /// Reads the local player's aircraft state and produces an <see cref="AircraftStatePacket"/>.
    /// Uses direct API access where compile-time verified; minimal reflection otherwise.
    ///
    /// Reflection budget: ~15 GetValue calls per frame typical, ~22 worst case. Target ≤25.
    /// Legacy FlightGamePatches used 148 reflection calls — this is a 6-10× reduction.
    ///
    /// Direct API (no reflection):
    ///   transform.position/rotation, Rigidbody.velocity/angularVelocity,
    ///   UniAircraft.Engines, .Flaps.AreFlapsDown, .UniPilot, .Fuel,
    ///   .Countermeasures, .FlightDamage, .Radar, Gun2.IsFiring
    ///
    /// Reflection required (~15-22 calls/frame):
    ///   FlightInput pitch/roll/yaw/throttle/afterburner, StickAndRudder fallback,
    ///   Engine throttle/afterburner fields, LandingGear, FlightStats,
    ///   BrakeState, WeaponControls (flare/chaff), FireControl.IsNavMode, NozzleAngle
    /// </summary>
    public class LocalAircraftStateReader
    {
        private readonly FloatingOriginService _originService;
        private uint _sequenceNumber;
        private bool _reflectionInitialized;

        // ── Cached reflection: FlightInput for stick positions ──────────────────
        // REFLECTION REQUIRED: FlightControls/FlightInput are internal fields on UniAircraft/UniPilot
        private FieldInfo _flightControlsField;
        private FieldInfo _flightInputField;
        private PropertyInfo _pitchProp;
        private PropertyInfo _rollProp;
        private PropertyInfo _yawProp;
        private FieldInfo _pitchField;
        private FieldInfo _rollField;
        private FieldInfo _yawField;
        // REFLECTION REQUIRED: FlightInput.Throttle/Afterburner are not compile-time accessible
        private FieldInfo _throttleField;
        private FieldInfo _afterburnerField;
        private PropertyInfo _isAfterburningProp;

        // REFLECTION REQUIRED: StickAndRudder sub-object for raw input fallback
        private FieldInfo _stickAndRudderField;
        private PropertyInfo _sarPitchProp;
        private PropertyInfo _sarRollProp;
        private PropertyInfo _sarYawProp;
        private FieldInfo _sarPitchField;
        private FieldInfo _sarRollField;
        private FieldInfo _sarYawField;

        // REFLECTION REQUIRED: UniEngine.Throttle/Afterburner field accessibility unverified
        private FieldInfo _engineThrottleField;
        private FieldInfo _engineAfterburnerField;

        // REFLECTION REQUIRED: LandingGear is not in the verified public API
        private FieldInfo _landingGearField;
        private PropertyInfo _landingGearProp;
        private PropertyInfo _gearIsDownProp;
        private PropertyInfo _weightOnWheelsProp;

        // REFLECTION REQUIRED: FlightStats/SpeedKIAS not compile-time verified
        private PropertyInfo _flightStatsProp;
        private FieldInfo _flightStatsField;
        private PropertyInfo _speedKIASProp;
        private FieldInfo _speedKIASField;

        // REFLECTION REQUIRED: BrakeState accessibility unknown
        private PropertyInfo _brakeStateProp;
        private FieldInfo _brakeStateField;

        // REFLECTION REQUIRED: WeaponControls for flare/chaff firing state
        private FieldInfo _weaponControlsField;
        private FieldInfo _isFlareFiringField;
        private FieldInfo _isChaffFiringField;

        // REFLECTION REQUIRED: FireControl.IsNavMode not compile-time verified
        private FieldInfo _fireControlField;
        private PropertyInfo _isNavModeProp;

        // REFLECTION REQUIRED: Nozzle angle on engine is not verified public
        private MethodInfo _getTrueNozzleAngleMethod;
        private FieldInfo _vtolNozzleAngleField;
        private PropertyInfo _nozzleAnalogProp;

        // Cached sub-object references (refreshed when the aircraft instance changes)
        private object _cachedFlightInput;
        private object _cachedLandingGear;
        private object _cachedFlightStats;
        private Gun2 _cachedGun;
        private int _cachedAircraftInstanceId;

        public LocalAircraftStateReader(FloatingOriginService originService)
        {
            _originService = originService ?? throw new ArgumentNullException(nameof(originService));
        }

        /// <summary>
        /// Read the current state of the local aircraft and produce a packet.
        /// Called at send rate (e.g., 128Hz).
        /// </summary>
        public AircraftStatePacket ReadState(UniAircraft aircraft, ulong localPeerId)
        {
            return ReadState(aircraft, localPeerId, Time.time);
        }

        public AircraftStatePacket ReadState(UniAircraft aircraft, ulong localPeerId, float timestamp)
        {
            // Auto-reset cached component references when the aircraft instance changes
            // (death + respawn creates a brand-new aircraft). Stale caches silently read
            // frozen state from the destroyed plane — remote players would see this
            // player's gear/flaps/speed locked at their pre-death values forever.
            int instanceId = aircraft.GetInstanceID();
            if (_reflectionInitialized && instanceId != _cachedAircraftInstanceId)
            {
                Log.Info("STATE-READ", "Aircraft instance changed (respawn) — refreshing reflection caches");
                ResetForNewAircraft();
            }

            if (!_reflectionInitialized)
            {
                InitializeReflection(aircraft);
                _cachedAircraftInstanceId = instanceId;
            }

            var rb = aircraft.GetComponent<Rigidbody>();
            var packet = new AircraftStatePacket();
            packet.PlayerId = localPeerId;
            packet.AircraftType = aircraft.Data?.Name ?? "";
            packet.SequenceNumber = _sequenceNumber++;
            packet.Timestamp = timestamp;

            // ── Position (absolute via FloatingOriginService) ── DIRECT API
            Vector3 localPosition = rb != null ? rb.position : aircraft.transform.position;
            _originService.LocalToAbsolute(localPosition,
                out packet.PosX, out packet.PosY, out packet.PosZ);

            // ── Rotation ── DIRECT API
            var rot = rb != null ? rb.rotation : aircraft.transform.rotation;
            packet.RotX = rot.x;
            packet.RotY = rot.y;
            packet.RotZ = rot.z;
            packet.RotW = rot.w;

            // ── Velocity ── DIRECT API
            if (rb != null)
            {
                var vel = rb.velocity;
                packet.VelX = vel.x;
                packet.VelY = vel.y;
                packet.VelZ = vel.z;
                var angVel = rb.angularVelocity;
                packet.AngVelX = angVel.x;
                packet.AngVelY = angVel.y;
                packet.AngVelZ = angVel.z;
            }

            // ── Reflection-based reads ──
            // Each reader is isolated: one intermittently-failing read must NEVER blank
            // out the others. (A shared try/catch here once caused remote landing gear to
            // oscillate: any throw before ReadFlags left the flags byte at its default 0
            // = "gear up", so packets alternated between real and default flag values.)
            TryRead(ReadThrottleAndAfterburner, aircraft, ref packet, "throttle");
            TryRead(ReadControlInputs, aircraft, ref packet, "controls");
            TryRead(ReadSpeedAndBrake, aircraft, ref packet, "speed");
            TryRead(ReadNozzleAngle, aircraft, ref packet, "nozzle");
            TryRead(ReadFlags, aircraft, ref packet, "flags");

            return packet;
        }

        /// <summary>Reset reflection state when aircraft changes (respawn/switch).</summary>
        public void ResetForNewAircraft()
        {
            _reflectionInitialized = false;
            _cachedFlightInput = null;
            _cachedLandingGear = null;
            _cachedFlightStats = null;
            _cachedGun = null;
        }

        private delegate void StateReader(UniAircraft aircraft, ref AircraftStatePacket packet);

        private float _lastReadErrorLogTime;

        /// <summary>
        /// Runs one state reader in isolation so a failure can't prevent later readers
        /// from filling their part of the packet. Failures are logged (throttled) —
        /// silent failures here are how sync bugs hide.
        /// </summary>
        private void TryRead(StateReader reader, UniAircraft aircraft, ref AircraftStatePacket packet, string name)
        {
            try
            {
                reader(aircraft, ref packet);
            }
            catch (Exception ex)
            {
                if (Time.time - _lastReadErrorLogTime > 10f)
                {
                    _lastReadErrorLogTime = Time.time;
                    var inner = ex is TargetInvocationException tie && tie.InnerException != null
                        ? tie.InnerException
                        : ex;
                    Log.Warning("STATE-READ", $"Reader '{name}' failed: {inner.GetType().Name}: {inner.Message}");
                }
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  Per-frame state reading
        // ════════════════════════════════════════════════════════════════

        private void ReadThrottleAndAfterburner(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            bool hasAfterburner = HasAfterburner(aircraft);

            // Try FlightInput first — primary source for player input
            var flightInput = GetLiveFlightInput(aircraft);
            if (flightInput != null)
            {
                // REFLECTION REQUIRED: FlightInput.Throttle is not compile-time accessible
                if (_throttleField != null)
                    packet.Throttle = Convert.ToSingle(_throttleField.GetValue(flightInput));

                if (hasAfterburner)
                {
                    // REFLECTION REQUIRED: FlightInput.IsAfterburning is not compile-time accessible
                    if (_isAfterburningProp != null)
                        packet.Afterburner = Convert.ToBoolean(_isAfterburningProp.GetValue(flightInput));

                    if (!packet.Afterburner && _afterburnerField != null)
                        packet.Afterburner = Convert.ToSingle(_afterburnerField.GetValue(flightInput)) > 0f;
                }
            }

            // Fallback: read from engine if FlightInput didn't provide values
            var engines = aircraft.Engines; // DIRECT API: UniAircraft.Engines is public
            if (engines != null && engines.Count > 0)
            {
                var engine = engines[0];
                if (engine != null)
                {
                    // REFLECTION REQUIRED: UniEngine.Throttle field not compile-time verified
                    if (packet.Throttle == 0f && _engineThrottleField != null)
                        packet.Throttle = Convert.ToSingle(_engineThrottleField.GetValue(engine));

                    // REFLECTION REQUIRED: UniEngine.Afterburner field not compile-time verified
                    if (hasAfterburner && !packet.Afterburner && _engineAfterburnerField != null)
                    {
                        float abValue = Convert.ToSingle(_engineAfterburnerField.GetValue(engine));
                        packet.Afterburner = abValue > 0.1f;
                    }
                }
            }

            if (!hasAfterburner)
                packet.Afterburner = false;
        }

        private void ReadControlInputs(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            // REFLECTION REQUIRED: FlightInput.Pitch/Roll/Yaw are private
            var flightInput = GetLiveFlightInput(aircraft);
            if (flightInput == null) return;

            // Try properties first (more reliable), then fields
            if (_pitchProp != null)
                packet.Pitch = Convert.ToSingle(_pitchProp.GetValue(flightInput));
            else if (_pitchField != null)
                packet.Pitch = Convert.ToSingle(_pitchField.GetValue(flightInput));

            if (_rollProp != null)
                packet.Roll = Convert.ToSingle(_rollProp.GetValue(flightInput));
            else if (_rollField != null)
                packet.Roll = Convert.ToSingle(_rollField.GetValue(flightInput));

            if (_yawProp != null)
                packet.Yaw = Convert.ToSingle(_yawProp.GetValue(flightInput));
            else if (_yawField != null)
                packet.Yaw = Convert.ToSingle(_yawField.GetValue(flightInput));

            // Fallback: StickAndRudder sub-object if primary inputs all zero
            if (packet.Pitch == 0f && packet.Roll == 0f && packet.Yaw == 0f
                && _stickAndRudderField != null)
            {
                // REFLECTION REQUIRED: StickAndRudder is a private sub-object on FlightInput
                var sar = _stickAndRudderField.GetValue(flightInput);
                if (sar != null)
                {
                    if (_sarPitchProp != null)
                        packet.Pitch = Convert.ToSingle(_sarPitchProp.GetValue(sar));
                    else if (_sarPitchField != null)
                        packet.Pitch = Convert.ToSingle(_sarPitchField.GetValue(sar));

                    if (_sarRollProp != null)
                        packet.Roll = Convert.ToSingle(_sarRollProp.GetValue(sar));
                    else if (_sarRollField != null)
                        packet.Roll = Convert.ToSingle(_sarRollField.GetValue(sar));

                    if (_sarYawProp != null)
                        packet.Yaw = Convert.ToSingle(_sarYawProp.GetValue(sar));
                    else if (_sarYawField != null)
                        packet.Yaw = Convert.ToSingle(_sarYawField.GetValue(sar));
                }
            }
        }

        private void ReadSpeedAndBrake(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            // REFLECTION REQUIRED: FlightStats.IndicatedAirspeedKnots not compile-time verified
            if (_cachedFlightStats != null)
            {
                if (_speedKIASProp != null)
                    packet.SpeedKIAS = Convert.ToSingle(_speedKIASProp.GetValue(_cachedFlightStats));
                else if (_speedKIASField != null)
                    packet.SpeedKIAS = Convert.ToSingle(_speedKIASField.GetValue(_cachedFlightStats));
            }

            // REFLECTION REQUIRED: BrakeState accessibility unknown
            if (_brakeStateProp != null)
                packet.BrakeState = Convert.ToSingle(_brakeStateProp.GetValue(aircraft));
            else if (_brakeStateField != null)
                packet.BrakeState = Convert.ToSingle(_brakeStateField.GetValue(aircraft));
        }

        private void ReadNozzleAngle(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            float maxNozzleAngle = GetMaxNozzleAngle(aircraft);
            if (maxNozzleAngle <= 0.01f)
            {
                packet.NozzleAngle = 0f;
                return;
            }

            // DIRECT API: UniAircraft reports the current smoothed VTOL angle.
            packet.NozzleAngle = Mathf.Clamp(aircraft.GetTrueNozzleAngle(), 0f, maxNozzleAngle);
        }

        private static bool HasAfterburner(UniAircraft aircraft)
        {
            var engines = aircraft?.Engines;
            if (engines == null) return false;

            foreach (var engine in engines)
                if (engine != null && engine.HasAfterburner)
                    return true;

            return false;
        }

        private static float GetMaxNozzleAngle(UniAircraft aircraft)
        {
            var engines = aircraft?.Engines;
            if (engines == null) return 0f;

            float max = 0f;
            foreach (var engine in engines)
                if (engine != null && engine.VTOLNozzleMax > max)
                    max = engine.VTOLNozzleMax;

            return max;
        }

        private void ReadFlags(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            // ── Flaps ── DIRECT API: UniAircraft.Flaps.AreFlapsDown is public
            var flaps = aircraft.Flaps;
            if (flaps != null)
                packet.FlapsDown = flaps.AreFlapsDown;

            // ── Gun firing ── DIRECT API: Gun2.IsFiring is a public field
            if (_cachedGun != null)
                packet.IsFiring = _cachedGun.IsFiring;

            // ── Gear down ──
            // REFLECTION REQUIRED: LandingGear.IsGearLowered not compile-time verified
            if (_cachedLandingGear != null && _gearIsDownProp != null)
            {
                try { packet.GearDown = Convert.ToBoolean(_gearIsDownProp.GetValue(_cachedLandingGear)); }
                catch { packet.GearDown = true; }
            }
            else
            {
                packet.GearDown = true; // Safe default: gear down
            }

            // ── Weight on wheels ──
            // REFLECTION REQUIRED: LandingGear.IsWeightOnWheels not compile-time verified
            if (_cachedLandingGear != null && _weightOnWheelsProp != null)
            {
                try { packet.IsWeightOnWheels = Convert.ToBoolean(_weightOnWheelsProp.GetValue(_cachedLandingGear)); }
                catch { packet.IsWeightOnWheels = true; }
            }
            else
            {
                packet.IsWeightOnWheels = true; // Safe default: on ground
            }

            // ── Flare/Chaff firing ──
            ReadCountermeasureState(aircraft, ref packet);

            // ── NavMode (gun safety) ──
            ReadNavMode(aircraft, ref packet);
        }

        private void ReadCountermeasureState(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            // REFLECTION REQUIRED: WeaponControls.IsFlareFiring/IsChaffFiring not compile-time verified
            if (_weaponControlsField == null) return;

            try
            {
                var weaponInput = _weaponControlsField.GetValue(aircraft);
                if (weaponInput == null) return;

                if (_isFlareFiringField != null)
                    packet.IsFlareFiring = Convert.ToBoolean(_isFlareFiringField.GetValue(weaponInput));

                if (_isChaffFiringField != null)
                    packet.IsChaffFiring = Convert.ToBoolean(_isChaffFiringField.GetValue(weaponInput));
            }
            catch { /* Countermeasure state non-critical */ }
        }

        private void ReadNavMode(UniAircraft aircraft, ref AircraftStatePacket packet)
        {
            // REFLECTION REQUIRED: FireControl field and IsNavMode not compile-time verified
            packet.IsNavMode = true; // Default to safe (gun safety on)

            if (_fireControlField == null) return;

            try
            {
                var fc = _fireControlField.GetValue(aircraft);
                if (fc != null && _isNavModeProp != null)
                    packet.IsNavMode = Convert.ToBoolean(_isNavModeProp.GetValue(fc));
            }
            catch { /* NavMode non-critical — default to safe */ }
        }

        // ════════════════════════════════════════════════════════════════
        //  FlightInput helper
        // ════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the live FlightInput object. Re-reads each frame because the reference
        /// can become stale when the pilot/aircraft is recycled.
        /// </summary>
        private object GetLiveFlightInput(UniAircraft aircraft)
        {
            // REFLECTION REQUIRED: FlightControls/FlightInput are internal fields
            // Prefer UniAircraft.FlightControls (reflects actual player stick input)
            if (_flightControlsField != null)
            {
                var fresh = _flightControlsField.GetValue(aircraft);
                if (fresh != null)
                {
                    _cachedFlightInput = fresh;
                    return fresh;
                }
            }

            // Fallback: UniPilot.FlightInput
            if (_flightInputField != null)
            {
                var pilot = aircraft.UniPilot; // DIRECT API: UniAircraft.UniPilot is public
                if (pilot != null)
                {
                    var fresh = _flightInputField.GetValue(pilot);
                    if (fresh != null)
                    {
                        _cachedFlightInput = fresh;
                        return fresh;
                    }
                }
            }

            return _cachedFlightInput;
        }

        // ════════════════════════════════════════════════════════════════
        //  Reflection initialization (one-time per aircraft)
        // ════════════════════════════════════════════════════════════════

        /// <summary>Initialize reflection caches for private/unverified fields.</summary>
        private void InitializeReflection(UniAircraft aircraft)
        {
            if (_reflectionInitialized) return;
            _reflectionInitialized = true;

            var acType = aircraft.GetType();
            var flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            InitFlightInput(aircraft, acType, flags);
            InitEngine(aircraft, flags);
            InitLandingGear(aircraft, acType, flags);
            InitFlightStats(aircraft, acType, flags);
            InitBrake(acType, flags);
            InitWeaponControls(aircraft, acType, flags);
            InitFireControl(aircraft, acType, flags);

            // Cache Gun2 — DIRECT API: Gun2 is a public MonoBehaviour
            _cachedGun = aircraft.GetComponentInChildren<Gun2>();
        }

        private void InitFlightInput(UniAircraft aircraft, Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: FlightControls is not a verified public member
            _flightControlsField = acType.GetField("FlightControls", flags);

            // REFLECTION REQUIRED: UniPilot.FlightInput is not verified
            var pilot = aircraft.UniPilot; // DIRECT API
            if (pilot != null)
            {
                var pilotType = pilot.GetType();
                _flightInputField = pilotType.GetField("FlightInput", flags);
            }

            // Determine FlightInput type from a live object
            object initFlightInput = null;
            if (_flightControlsField != null)
                initFlightInput = _flightControlsField.GetValue(aircraft);
            if (initFlightInput == null && _flightInputField != null && pilot != null)
                initFlightInput = _flightInputField.GetValue(pilot);

            _cachedFlightInput = initFlightInput;
            if (initFlightInput == null) return;

            var inputType = initFlightInput.GetType();

            // REFLECTION REQUIRED: FlightInput.Pitch/Roll/Yaw are private
            _pitchProp = inputType.GetProperty("Pitch", flags);
            _rollProp = inputType.GetProperty("Roll", flags);
            _yawProp = inputType.GetProperty("Yaw", flags)
                ?? inputType.GetProperty("Rudder", flags);

            _pitchField = inputType.GetField("pitch", flags)
                ?? inputType.GetField("Pitch", flags)
                ?? inputType.GetField("_pitch", flags);
            _rollField = inputType.GetField("roll", flags)
                ?? inputType.GetField("Roll", flags)
                ?? inputType.GetField("_roll", flags);
            _yawField = inputType.GetField("yaw", flags)
                ?? inputType.GetField("Yaw", flags)
                ?? inputType.GetField("rudder", flags)
                ?? inputType.GetField("Rudder", flags);

            // REFLECTION REQUIRED: Throttle and afterburner on FlightInput
            _throttleField = inputType.GetField("Throttle", flags);
            _afterburnerField = inputType.GetField("Afterburner", flags);
            _isAfterburningProp = inputType.GetProperty("IsAfterburning", flags);
            _nozzleAnalogProp = inputType.GetProperty("NozzleAnalog", flags);

            // REFLECTION REQUIRED: StickAndRudder sub-object for raw input fallback
            _stickAndRudderField = inputType.GetField("StickAndRudder", flags);
            if (_stickAndRudderField != null)
            {
                var sarObj = _stickAndRudderField.GetValue(initFlightInput);
                if (sarObj != null)
                {
                    var sarType = sarObj.GetType();
                    _sarPitchProp = sarType.GetProperty("Pitch", flags);
                    _sarRollProp = sarType.GetProperty("Roll", flags);
                    _sarYawProp = sarType.GetProperty("Yaw", flags)
                        ?? sarType.GetProperty("Rudder", flags);
                    _sarPitchField = sarType.GetField("Pitch", flags)
                        ?? sarType.GetField("pitch", flags);
                    _sarRollField = sarType.GetField("Roll", flags)
                        ?? sarType.GetField("roll", flags);
                    _sarYawField = sarType.GetField("Yaw", flags)
                        ?? sarType.GetField("yaw", flags)
                        ?? sarType.GetField("Rudder", flags)
                        ?? sarType.GetField("rudder", flags);
                }
            }
        }

        private void InitEngine(UniAircraft aircraft, BindingFlags flags)
        {
            // DIRECT API: UniAircraft.Engines is public
            var engines = aircraft.Engines;
            if (engines == null || engines.Count == 0) return;

            var engine = engines[0];
            if (engine == null) return;

            var engineType = engine.GetType();

            // REFLECTION REQUIRED: UniEngine.Throttle/Afterburner fields not compile-time verified
            _engineThrottleField = engineType.GetField("Throttle", flags);
            _engineAfterburnerField = engineType.GetField("Afterburner", flags);

            // REFLECTION REQUIRED: Nozzle angle methods/fields on engine
            _getTrueNozzleAngleMethod = engineType.GetMethod("GetTrueNozzleAngle",
                BindingFlags.Public | BindingFlags.Instance);
            _vtolNozzleAngleField = engineType.GetField("VTOLNozzleAngle",
                BindingFlags.Public | BindingFlags.Instance);
        }

        private void InitLandingGear(UniAircraft aircraft, Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: LandingGear field on UniAircraft not verified
            _landingGearField = acType.GetField("LandingGear", flags)
                ?? acType.GetField("landingGear", flags)
                ?? acType.GetField("_landingGear", flags);

            if (_landingGearField != null)
                _cachedLandingGear = _landingGearField.GetValue(aircraft);

            // Try property if field not found
            if (_cachedLandingGear == null)
            {
                _landingGearProp = acType.GetProperty("LandingGear", flags)
                    ?? acType.GetProperty("Gear", flags);
                if (_landingGearProp != null)
                    _cachedLandingGear = _landingGearProp.GetValue(aircraft);
            }

            if (_cachedLandingGear == null) return;

            var gearType = _cachedLandingGear.GetType();
            _gearIsDownProp = gearType.GetProperty("IsGearLowered", flags);
            _weightOnWheelsProp = gearType.GetProperty("IsWeightOnWheels", flags);
        }

        private void InitFlightStats(UniAircraft aircraft, Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: FlightStats not in public API
            _flightStatsProp = acType.GetProperty("FlightStats", flags);
            _flightStatsField = acType.GetField("FlightStats", flags);

            if (_flightStatsProp != null)
                _cachedFlightStats = _flightStatsProp.GetValue(aircraft);
            else if (_flightStatsField != null)
                _cachedFlightStats = _flightStatsField.GetValue(aircraft);

            if (_cachedFlightStats == null) return;

            var statsType = _cachedFlightStats.GetType();
            _speedKIASProp = statsType.GetProperty("IndicatedAirspeedKnots", flags);
            _speedKIASField = statsType.GetField("IndicatedAirspeedKnots", flags);
        }

        private void InitBrake(Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: BrakeState accessibility unknown
            _brakeStateProp = acType.GetProperty("BrakeState", flags);
            _brakeStateField = acType.GetField("BrakeState", flags);
        }

        private void InitWeaponControls(UniAircraft aircraft, Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: WeaponControls not in public API
            _weaponControlsField = acType.GetField("WeaponControls", flags);
            if (_weaponControlsField == null) return;

            var wi = _weaponControlsField.GetValue(aircraft);
            if (wi == null) return;

            var wiType = wi.GetType();
            _isFlareFiringField = wiType.GetField("IsFlareFiring", flags);
            _isChaffFiringField = wiType.GetField("IsChaffFiring", flags);
        }

        private void InitFireControl(UniAircraft aircraft, Type acType, BindingFlags flags)
        {
            // REFLECTION REQUIRED: FireControl field on UniAircraft not compile-time verified
            _fireControlField = acType.GetField("FireControl", flags);
            if (_fireControlField == null) return;

            var fc = _fireControlField.GetValue(aircraft);
            if (fc == null) return;

            _isNavModeProp = fc.GetType().GetProperty("IsNavMode", flags);
        }
    }
}
