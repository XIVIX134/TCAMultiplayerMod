using System;
using System.Reflection;
using UnityEngine;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Utility to access the game's FloatingOrigin system
    /// The game shifts all objects periodically to keep the player near origin
    /// We need to convert between local and world-absolute coordinates
    /// </summary>
    public static class FloatingOriginHelper
    {
        private static Type _floatingOriginType;
        private static object _floatingOriginInstance;
        private static PropertyInfo _totalOffsetProperty;
        private static bool _initialized = false;
        private static bool _failed = false;
        
        // Cached offset - updated each frame
        private static Vector3d _cachedTotalOffset = Vector3d.zero;
        private static Vector3d _previousOffset = Vector3d.zero;
        private static float _lastUpdateTime = -1f;
        
        // Property for ReferenceObject
        private static FieldInfo _referenceObjectField;

        /// <summary>
        /// The total offset applied by FloatingOrigin since game start
        /// This is how far the origin has been shifted from true world (0,0,0)
        /// </summary>
        public static Vector3d TotalOffset
        {
            get
            {
                UpdateCachedOffset();
                return _cachedTotalOffset;
            }
        }
        
        /// <summary>
        /// Whether FloatingOrigin was found and is available
        /// </summary>
        public static bool IsAvailable => Initialize();
        
        private static bool Initialize()
        {
            if (_initialized) return !_failed;
            if (_failed) return false;
            
            try
            {
                // Find Falcon.World.FloatingOrigin type
                _floatingOriginType = Type.GetType("Falcon.World.FloatingOrigin, Assembly-CSharp");
                
                if (_floatingOriginType == null)
                {
                    // Try to find it in loaded assemblies
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        _floatingOriginType = asm.GetType("Falcon.World.FloatingOrigin");
                        if (_floatingOriginType != null) break;
                    }
                }
                
                if (_floatingOriginType == null)
                {
                    Plugin.Log?.LogWarning("[FloatingOriginHelper] Could not find FloatingOrigin type");
                    _failed = true;
                    return false;
                }
                
                Plugin.Log?.LogInfo($"[FloatingOriginHelper] Found type: {_floatingOriginType.FullName}");
                
                // Look for TotalOffset property (likely a Vector3d or similar)
                _totalOffsetProperty = _floatingOriginType.GetProperty("TotalOffset", 
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance);
                
                if (_totalOffsetProperty == null)
                {
                    // Try to find any offset-related field/property
                    foreach (var prop in _floatingOriginType.GetProperties(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    {
                        Plugin.Log?.LogInfo($"[FloatingOriginHelper] Property: {prop.Name} ({prop.PropertyType.Name})");
                        if (prop.Name.Contains("Offset") || prop.Name.Contains("Origin"))
                        {
                            _totalOffsetProperty = prop;
                        }
                    }
                    
                    foreach (var field in _floatingOriginType.GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance))
                    {
                        Plugin.Log?.LogInfo($"[FloatingOriginHelper] Field: {field.Name} ({field.FieldType.Name})");
                    }
                }
                
                // Try to find singleton instance
                var instanceProp = _floatingOriginType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp != null)
                {
                    _floatingOriginInstance = instanceProp.GetValue(null);
                    Plugin.Log?.LogInfo("[FloatingOriginHelper] Found Instance property");
                }
                else
                {
                    // Try to find via FindObjectOfType
                    if (typeof(UnityEngine.Object).IsAssignableFrom(_floatingOriginType))
                    {
                        _floatingOriginInstance = UnityEngine.Object.FindObjectOfType(_floatingOriginType);
                        Plugin.Log?.LogInfo($"[FloatingOriginHelper] Found via FindObjectOfType: {_floatingOriginInstance != null}");
                    }
                }
                
                // Get ReferenceObject field
                _referenceObjectField = _floatingOriginType.GetField("ReferenceObject", BindingFlags.Public | BindingFlags.Instance);
                
                _initialized = true;
                Plugin.Log?.LogInfo($"[FloatingOriginHelper] Initialized. TotalOffsetProperty: {_totalOffsetProperty?.Name ?? "null"}");
                
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FloatingOriginHelper] Init error: {ex}");
                _failed = true;
                return false;
            }
        }
        
        private static void UpdateCachedOffset()
        {
            if (!Initialize()) return;
            
            try
            {
                // Try to refresh instance each time in case it was created later
                if (_floatingOriginInstance == null && typeof(UnityEngine.Object).IsAssignableFrom(_floatingOriginType))
                {
                    _floatingOriginInstance = UnityEngine.Object.FindObjectOfType(_floatingOriginType);
                }
                
                if (_totalOffsetProperty != null)
                {
                    object instance = _totalOffsetProperty.GetGetMethod().IsStatic ? null : _floatingOriginInstance;
                    var value = _totalOffsetProperty.GetValue(instance);
                    
                    if (value != null)
                    {
                        // Could be Vector3, Vector3d, or custom type
                        _previousOffset = _cachedTotalOffset;
                        _cachedTotalOffset = ConvertToVector3d(value);
                        
                        // Detect and log FloatingOrigin shifts
                        double dx = _cachedTotalOffset.x - _previousOffset.x;
                        double dy = _cachedTotalOffset.y - _previousOffset.y;
                        double dz = _cachedTotalOffset.z - _previousOffset.z;
                        double shiftSq = dx * dx + dy * dy + dz * dz;
                        if (shiftSq > 1.0) // > 1m shift
                        {
                            Plugin.Log?.LogInfo($"[FloatingOriginHelper] Origin shifted! delta=({dx:F1},{dy:F1},{dz:F1}) new offset={_cachedTotalOffset}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Silently fail after first warning
                Plugin.Log?.LogWarning($"[FloatingOriginHelper] Update error: {ex.Message}");
            }
        }
        
        private static Vector3d ConvertToVector3d(object value)
        {
            if (value is Vector3 v3)
            {
                return new Vector3d(v3.x, v3.y, v3.z);
            }
            
            // Try to read x, y, z properties/fields via reflection
            var type = value.GetType();
            
            double x = 0, y = 0, z = 0;
            
            var xProp = type.GetProperty("x") ?? type.GetProperty("X");
            var yProp = type.GetProperty("y") ?? type.GetProperty("Y");
            var zProp = type.GetProperty("z") ?? type.GetProperty("Z");
            
            if (xProp != null) x = Convert.ToDouble(xProp.GetValue(value));
            if (yProp != null) y = Convert.ToDouble(yProp.GetValue(value));
            if (zProp != null) z = Convert.ToDouble(zProp.GetValue(value));
            
            if (xProp == null)
            {
                var xField = type.GetField("x") ?? type.GetField("X");
                var yField = type.GetField("y") ?? type.GetField("Y");
                var zField = type.GetField("z") ?? type.GetField("Z");
                
                if (xField != null) x = Convert.ToDouble(xField.GetValue(value));
                if (yField != null) y = Convert.ToDouble(yField.GetValue(value));
                if (zField != null) z = Convert.ToDouble(zField.GetValue(value));
            }
            
            return new Vector3d(x, y, z);
        }
        
        /// <summary>
        /// Convert a local position to world-absolute position
        /// </summary>
        public static Vector3d LocalToAbsolute(Vector3 localPos)
        {
            try
            {
                var offset = TotalOffset;
                return new Vector3d(
                    localPos.x + offset.x,
                    localPos.y + offset.y,
                    localPos.z + offset.z
                );
            }
            catch
            {
                // If anything fails, just return the local position as-is
                return new Vector3d(localPos.x, localPos.y, localPos.z);
            }
        }
        
        /// <summary>
        /// Convert a world-absolute position to local position
        /// </summary>
        public static Vector3 AbsoluteToLocal(Vector3d absolutePos)
        {
            try
            {
                var offset = TotalOffset;
                return new Vector3(
                    (float)(absolutePos.x - offset.x),
                    (float)(absolutePos.y - offset.y),
                    (float)(absolutePos.z - offset.z)
                );
            }
            catch
            {
                // If anything fails, just return the position as-is
                return new Vector3((float)absolutePos.x, (float)absolutePos.y, (float)absolutePos.z);
            }
        }
        
        /// <summary>
        /// Manually set the ReferenceObject for the FloatingOrigin system
        /// Ensures floating point precision is maintained when spawning new local aircraft
        /// </summary>
        public static void SetReferenceObject(Transform referenceTransform)
        {
            try
            {
                if (!Initialize() || _floatingOriginInstance == null || _referenceObjectField == null)
                {
                    Plugin.Log?.LogWarning("[FloatingOriginHelper] Cannot set ReferenceObject - FloatingOrigin unavailable");
                    return;
                }
                
                _referenceObjectField.SetValue(_floatingOriginInstance, referenceTransform);
                Plugin.Log?.LogInfo($"[FloatingOriginHelper] Successfully set ReferenceObject to {(referenceTransform != null ? referenceTransform.name : "null")}");
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogError($"[FloatingOriginHelper] Failed to set ReferenceObject: {ex.Message}");
            }
        }
    }
    
    /// <summary>
    /// Double-precision 3D vector for large world coordinates
    /// </summary>
    public struct Vector3d
    {
        public double x, y, z;
        
        public static readonly Vector3d zero = new Vector3d(0, 0, 0);
        
        public Vector3d(double x, double y, double z)
        {
            this.x = x;
            this.y = y;
            this.z = z;
        }
        
        public Vector3 ToVector3() => new Vector3((float)x, (float)y, (float)z);
        
        public override string ToString() => $"({x:F1}, {y:F1}, {z:F1})";
    }
}
