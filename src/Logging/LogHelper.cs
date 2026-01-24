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
        
        public static void Info(LogCategory category, string message)
        {
            if (!IsEnabled(category)) return;
            Plugin.Log?.LogInfo(message);
        }
        
        public static void InfoInterval(LogCategory category, string message, string key, float intervalSeconds)
        {
            if (!IsEnabled(category)) return;
            if (!ShouldLogInterval(key, intervalSeconds)) return;
            Plugin.Log?.LogInfo(message);
        }
        
        public static void InfoSample(LogCategory category, string message, string key, int sampleRate)
        {
            if (!IsEnabled(category)) return;
            if (!ShouldSample(key, sampleRate)) return;
            Plugin.Log?.LogInfo(message);
        }
    }
}
