using System;
using System.Collections.Generic;
using UnityEngine;

namespace TCAMultiplayer
{
    public enum LogCategory
    {
        General,
        Network,
        Transport,
        Packets,
        Patches,
        Player,
        Damage,
        Weapon,
        Interpolation,
        Reflection
    }
    
    public static class LogHelper
    {
        private static readonly Dictionary<string, float> LastLogTimes = new Dictionary<string, float>();
        private static readonly Dictionary<string, int> Counters = new Dictionary<string, int>();
        
        public static float DefaultIntervalSeconds => Plugin.LogIntervalSeconds?.Value ?? 1f;
        public static int PacketSampleRate => Plugin.PacketLogSampleRate?.Value ?? 60;
        public static int HighFreqSampleRate => Plugin.HighFreqLogSampleRate?.Value ?? 30;
        
        public static bool IsEnabled(LogCategory category)
        {
            if (Plugin.VerboseAll?.Value ?? false) return true;
            
            switch (category)
            {
                case LogCategory.Network:
                    return Plugin.VerboseNetworking?.Value ?? false;
                case LogCategory.Transport:
                    return Plugin.VerboseTransport?.Value ?? false;
                case LogCategory.Packets:
                    return Plugin.VerbosePackets?.Value ?? false;
                case LogCategory.Patches:
                    return Plugin.VerbosePatches?.Value ?? false;
                case LogCategory.Player:
                    return Plugin.VerbosePlayer?.Value ?? false;
                case LogCategory.Damage:
                    return Plugin.VerboseDamage?.Value ?? false;
                case LogCategory.Weapon:
                    return Plugin.VerboseWeapons?.Value ?? false;
                case LogCategory.Interpolation:
                    return Plugin.VerboseInterpolation?.Value ?? false;
                case LogCategory.Reflection:
                    return Plugin.VerboseReflection?.Value ?? false;
                default:
                    return false;
            }
        }
        
        public static bool ShouldLogInterval(string key, float intervalSeconds)
        {
            if (intervalSeconds <= 0f) return true;
            
            float now = Time.time;
            if (!LastLogTimes.TryGetValue(key, out float last) || now - last >= intervalSeconds)
            {
                LastLogTimes[key] = now;
                return true;
            }
            
            return false;
        }
        
        public static bool ShouldSample(string key, int sampleRate)
        {
            if (sampleRate <= 1) return true;
            
            if (!Counters.TryGetValue(key, out int count))
            {
                count = 0;
            }
            
            count++;
            Counters[key] = count;
            return (count % sampleRate) == 0;
        }
        
        /// <summary>
        /// Get a short category tag for log messages
        /// </summary>
        private static string GetCategoryTag(LogCategory category)
        {
            return category switch
            {
                LogCategory.Network => "NET",
                LogCategory.Transport => "TRANS",
                LogCategory.Packets => "PKT",
                LogCategory.Patches => "PATCH",
                LogCategory.Player => "PLAYER",
                LogCategory.Damage => "DMG",
                LogCategory.Weapon => "WPN",
                LogCategory.Interpolation => "INTERP",
                LogCategory.Reflection => "REFL",
                _ => "GEN"
            };
        }
        
        /// <summary>
        /// Format a message with instance prefix for BepInEx
        /// </summary>
        private static string FormatForBepInEx(LogCategory category, string message)
        {
            string instancePrefix = InstanceLogger.GetPrefix();
            string categoryTag = GetCategoryTag(category);
            return $"{instancePrefix}[{categoryTag}] {message}";
        }
        
        /// <summary>
        /// Format a message for the instance log file (cleaner format)
        /// </summary>
        private static string FormatForFile(LogCategory category, string message)
        {
            string categoryTag = GetCategoryTag(category);
            return $"[{categoryTag}] {message}";
        }
        
        public static void Info(LogCategory category, string message)
        {
            if (!IsEnabled(category)) return;
            
            // Write to instance log file (clean format)
            InstanceLogger.LogInfo(FormatForFile(category, message));
            
            // Also log to BepInEx with prefix
            Plugin.Log?.LogInfo(FormatForBepInEx(category, message));
        }
        
        public static void InfoInterval(LogCategory category, string message, string key, float intervalSeconds)
        {
            if (!IsEnabled(category)) return;
            if (!ShouldLogInterval(key, intervalSeconds)) return;
            
            InstanceLogger.LogInfo(FormatForFile(category, message));
            Plugin.Log?.LogInfo(FormatForBepInEx(category, message));
        }
        
        public static void InfoSample(LogCategory category, string message, string key, int sampleRate)
        {
            if (!IsEnabled(category)) return;
            if (!ShouldSample(key, sampleRate)) return;
            
            InstanceLogger.LogInfo(FormatForFile(category, message));
            Plugin.Log?.LogInfo(FormatForBepInEx(category, message));
        }
        
        /// <summary>
        /// Log a warning message
        /// </summary>
        public static void Warning(LogCategory category, string message)
        {
            if (!IsEnabled(category)) return;
            
            InstanceLogger.LogWarning(FormatForFile(category, message));
            Plugin.Log?.LogWarning(FormatForBepInEx(category, message));
        }
        
        /// <summary>
        /// Log an error message (always logged regardless of category settings)
        /// </summary>
        public static void Error(LogCategory category, string message)
        {
            InstanceLogger.LogError(FormatForFile(category, message));
            Plugin.Log?.LogError(FormatForBepInEx(category, message));
        }
        
        /// <summary>
        /// Log a network send event with special formatting
        /// </summary>
        public static void NetworkSend(string details)
        {
            if (!IsEnabled(LogCategory.Network)) return;
            InstanceLogger.LogNetwork("SEND", details);
        }
        
        /// <summary>
        /// Log a network receive event with special formatting
        /// </summary>
        public static void NetworkRecv(string details)
        {
            if (!IsEnabled(LogCategory.Network)) return;
            InstanceLogger.LogNetwork("RECV", details);
        }
        
        /// <summary>
        /// Log a state transition with visual emphasis
        /// </summary>
        public static void StateChange(string fromState, string toState)
        {
            InstanceLogger.LogStateChange(fromState, toState);
            Plugin.Log?.LogInfo($"{InstanceLogger.GetPrefix()}STATE: {fromState} -> {toState}");
        }
    }
}
