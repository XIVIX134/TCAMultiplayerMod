using System;
using System.Collections.Generic;
using TCAMultiplayer.Networking;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Tracks kills and deaths for all players across the multiplayer session.
    /// Singleton accessible via ScoreTracker.Instance.
    /// </summary>
    public class ScoreTracker
    {
        public static ScoreTracker Instance { get; private set; }

        /// <summary>
        /// Per-player score data.
        /// </summary>
        public class PlayerScore
        {
            public ulong PeerId { get; set; }
            public string PlayerName { get; set; }
            public int Kills { get; set; }
            public int Deaths { get; set; }

            public PlayerScore(ulong peerId, string playerName)
            {
                PeerId = peerId;
                PlayerName = playerName;
            }
        }

        private readonly Dictionary<ulong, PlayerScore> _scores = new Dictionary<ulong, PlayerScore>();
        private readonly object _lock = new object();

        /// <summary>
        /// Recent kill-feed entries (killer -> victim, weapon) for HUD display.
        /// </summary>
        public class KillFeedEntry
        {
            public string KillerName;
            public string VictimName;
            public string WeaponName;
            public float Timestamp; // Time.time when the kill happened
        }

        private readonly List<KillFeedEntry> _killFeed = new List<KillFeedEntry>();
        private const int MAX_KILL_FEED = 5;
        private const float KILL_FEED_DURATION = 8f;

        public ScoreTracker()
        {
            Instance = this;
        }

        /// <summary>
        /// Register a player (call when they join or at game start).
        /// </summary>
        public void RegisterPlayer(ulong peerId, string playerName)
        {
            lock (_lock)
            {
                if (!_scores.ContainsKey(peerId))
                {
                    _scores[peerId] = new PlayerScore(peerId, playerName);
                }
                else
                {
                    _scores[peerId].PlayerName = playerName;
                }
            }
        }

        /// <summary>
        /// Record a kill. Increments killer's kills and victim's deaths.
        /// </summary>
        public void RecordKill(ulong killerId, ulong victimId, string weaponName)
        {
            string killerName = $"Player {killerId}";
            string victimName = $"Player {victimId}";

            lock (_lock)
            {
                if (_scores.TryGetValue(killerId, out var killerScore))
                {
                    killerScore.Kills++;
                    killerName = killerScore.PlayerName;
                }
                else
                {
                    var score = new PlayerScore(killerId, killerName);
                    score.Kills = 1;
                    _scores[killerId] = score;
                }

                if (_scores.TryGetValue(victimId, out var victimScore))
                {
                    victimScore.Deaths++;
                    victimName = victimScore.PlayerName;
                }
                else
                {
                    var score = new PlayerScore(victimId, victimName);
                    score.Deaths = 1;
                    _scores[victimId] = score;
                }

                _killFeed.Add(new KillFeedEntry
                {
                    KillerName = killerName,
                    VictimName = victimName,
                    WeaponName = weaponName ?? "Unknown",
                    Timestamp = UnityEngine.Time.time
                });

                // Trim feed
                while (_killFeed.Count > MAX_KILL_FEED)
                {
                    _killFeed.RemoveAt(0);
                }
            }

            // Display in the game's native message log
            try
            {
                // The game's native system handles colors via rich text
                string safeWeapon = string.IsNullOrEmpty(weaponName) ? "Unknown Weapon" : weaponName;
                string msg = $"<color=#00FF00>{killerName}</color> destroyed <color=#FF4444>{victimName}</color> [{safeWeapon}]";
                
                // Native UI method: AddMessage(string messageText, FlightMessageType type, FlightMessageSound sound = FlightMessageSound.None, float displayTime = 5f)
                Falcon.Game2.UI.FlightMessages2.AddMessage(msg, Falcon.Game2.UI.FlightMessageType.Flight, Falcon.Game2.UI.FlightMessageSound.ObjectiveComplete, 8f);
            }
            catch (Exception ex)
            {
                Plugin.Log?.LogWarning($"[ScoreTracker] Failed to push native kill message: {ex.Message}");
            }

            Plugin.Log?.LogInfo($"[ScoreTracker] Kill recorded: {killerId} -> {victimId} with {weaponName}");
        }

        /// <summary>
        /// Get a snapshot of all player scores, sorted by kills descending.
        /// </summary>
        public List<PlayerScore> GetScores()
        {
            lock (_lock)
            {
                var list = new List<PlayerScore>(_scores.Values);
                list.Sort((a, b) => b.Kills.CompareTo(a.Kills));
                return list;
            }
        }

        /// <summary>
        /// Get active kill feed entries (not expired).
        /// </summary>
        public List<KillFeedEntry> GetActiveKillFeed()
        {
            float now = UnityEngine.Time.time;
            lock (_lock)
            {
                // Remove expired entries
                _killFeed.RemoveAll(e => now - e.Timestamp > KILL_FEED_DURATION);
                return new List<KillFeedEntry>(_killFeed);
            }
        }

        /// <summary>
        /// Get score for a specific player.
        /// </summary>
        public PlayerScore GetPlayerScore(ulong peerId)
        {
            lock (_lock)
            {
                return _scores.TryGetValue(peerId, out var score) ? score : null;
            }
        }

        /// <summary>
        /// Reset all scores (e.g., new round).
        /// </summary>
        public void Reset()
        {
            lock (_lock)
            {
                _scores.Clear();
                _killFeed.Clear();
            }
        }
    }
}
