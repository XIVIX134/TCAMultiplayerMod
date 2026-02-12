using System.Reflection;

namespace TCAMultiplayer.Networking
{
    /// <summary>
    /// Shared reflection utilities to avoid duplicated lookup patterns
    /// across FlightGamePatches and RemoteAircraftManager.
    /// </summary>
    public static class ReflectionHelper
    {
        private static readonly BindingFlags AllInstance =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

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
