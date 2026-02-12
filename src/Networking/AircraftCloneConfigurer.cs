using System;
using System.Reflection;
using UnityEngine;
using TCAMultiplayer.Player;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Handles cloning aircraft GameObjects and configuring them for multiplayer use.
    /// Extracted from NetworkManager to isolate reflection-heavy configuration code.
    /// </summary>
    public static class AircraftCloneConfigurer
    {
        private const BindingFlags AllInstance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        /// <summary>
        /// Clone an aircraft GameObject and configure it for remote player use.
        /// </summary>
        /// <param name="sourceAircraft">The local aircraft to clone</param>
        /// <param name="peerId">The remote peer ID</param>
        /// <returns>Configured clone, or null if cloning fails</returns>
        public static GameObject CloneAircraft(GameObject sourceAircraft, ulong peerId)
        {
            if (sourceAircraft == null)
            {
                Plugin.Log.LogWarning("[AircraftCloneConfigurer] No source aircraft to clone");
                return null;
            }

            try
            {
                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Cloning aircraft from: {sourceAircraft.name}");

                // Log source FireControl state for debugging
                LogSourceFireControlState(sourceAircraft);

                // Clone the entire GameObject hierarchy
                var clone = GameObject.Instantiate(sourceAircraft);
                clone.name = $"MP_RemoteAircraft_{peerId}";

                // Log clone FireControl state
                LogCloneFireControlState(clone);

                DisableCockpitCam(clone);
                
                // Disable physics and gameplay components
                DisableGameplayComponents(clone);

                // Add remote aircraft controller
                var controller = clone.AddComponent<RemoteAircraftController>();
                controller.PlayerId = peerId;
                controller.Initialize();

                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Successfully cloned aircraft for peer {peerId}");
                return clone;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] Aircraft clone failed: {ex}");
                return null;
            }
        }

        private static void LogSourceFireControlState(GameObject source)
        {
            try
            {
                var fireControlType = Type.GetType("Falcon.Weapons.FireControl, Assembly-CSharp");
                if (fireControlType == null) return;

                var sourceFireControl = source.GetComponentInChildren(fireControlType);
                if (sourceFireControl != null)
                {
                    var gunField = fireControlType.GetField("Gun", BindingFlags.Public | BindingFlags.Instance);
                    var gunValue = gunField?.GetValue(sourceFireControl);
                    Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Source FireControl found, Gun={gunValue != null}");
                    if (gunValue != null)
                    {
                        Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Source Gun type: {gunValue.GetType().FullName}");
                    }
                }
                else
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] Source aircraft has no FireControl!");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AircraftCloneConfigurer] Error logging source FireControl: {ex.Message}");
            }
        }

        private static void LogCloneFireControlState(GameObject clone)
        {
            try
            {
                var fireControlType = Type.GetType("Falcon.Weapons.FireControl, Assembly-CSharp");
                if (fireControlType == null) return;

                var cloneFireControl = clone.GetComponentInChildren(fireControlType);
                if (cloneFireControl != null)
                {
                    var gunField = fireControlType.GetField("Gun", BindingFlags.Public | BindingFlags.Instance);
                    var gunValue = gunField?.GetValue(cloneFireControl);
                    Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Clone FireControl found, Gun={gunValue != null}");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AircraftCloneConfigurer] Error logging clone FireControl: {ex.Message}");
            }
        }

        /// <summary>
        /// Disable physics, AI, and input components on cloned aircraft.
        /// Keeps Target, Damageable, Signature, FireControl for combat.
        /// </summary>
        public static void DisableGameplayComponents(GameObject aircraft)
        {
            try
            {
                // Diagnostic: Log all child GameObjects to find canopy
                LogAircraftHierarchy(aircraft.transform, 0);

                // Disable Rigidbody (make kinematic)
                var rb = aircraft.GetComponent<Rigidbody>();
                if (rb != null)
                {
                    rb.isKinematic = true;
                    rb.useGravity = false;
                    Plugin.Log.LogInfo("[AircraftCloneConfigurer] Disabled Rigidbody physics");
                }

                // Configure colliders - keep body for hit detection, disable gear/wheels
                ConfigureColliders(aircraft);

                // Disable known gameplay components by type name
                // NOTE: We keep Target and Damageable ENABLED for combat!
                // NOTE: We keep FireControl ENABLED so Gun2 gets initialized!
                string[] componentsToDisable = new string[]
                {
                    "UniPilot", "FlightInput", "WeaponInput",
                    "UniAircraftDamage", "UniFlight",
                    "StickAndRudder", "VehicleLauncher"
                };

                // Components to keep enabled for combat
                string[] componentsToKeep = new string[]
                {
                    "Target", "Damageable", "Signature", "FireControl"
                };

                var allComponents = aircraft.GetComponentsInChildren<MonoBehaviour>(true);
                int disabledCount = 0;

                foreach (var comp in allComponents)
                {
                    if (comp == null) continue;

                    string typeName = comp.GetType().Name;

                    // Skip components we want to keep for combat
                    bool shouldKeep = false;
                    foreach (var toKeep in componentsToKeep)
                    {
                        if (typeName.Contains(toKeep))
                        {
                            shouldKeep = true;
                            break;
                        }
                    }
                    if (shouldKeep) continue;

                    // Disable if it's in our list
                    foreach (var toDisable in componentsToDisable)
                    {
                        if (typeName.Contains(toDisable))
                        {
                            comp.enabled = false;
                            disabledCount++;
                            break;
                        }
                    }

                    // Also disable anything that looks like AI or input
                    if (typeName.Contains("AI") || typeName.Contains("Input") ||
                        typeName.Contains("Control") || typeName.Contains("Pilot"))
                    {
                        comp.enabled = false;
                        disabledCount++;
                    }
                }

                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Disabled {disabledCount} gameplay components");

                // Configure targeting system for remote aircraft
                ConfigureTargeting(aircraft);

                // Ensure canopy-related objects are visible
                EnsureCanopyVisible(aircraft);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] Error disabling components: {ex.Message}");
            }
        }

        private static void DisableCockpitCam(GameObject aircraft)
        {
            var cockpitCam = aircraft.transform.Find("Model/CockpitCam");
            if (cockpitCam != null)
            {
                var cam = cockpitCam.GetComponent<Camera>();
                if (cam != null) cam.enabled = false;
                cockpitCam.gameObject.SetActive(false);
            }
        }

        private static void ConfigureColliders(GameObject aircraft)
        {
            var colliders = aircraft.GetComponentsInChildren<Collider>(true);
            int enabledColliders = 0;

            foreach (var col in colliders)
            {
                string colName = col.gameObject.name.ToLower();
                // Keep body/fuselage colliders for hit detection, disable gear/wheel colliders
                if (colName.Contains("wheel") || colName.Contains("gear") || colName.Contains("tire"))
                {
                    col.enabled = false;
                }
                else
                {
                    col.enabled = true;
                    enabledColliders++;
                }
            }

            Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Enabled {enabledColliders}/{colliders.Length} colliders for hit detection");
        }

        /// <summary>
        /// Configure Target component so remote aircraft can be locked and targeted.
        /// CRITICAL: Must set Faction string for TargetManagement.RegisterTarget() to work!
        /// </summary>
        public static void ConfigureTargeting(GameObject aircraft)
        {
            try
            {
                var targetType = Type.GetType("Falcon.Targeting.Target, Assembly-CSharp");
                if (targetType == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] Target type not found - targeting won't work");
                    return;
                }

                var target = aircraft.GetComponentInChildren(targetType) as MonoBehaviour;
                if (target == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] No Target component found on aircraft");
                    return;
                }

                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Found Target component on: {target.gameObject.name}");

                // Get Coalition enum type
                var coalitionEnumType = Type.GetType("Falcon.Factions.Coalition, Assembly-CSharp");
                if (coalitionEnumType == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] Coalition enum type not found");
                    return;
                }

                // Coalition.Red = 1 (enemy)
                object redCoalition = Enum.ToObject(coalitionEnumType, 1);

                // Set TargetType to Fighter (0)
                SetTargetType(targetType, target);

                // CRITICAL: Set Faction string
                SetFaction(targetType, target);

                // Set DefaultCoalition and Coalition to RED (enemy)
                SetCoalition(targetType, target, redCoalition);

                // Set IsDestroyed = false, IsCriticalHP = false
                SetDestroyedFlags(targetType, target);

                // Configure Signature for IR/Radar detection
                ConfigureSignature(targetType, target);

                // CRITICAL: Force re-registration with TargetManagement
                target.enabled = false;
                target.enabled = true;

                // Verify registration
                VerifyTargetRegistration(target);

                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Target component configured for combat");

                // Configure Viewable component for radar/map display
                ConfigureViewable(aircraft);

                // Configure Damageable component
                ConfigureDamageable(aircraft);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] ConfigureTargeting error: {ex.Message}\n{ex.StackTrace}");
            }
        }

        private static void SetTargetType(Type targetType, MonoBehaviour target)
        {
            var targetTypeField = targetType.GetField("TargetType", AllInstance);
            if (targetTypeField != null)
            {
                var targetTypeEnumType = targetTypeField.FieldType;
                object fighterType = Enum.ToObject(targetTypeEnumType, 0); // Fighter = 0
                targetTypeField.SetValue(target, fighterType);
                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set TargetType to Fighter");
            }
        }

        private static void SetFaction(Type targetType, MonoBehaviour target)
        {
            // Try backing field first (auto-property)
            var factionBackingField = targetType.GetField("<Faction>k__BackingField", AllInstance);
            if (factionBackingField != null)
            {
                factionBackingField.SetValue(target, "Enemy");
                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set Faction to 'Enemy'");
            }
            else
            {
                // Try direct field
                var factionField = targetType.GetField("Faction", AllInstance);
                if (factionField != null)
                {
                    factionField.SetValue(target, "Enemy");
                    Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set Faction field to 'Enemy'");
                }
            }
        }

        private static void SetCoalition(Type targetType, MonoBehaviour target, object redCoalition)
        {
            var defaultCoalitionField = targetType.GetField("DefaultCoalition", AllInstance);
            if (defaultCoalitionField != null)
            {
                defaultCoalitionField.SetValue(target, redCoalition);
                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set DefaultCoalition to Red");
            }

            var coalitionBackingField = targetType.GetField("<Coalition>k__BackingField", AllInstance);
            if (coalitionBackingField != null)
            {
                coalitionBackingField.SetValue(target, redCoalition);
                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set Coalition to Red (enemy)");
            }
        }

        private static void SetDestroyedFlags(Type targetType, MonoBehaviour target)
        {
            var isDestroyedField = targetType.GetField("IsDestroyed", AllInstance);
            var isCriticalField = targetType.GetField("IsCriticalHP", AllInstance);
            if (isDestroyedField != null) isDestroyedField.SetValue(target, false);
            if (isCriticalField != null) isCriticalField.SetValue(target, false);
        }

        private static void ConfigureSignature(Type targetType, MonoBehaviour target)
        {
            var signatureField = targetType.GetField("Signature", AllInstance);
            if (signatureField == null) return;

            var signature = signatureField.GetValue(target);
            if (signature == null) return;

            var sigType = signature.GetType();

            // Set engine running for IR signature
            var isEngineRunningField = sigType.GetField("IsEngineRunning", AllInstance);
            if (isEngineRunningField != null)
            {
                isEngineRunningField.SetValue(signature, true);
            }

            // Set throttle for IR signature
            var throttleField = sigType.GetField("EngineThrottle", AllInstance);
            if (throttleField != null)
            {
                throttleField.SetValue(signature, 0.8f);
            }

            // Configure RCS curve for radar detection
            ConfigureRCSCurve(sigType, signature);

            // Configure IR curve for IR missiles
            ConfigureIRCurve(sigType, signature);

            Plugin.Log.LogInfo("[AircraftCloneConfigurer] Configured Signature for IR/Radar detection");
        }

        private static void ConfigureRCSCurve(Type sigType, object signature)
        {
            var rcsCurveProp = sigType.GetProperty("RCSCurve", AllInstance);
            if (rcsCurveProp == null)
            {
                Plugin.Log.LogWarning("[AircraftCloneConfigurer] Signature.RCSCurve property not found");
                return;
            }

            var rcsCurve = rcsCurveProp.GetValue(signature);
            if (rcsCurve == null) return;

            var curveType = rcsCurve.GetType();
            var pointsField = curveType.GetField("Points", BindingFlags.Public | BindingFlags.Instance);
            if (pointsField == null)
            {
                Plugin.Log.LogWarning("[AircraftCloneConfigurer] RCSCurve.Points field not found");
                return;
            }

            var points = pointsField.GetValue(rcsCurve) as System.Collections.IList;
            if (points == null) return;

            int originalCount = points.Count;
            points.Clear();

            // Points are (angle, RCS value) - angle from behind (0=tail, 180=nose)
            var tupleType = typeof(ValueTuple<float, float>);
            points.Add(Activator.CreateInstance(tupleType, 0f, 5f));    // Tail-on
            points.Add(Activator.CreateInstance(tupleType, 45f, 8f));   // Rear quarter
            points.Add(Activator.CreateInstance(tupleType, 90f, 12f));  // Beam (side)
            points.Add(Activator.CreateInstance(tupleType, 135f, 8f));  // Front quarter
            points.Add(Activator.CreateInstance(tupleType, 180f, 5f));  // Nose-on

            Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Configured RCSCurve with {points.Count} points (was {originalCount})");
        }

        private static void ConfigureIRCurve(Type sigType, object signature)
        {
            var irCurveProp = sigType.GetProperty("IRCurve", AllInstance);
            if (irCurveProp == null) return;

            var irCurve = irCurveProp.GetValue(signature);
            if (irCurve == null) return;

            var curveType = irCurve.GetType();
            var pointsField = curveType.GetField("Points", BindingFlags.Public | BindingFlags.Instance);
            if (pointsField == null) return;

            var points = pointsField.GetValue(irCurve) as System.Collections.IList;
            if (points == null || points.Count > 0) return;

            var tupleType = typeof(ValueTuple<float, float>);
            points.Add(Activator.CreateInstance(tupleType, 0f, 10f));   // Tail-on (hot exhaust)
            points.Add(Activator.CreateInstance(tupleType, 45f, 6f));   // Rear quarter
            points.Add(Activator.CreateInstance(tupleType, 90f, 3f));   // Beam
            points.Add(Activator.CreateInstance(tupleType, 135f, 2f));  // Front quarter
            points.Add(Activator.CreateInstance(tupleType, 180f, 1f));  // Nose-on

            Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Configured IRCurve with {points.Count} points");
        }

        private static void VerifyTargetRegistration(MonoBehaviour target)
        {
            try
            {
                var targetManagementType = Type.GetType("Falcon.Targeting.TargetManagement, Assembly-CSharp");
                if (targetManagementType == null) return;

                var allTargetsField = targetManagementType.GetField("AllTargets", BindingFlags.Public | BindingFlags.Static);
                if (allTargetsField == null) return;

                var allTargets = allTargetsField.GetValue(null) as System.Collections.IList;
                if (allTargets == null) return;

                bool isRegistered = allTargets.Contains(target);
                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Target registered in AllTargets: {isRegistered} (count: {allTargets.Count})");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"[AircraftCloneConfigurer] Error verifying registration: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure Damageable component so remote aircraft can take damage.
        /// </summary>
        public static void ConfigureDamageable(GameObject aircraft)
        {
            try
            {
                var damageableType = Type.GetType("Falcon.Damage.Damageable, Assembly-CSharp");
                if (damageableType == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] Damageable type not found");
                    return;
                }

                var damageable = aircraft.GetComponentInChildren(damageableType) as MonoBehaviour;
                if (damageable == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] No Damageable component found");
                    return;
                }

                damageable.enabled = true;

                // Set IsInvincible to false
                var invincibleField = damageableType.GetField("IsInvincible", AllInstance);
                if (invincibleField != null)
                {
                    invincibleField.SetValue(damageable, false);
                }

                // Set DifficultyMultiplier to 1.0
                var difficultyField = damageableType.GetField("DifficultyMultiplier", AllInstance);
                if (difficultyField != null)
                {
                    difficultyField.SetValue(damageable, 1.0f);
                }

                // Ensure IsDestroyed is false
                var isDestroyedProp = damageableType.GetProperty("IsDestroyed", AllInstance);
                if (isDestroyedProp != null && isDestroyedProp.CanWrite)
                {
                    isDestroyedProp.SetValue(damageable, false);
                }
                else
                {
                    var isDestroyedBackingField = damageableType.GetField("<IsDestroyed>k__BackingField", AllInstance);
                    if (isDestroyedBackingField != null)
                    {
                        isDestroyedBackingField.SetValue(damageable, false);
                    }
                }

                // Ensure hitpoints are set
                var maxHPField = damageableType.GetField("maxHP", AllInstance);
                var hitPointsProp = damageableType.GetProperty("HitPoints", AllInstance);
                if (maxHPField != null)
                {
                    int maxHP = (int)maxHPField.GetValue(damageable);
                    if (maxHP <= 0) maxHP = 100;
                    if (hitPointsProp != null)
                    {
                        hitPointsProp.SetValue(damageable, maxHP);
                    }
                    Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Damageable configured with {maxHP} HP");
                }

                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Damageable component configured for combat");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] ConfigureDamageable error: {ex.Message}");
            }
        }

        /// <summary>
        /// Configure Viewable component so remote aircraft appears on radar and map.
        /// </summary>
        public static void ConfigureViewable(GameObject aircraft)
        {
            try
            {
                var viewableType = Type.GetType("Falcon.Viewable, Assembly-CSharp");
                if (viewableType == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] Viewable type not found");
                    return;
                }

                var viewable = aircraft.GetComponentInChildren(viewableType) as MonoBehaviour;
                if (viewable == null)
                {
                    Plugin.Log.LogWarning("[AircraftCloneConfigurer] No Viewable component found");
                    return;
                }

                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Found Viewable on: {viewable.gameObject.name}");

                // Set viewType to Aircraft (0)
                var viewTypeField = viewableType.GetField("viewType", AllInstance);
                if (viewTypeField != null)
                {
                    viewTypeField.SetValue(viewable, 0); // ViewType.Aircraft
                    Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set Viewable viewType to Aircraft");
                }

                // Set AutoPopulateStats to true
                var autoPopulateField = viewableType.GetField("AutoPopulateStats", AllInstance);
                if (autoPopulateField != null)
                {
                    autoPopulateField.SetValue(viewable, true);
                    Plugin.Log.LogInfo("[AircraftCloneConfigurer] Set AutoPopulateStats to true");
                }

                // Force re-registration
                viewable.enabled = false;
                viewable.enabled = true;

                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Viewable configured for radar/map");
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] ConfigureViewable error: {ex.Message}");
            }
        }

        /// <summary>
        /// Log aircraft hierarchy to find canopy and other parts.
        /// </summary>
        public static void LogAircraftHierarchy(Transform parent, int depth)
        {
            if (depth > 6) return;

            string indent = new string(' ', depth * 2);
            var renderer = parent.GetComponent<Renderer>();
            var meshFilter = parent.GetComponent<MeshFilter>();
            bool hasVisual = renderer != null || meshFilter != null;
            bool isActive = parent.gameObject.activeSelf;

            string name = parent.name.ToLower();
            bool isCanopyRelated = name.Contains("canopy") || name.Contains("glass") ||
                                   name.Contains("cockpit") || name.Contains("window") ||
                                   name.Contains("bubble") || name.Contains("hood") ||
                                   name.Contains("hud") || name.Contains("transparency");

            if (depth <= 2 || hasVisual || isCanopyRelated)
            {
                string status = isActive ? "ACTIVE" : "INACTIVE";
                string visual = hasVisual ? "[VISUAL]" : "";
                string canopy = isCanopyRelated ? "[CANOPY?]" : "";
                Plugin.Log.LogInfo($"[Hierarchy]{indent}{parent.name} {status} {visual} {canopy}");

                if (isCanopyRelated && renderer != null)
                {
                    Plugin.Log.LogInfo($"[Hierarchy]{indent}  -> Renderer enabled: {renderer.enabled}, material: {renderer.material?.name}");
                }
            }

            foreach (Transform child in parent)
            {
                LogAircraftHierarchy(child, depth + 1);
            }
        }

        /// <summary>
        /// Ensure canopy and related visual elements are visible on cloned aircraft.
        /// </summary>
        public static void EnsureCanopyVisible(GameObject aircraft)
        {
            try
            {
                var allTransforms = aircraft.GetComponentsInChildren<Transform>(true);
                int canopyFixCount = 0;

                foreach (var t in allTransforms)
                {
                    string name = t.name.ToLower();

                    bool isCanopyRelated = name.Contains("canopy") || name.Contains("glass") ||
                                           name.Contains("cockpit") || name.Contains("window") ||
                                           name.Contains("bubble") || name.Contains("hood") ||
                                           name.Contains("transparency") || name.Contains("windshield") ||
                                           name.Contains("hud") || name.Contains("visor");

if (isCanopyRelated && !name.Equals("cockpitcam"))
                    {
                        Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Found canopy object: {t.name}, active={t.gameObject.activeSelf}");

                        // Ensure GameObject is active
                        if (!t.gameObject.activeSelf)
                        {
                            t.gameObject.SetActive(true);
                            Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Activated canopy: {t.name}");
                            canopyFixCount++;
                        }

                        // Ensure all parent objects are active too
                        Transform parent = t.parent;
                        while (parent != null && parent != aircraft.transform)
                        {
                            if (!parent.gameObject.activeSelf)
                            {
                                parent.gameObject.SetActive(true);
                                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Activated canopy parent: {parent.name}");
                                canopyFixCount++;
                            }
                            parent = parent.parent;
                        }

                        // Ensure renderer is enabled
                        var renderer = t.GetComponent<Renderer>();
                        if (renderer != null)
                        {
                            if (!renderer.enabled)
                            {
                                renderer.enabled = true;
                                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Enabled canopy renderer: {t.name}");
                                canopyFixCount++;
                            }
                            Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Canopy material: {renderer.material?.name}, shader: {renderer.material?.shader?.name}");
                        }

                        // Check all renderers in children too
                        var childRenderers = t.GetComponentsInChildren<Renderer>(true);
                        foreach (var r in childRenderers)
                        {
                            if (r != null && !r.enabled)
                            {
                                r.enabled = true;
                                Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Enabled canopy child renderer: {r.name}");
                                canopyFixCount++;
                            }
                        }
                    }
                }

                // Force LOD0 (highest detail)
                ForceLOD0(aircraft);

                if (canopyFixCount > 0)
                {
                    Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Fixed {canopyFixCount} canopy visibility issues");
                }
                else
                {
                    Plugin.Log.LogInfo("[AircraftCloneConfigurer] No canopy visibility issues found");
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[AircraftCloneConfigurer] EnsureCanopyVisible error: {ex.Message}");
            }
        }

        private static void ForceLOD0(GameObject aircraft)
        {
            var lodGroup = aircraft.GetComponent<LODGroup>();
            if (lodGroup != null)
            {
                Plugin.Log.LogInfo("[AircraftCloneConfigurer] Found LODGroup, forcing LOD0");
                lodGroup.ForceLOD(0);
            }

            var childLodGroups = aircraft.GetComponentsInChildren<LODGroup>(true);
            foreach (var lod in childLodGroups)
            {
                if (lod != null)
                {
                    lod.ForceLOD(0);
                    Plugin.Log.LogInfo($"[AircraftCloneConfigurer] Forced LOD0 on: {lod.gameObject.name}");
                }
            }
        }
    }
}
