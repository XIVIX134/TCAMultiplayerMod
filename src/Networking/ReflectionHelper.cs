using System;
using System.Reflection;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Shared reflection utilities to avoid duplicated lookup patterns
    /// across FlightGamePatches and RemoteAircraftManager.
    /// 
    /// Provides robust reflection with multiple fallback patterns for accessing
    /// private fields and properties that may have compiler-generated names.
    /// </summary>
    public static class ReflectionHelper
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static readonly BindingFlags AllStatic =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static;

        private static readonly BindingFlags AllFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        #region Robust Backing Field Resolution

        /// <summary>
        /// Gets a field that backs a property, trying multiple naming conventions.
        /// This is more robust than assuming compiler-generated backing field names.
        /// 
        /// Tries in order:
        /// 1. &lt;PropertyName&gt;k__BackingField (compiler-generated)
        /// 2. _propertyName (common convention)
        /// 3. propertyName (direct field)
        /// 4. m_propertyName (Hungarian notation)
        /// 5. Any field with matching type (last resort)
        /// </summary>
        /// <param name="type">The type to search</param>
        /// <param name="propertyName">The property name whose backing field we want</param>
        /// <param name="flags">Binding flags for the search</param>
        /// <returns>The FieldInfo if found, null otherwise</returns>
        public static FieldInfo GetBackingField(Type type, string propertyName, BindingFlags flags)
        {
            if (type == null || string.IsNullOrEmpty(propertyName))
                return null;

            // 1. Try compiler-generated backing field name
            var field = type.GetField($"<{propertyName}>k__BackingField", flags);
            if (field != null)
            {
                Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via <X>k__BackingField pattern");
                return field;
            }

            // 2. Try underscore prefix (common convention)
            field = type.GetField($"_{propertyName}", flags);
            if (field != null)
            {
                Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via _X pattern");
                return field;
            }

            // 3. Try underscore prefix with camelCase
            string camelCase = char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
            field = type.GetField($"_{camelCase}", flags);
            if (field != null)
            {
                Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via _x pattern");
                return field;
            }

            // 4. Try direct field name (camelCase)
            field = type.GetField(camelCase, flags);
            if (field != null)
            {
                Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via direct field name");
                return field;
            }

            // 5. Try m_ prefix (Hungarian notation)
            field = type.GetField($"m_{propertyName}", flags);
            if (field != null)
            {
                Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via m_X pattern");
                return field;
            }

            // 6. Last resort: find any non-static field with the property's type
            var property = type.GetProperty(propertyName, flags);
            if (property != null)
            {
                foreach (var f in type.GetFields(flags))
                {
                    if (f.FieldType == property.PropertyType && !f.IsStatic)
                    {
                        Plugin.Log?.LogDebug($"[ReflectionHelper] Found backing field for {propertyName} via type matching: {f.Name}");
                        return f;
                    }
                }
            }

            Plugin.Log?.LogWarning($"[ReflectionHelper] Could not find backing field for property {propertyName} on type {type.Name}");
            return null;
        }

        /// <summary>
        /// Gets a property value using robust fallback patterns.
        /// Tries direct property access first, then backing field.
        /// </summary>
        public static object GetPropertyValue(object instance, string propertyName)
        {
            if (instance == null) return null;

            var type = instance.GetType();

            // Try property first
            var prop = type.GetProperty(propertyName, AllFlags);
            if (prop != null && prop.CanRead)
            {
                return prop.GetValue(instance);
            }

            // Try backing field
            var field = GetBackingField(type, propertyName, AllInstance);
            if (field != null)
            {
                return field.GetValue(instance);
            }

            return null;
        }

        /// <summary>
        /// Sets a property value using robust fallback patterns.
        /// Tries direct property access first, then backing field.
        /// </summary>
        public static bool SetPropertyValue(object instance, string propertyName, object value)
        {
            if (instance == null) return false;

            var type = instance.GetType();

            // Try property first
            var prop = type.GetProperty(propertyName, AllFlags);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(instance, value);
                return true;
            }

            // Try backing field
            var field = GetBackingField(type, propertyName, AllInstance);
            if (field != null)
            {
                field.SetValue(instance, value);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Gets a field value, trying multiple naming conventions.
        /// </summary>
        public static object GetFieldValue(object instance, string fieldName)
        {
            if (instance == null) return null;

            var type = instance.GetType();
            var field = type.GetField(fieldName, AllInstance);
            return field?.GetValue(instance);
        }

        /// <summary>
        /// Sets a field value, trying multiple naming conventions.
        /// </summary>
        public static bool SetFieldValue(object instance, string fieldName, object value)
        {
            if (instance == null) return false;

            var type = instance.GetType();
            var field = type.GetField(fieldName, AllInstance);
            if (field != null)
            {
                field.SetValue(instance, value);
                return true;
            }

            return false;
        }

        #endregion

        #region Type Resolution Helpers

        /// <summary>
        /// Gets a type from Assembly-CSharp by full name.
        /// Caches the result for performance.
        /// </summary>
        private static readonly System.Collections.Generic.Dictionary<string, Type> _typeCache = 
            new System.Collections.Generic.Dictionary<string, Type>();

        public static Type GetGameType(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) return null;

            if (_typeCache.TryGetValue(fullName, out Type cached))
                return cached;

            var type = Type.GetType($"{fullName}, Assembly-CSharp");
            if (type == null)
            {
                // Try without namespace
                var lastDot = fullName.LastIndexOf('.');
                if (lastDot > 0)
                {
                    var shortName = fullName.Substring(lastDot + 1);
                    type = Type.GetType($"{fullName}, Assembly-CSharp");
                }
            }

            if (type != null)
            {
                _typeCache[fullName] = type;
            }
            else
            {
                Plugin.Log?.LogWarning($"[ReflectionHelper] Could not resolve type: {fullName}");
            }

            return type;
        }

        /// <summary>
        /// Gets a method from a type with parameter type checking.
        /// </summary>
        public static MethodInfo GetMethod(Type type, string methodName, Type[] parameterTypes = null)
        {
            if (type == null || string.IsNullOrEmpty(methodName)) return null;

            if (parameterTypes == null || parameterTypes.Length == 0)
            {
                return type.GetMethod(methodName, AllFlags);
            }

            return type.GetMethod(methodName, AllFlags, null, parameterTypes, null);
        }

        /// <summary>
        /// Gets a static field value from a type.
        /// </summary>
        public static object GetStaticFieldValue(Type type, string fieldName)
        {
            if (type == null || string.IsNullOrEmpty(fieldName)) return null;

            var field = type.GetField(fieldName, AllStatic);
            return field?.GetValue(null);
        }

        #endregion

        /// <summary>
        /// Extract the aircraft type name from a UniAircraft component's Data field.
        /// Tries: Data.Name (field) → Data.Name/InternalName (property) → Data.RWRCodes (field).
        /// Returns null if nothing found.
        /// </summary>
        public static string GetAircraftNameFromData(object uniAircraft)
        {
            if (uniAircraft == null) return null;

            var dataField = uniAircraft.GetType().GetField("Data", AllInstance);
            if (dataField == null) return null;

            var data = dataField.GetValue(uniAircraft);
            if (data == null) return null;

            // Public field Name (UniAircraftData.Name is a field, not a property)
            var nameField = data.GetType().GetField("Name", BindingFlags.Public | BindingFlags.Instance);
            var value = nameField?.GetValue(data) as string;
            if (!string.IsNullOrEmpty(value)) return value;

            // Fallback to properties
            var nameProp = data.GetType().GetProperty("Name")
                        ?? data.GetType().GetProperty("InternalName");
            value = nameProp?.GetValue(data) as string;
            if (!string.IsNullOrEmpty(value)) return value;

            // Last resort: RWR codes
            var rwrCodesField = data.GetType().GetField("RWRCodes", AllInstance);
            value = rwrCodesField?.GetValue(data) as string;
            if (!string.IsNullOrEmpty(value)) return value;

            return null;
        }

        /// <summary>
        /// Maps a GameObject name or string to a known aircraft type identifier.
        /// Returns null if no match found.
        /// </summary>
        public static string MapAircraftNameFromString(string value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            string lower = value.ToLowerInvariant();

            if (lower.Contains("av8b") || lower.Contains("av-8b") || lower.Contains("harrier")) return "AV8B";
            if (lower.Contains("f16") || lower.Contains("f-16")) return "F16C";
            if (lower.Contains("f18") || lower.Contains("f-18") || lower.Contains("f/a-18")) return "F18C";
            if (lower.Contains("f15") || lower.Contains("f-15")) return "F15C";
            if (lower.Contains("su27") || lower.Contains("su-27")) return "Su27";
            if (lower.Contains("mig29") || lower.Contains("mig-29")) return "MiG29";

            return null;
        }
    }
}
