using System;
using System.Collections.Generic;
using UnityEngine;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;

namespace TCAMultiplayer.Game
{
    /// <summary>
    /// Tracks kills, deaths, assists per player and maintains a kill feed.
    /// Hosts accept victim death reports, then broadcast authoritative score events.
    /// </summary>
    public class ScoreTracker : IDisposable
    {
        private const string Tag = "SCORE";
        private const int MaxKillFeedEntries = 10;

        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly List<KillFeedEntry> _killFeed = new List<KillFeedEntry>();
        private readonly HashSet<ulong> _legacyRecordedDeadVictims = new HashSet<ulong>();
        private readonly HashSet<DeathKey> _recordedDeathSequences = new HashSet<DeathKey>();
        private readonly Dictionary<ulong, ActiveDeath> _activeDeaths = new Dictionary<ulong, ActiveDeath>();
        private readonly Func<float> _timeProvider;
        private bool _disposed;

        public event Action<KillFeedEntry> OnKillFeedUpdated;
        public event Action OnScoresChanged;
        public event Action<KillConfirmPacket> OnKillConfirmed;
        public event Action<AircraftDestroyedPacket> OnDeathConfirmed;

        public IReadOnlyList<KillFeedEntry> KillFeed => _killFeed;

        // ── Constructor ─────────────────────────────────────────────

        public ScoreTracker(
            GameSession session,
            PacketRouter router,
            Func<float> timeProvider = null,
            ConnectionManager connection = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _connection = connection;
            _timeProvider = timeProvider ?? (() => Time.time);

            _router.Register(PacketType.DeathReport, HandleDeathReportRaw);
            _router.Register(PacketType.ScoreEvent, HandleScoreEventRaw);
            _session.OnPlayerLeft += HandlePlayerLeft;
            Log.Info(Tag, "Initialized");
        }

        // ── Public API ──────────────────────────────────────────────

        public void MarkPlayerRespawned(ulong playerId)
        {
            if (_disposed) return;
            _legacyRecordedDeadVictims.Remove(playerId);
            _activeDeaths.Remove(playerId);
            var player = _session.GetPlayer(playerId);
            if (player != null)
            {
                if (!player.IsAlive || player.IsAwaitingRespawn || player.LifeId == 0)
                    _session.BeginPlayerLife(playerId);
                else
                    player.IsAlive = true;
            }
        }

        /// <summary>
        /// Record a kill: increment killer's kills, victim's deaths, add to kill feed.
        /// </summary>
        public void RecordKill(ulong killerId, ulong victimId, string weaponName)
        {
            if (_disposed) return;
            if (_session.ArePlayersOnSameTeam(killerId, victimId))
                Log.Debug(Tag, $"Ignoring friendly kill killer={killerId} victim={victimId}");

            RecordKillInternal(killerId, victimId, weaponName, 0, countDeath: true, replaceUncreditedDeath: false);
            OnScoresChanged?.Invoke();
        }

        private void RecordKillInternal(
            ulong killerId,
            ulong victimId,
            string weaponName,
            uint deathSequence,
            bool countDeath,
            bool replaceUncreditedDeath)
        {
            var killer = _session.GetPlayer(killerId);

            bool friendlyKill = _session.ArePlayersOnSameTeam(killerId, victimId);
            if (killer != null && !friendlyKill)
            {
                killer.Kills++;
                Log.Info(Tag, $"Kill: {killer.PlayerName} (kills={killer.Kills})");
            }

            if (countDeath)
                RecordDeathInternal(victimId, logFeed: false, deathSequence: deathSequence);

            var entry = new KillFeedEntry
            {
                KillerId = killerId,
                VictimId = victimId,
                DeathSequence = deathSequence,
                HasKillCredit = !friendlyKill,
                KillerName = friendlyKill ? "Friendly fire" : killer?.PlayerName ?? $"Player_{killerId}",
                VictimName = _session.GetPlayer(victimId)?.PlayerName ?? $"Player_{victimId}",
                WeaponName = weaponName ?? "Unknown",
                Timestamp = _timeProvider()
            };

            if (replaceUncreditedDeath)
                ReplaceUncreditedDeathFeedEntry(entry);
            else
                AddKillFeedEntry(entry);
        }

        public void RecordDeath(ulong victimId, string reason)
        {
            if (_disposed) return;
            if (!RecordDeathInternal(victimId, logFeed: true, reason: reason, deathSequence: 0))
                return;
            OnScoresChanged?.Invoke();
        }

        private bool RecordDeathInternal(ulong victimId, bool logFeed, string reason = null, uint deathSequence = 0)
        {
            var victim = _session.GetPlayer(victimId);
            if (victim != null)
            {
                victim.Deaths++;
                _session.EndPlayerLife(victimId);
                Log.Info(Tag, $"Death: {victim.PlayerName} (deaths={victim.Deaths})");
            }

            if (!logFeed)
                return true;

            var entry = new KillFeedEntry
            {
                KillerId = 0,
                VictimId = victimId,
                DeathSequence = deathSequence,
                HasKillCredit = false,
                KillerName = reason ?? "Environment",
                VictimName = victim?.PlayerName ?? $"Player_{victimId}",
                WeaponName = "Destroyed",
                Timestamp = _timeProvider()
            };

            AddKillFeedEntry(entry);
            return true;
        }

        private void AddKillFeedEntry(KillFeedEntry entry)
        {
            _killFeed.Add(entry);
            if (_killFeed.Count > MaxKillFeedEntries)
                _killFeed.RemoveAt(0);
            OnKillFeedUpdated?.Invoke(entry);
        }

        /// <summary>
        /// Add a plain informational line (join/leave etc.) to the kill feed.
        /// </summary>
        public void RecordSystemMessage(string message)
        {
            if (_disposed || string.IsNullOrEmpty(message)) return;

            AddKillFeedEntry(new KillFeedEntry
            {
                IsSystemMessage = true,
                Message = message,
                Timestamp = _timeProvider()
            });
            Log.Info(Tag, $"Feed message: {message}");
        }

        private void HandlePlayerLeft(PlayerInfo player)
        {
            if (_disposed || player == null) return;
            if (player.PeerId == _session.LocalPeerId) return;

            RecordSystemMessage($"{player.PlayerName ?? $"Player_{player.PeerId}"} left the session");
        }

        private void ReplaceUncreditedDeathFeedEntry(KillFeedEntry entry)
        {
            for (int i = _killFeed.Count - 1; i >= 0; i--)
            {
                var existing = _killFeed[i];
                if (existing.VictimId == entry.VictimId && !existing.HasKillCredit)
                {
                    _killFeed[i] = entry;
                    OnKillFeedUpdated?.Invoke(entry);
                    return;
                }
            }

            AddKillFeedEntry(entry);
        }

        public void HandleKillConfirm(KillConfirmPacket packet)
        {
            HandleKillConfirmPacket(packet);
        }

        public void HandleLocalDeathReport(DeathReportPacket packet)
        {
            if (_disposed) return;
            if (_session.IsHost)
            {
                AcceptDeathReport(packet);
                return;
            }

            SendDeathReportToHost(packet);
        }

        public void HandleScoreEvent(ScoreEventPacket packet)
        {
            HandleScoreEventPacket(packet);
        }

        /// <summary>
        /// Record an assist for the given player.
        /// </summary>
        public void RecordAssist(ulong playerId)
        {
            if (_disposed) return;

            var player = _session.GetPlayer(playerId);
            if (player == null) return;

            player.Assists++;
            Log.Info(Tag, $"Assist: {player.PlayerName} (assists={player.Assists})");
            OnScoresChanged?.Invoke();
        }

        /// <summary>
        /// Returns players sorted by kills descending, then deaths ascending.
        /// </summary>
        public List<PlayerInfo> GetSortedScoreboard()
        {
            var players = new List<PlayerInfo>();
            foreach (var kvp in _session.Players)
            {
                players.Add(kvp.Value);
            }

            players.Sort((a, b) =>
            {
                int killCompare = b.Kills.CompareTo(a.Kills);
                if (killCompare != 0) return killCompare;
                return a.Deaths.CompareTo(b.Deaths);
            });

            return players;
        }

        /// <summary>
        /// Reset all player scores and clear the kill feed.
        /// </summary>
        public void ResetScores()
        {
            if (_disposed) return;

            foreach (var kvp in _session.Players)
            {
                var p = kvp.Value;
                p.Kills = 0;
                p.Deaths = 0;
                p.Assists = 0;
            }

            _legacyRecordedDeadVictims.Clear();
            _recordedDeathSequences.Clear();
            _activeDeaths.Clear();
            _killFeed.Clear();
            OnScoresChanged?.Invoke();
            Log.Info(Tag, "Scores reset");
        }

        // ── Inbound: host-authoritative score packets ────────────────

        private void HandleDeathReportRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;
            if (!_session.IsHost)
                return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null DeathReport payload from peer {fromPeerId}");
                return;
            }

            var packet = PacketSerializer.DeserializeDeathReport(payload);
            if (packet.VictimId != fromPeerId && fromPeerId != _session.LocalPeerId)
            {
                Log.Warning(Tag, $"DeathReport victim mismatch from peer {fromPeerId}: victim={packet.VictimId}");
                return;
            }

            AcceptDeathReport(packet);
        }

        private void HandleScoreEventRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;
            if (_session.IsHost)
                return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null ScoreEvent payload from peer {fromPeerId}");
                return;
            }

            var packet = PacketSerializer.DeserializeScoreEvent(payload);
            HandleScoreEventPacket(packet);
        }

        private void HandleKillConfirmPacket(KillConfirmPacket packet)
        {
            if (packet.KillerId == 0 || packet.VictimId == 0)
            {
                Log.Warning(Tag, "KillConfirm with invalid IDs, ignoring");
                return;
            }

            ClearActiveDeathIfPlayerRespawned(packet.VictimId);

            if (_activeDeaths.TryGetValue(packet.VictimId, out var activeDeath))
            {
                RecordDeathSequence(packet.VictimId, packet.DeathSequence);

                if (activeDeath.HasKillCredit)
                {
                    Log.Debug(Tag, $"Duplicate KillConfirm for already-credited victim {packet.VictimId} seq={packet.DeathSequence}, ignoring");
                    return;
                }

                RecordKillInternal(
                    packet.KillerId,
                    packet.VictimId,
                    packet.WeaponName,
                    packet.DeathSequence,
                    countDeath: false,
                    replaceUncreditedDeath: true);

                _activeDeaths[packet.VictimId] = new ActiveDeath(packet.DeathSequence, hasKillCredit: true);
                OnKillConfirmed?.Invoke(packet);
                OnScoresChanged?.Invoke();
                return;
            }

            if (IsDuplicateDeath(packet.VictimId, packet.DeathSequence))
            {
                Log.Debug(Tag, $"Duplicate KillConfirm for victim {packet.VictimId} seq={packet.DeathSequence}, ignoring");
                return;
            }

            RecordKillInternal(
                packet.KillerId,
                packet.VictimId,
                packet.WeaponName,
                packet.DeathSequence,
                countDeath: true,
                replaceUncreditedDeath: false);
            RecordDeathSequence(packet.VictimId, packet.DeathSequence);
            _activeDeaths[packet.VictimId] = new ActiveDeath(packet.DeathSequence, hasKillCredit: true);
            OnKillConfirmed?.Invoke(packet);
            OnScoresChanged?.Invoke();
        }

        public void HandleDeathConfirm(AircraftDestroyedPacket packet)
        {
            HandleAircraftDestroyedPacket(packet);
        }

        private void HandleAircraftDestroyedPacket(AircraftDestroyedPacket packet)
        {
            if (packet.VictimId == 0)
            {
                Log.Warning(Tag, "AircraftDestroyed with invalid victim, ignoring");
                return;
            }

            ClearActiveDeathIfPlayerRespawned(packet.VictimId);

            if (_activeDeaths.TryGetValue(packet.VictimId, out var activeDeath))
            {
                RecordDeathSequence(packet.VictimId, packet.DeathSequence);
                Log.Debug(Tag, $"Duplicate AircraftDestroyed for active victim {packet.VictimId} seq={packet.DeathSequence}, ignoring");
                return;
            }

            if (IsDuplicateDeath(packet.VictimId, packet.DeathSequence))
            {
                Log.Debug(Tag, $"Duplicate AircraftDestroyed for victim {packet.VictimId} seq={packet.DeathSequence}, ignoring");
                return;
            }

            RecordDeathInternal(packet.VictimId, logFeed: true, reason: packet.Reason, deathSequence: packet.DeathSequence);
            RecordDeathSequence(packet.VictimId, packet.DeathSequence);
            _activeDeaths[packet.VictimId] = new ActiveDeath(packet.DeathSequence, hasKillCredit: false);
            OnDeathConfirmed?.Invoke(packet);
            OnScoresChanged?.Invoke();
        }

        private void AcceptDeathReport(DeathReportPacket report)
        {
            report = NormalizeFriendlyDeathReport(report);
            if (!ValidateDeathReport(report))
                return;

            var scoreEvent = new ScoreEventPacket
            {
                VictimId = report.VictimId,
                KillerId = report.KillerId,
                LifeId = report.LifeId,
                WeaponName = report.WeaponName,
                Reason = report.Reason
            };

            if (!HandleScoreEventPacket(scoreEvent))
                return;

            BroadcastScoreEvent(scoreEvent);
        }

        private bool HandleScoreEventPacket(ScoreEventPacket packet)
        {
            packet = NormalizeFriendlyScoreEvent(packet);
            if (!ValidateScoreEvent(packet))
                return false;

            ClearActiveDeathIfPlayerRespawned(packet.VictimId);
            if (_activeDeaths.TryGetValue(packet.VictimId, out var activeDeath))
            {
                if (activeDeath.DeathSequence != 0 && activeDeath.DeathSequence != packet.LifeId)
                {
                    Log.Warning(Tag, $"Contradictory ScoreEvent for active victim {packet.VictimId}: activeLife={activeDeath.DeathSequence} incomingLife={packet.LifeId}");
                    return false;
                }

                bool hasKillCredit = packet.KillerId != 0 && packet.KillerId != packet.VictimId;
                if (activeDeath.HasKillCredit || !hasKillCredit)
                {
                    Log.Debug(Tag, $"Duplicate ScoreEvent for active victim {packet.VictimId} life={packet.LifeId}, ignoring");
                    return false;
                }

                RecordKillInternal(
                    packet.KillerId,
                    packet.VictimId,
                    packet.WeaponName,
                    packet.LifeId,
                    countDeath: false,
                    replaceUncreditedDeath: true);

                _activeDeaths[packet.VictimId] = new ActiveDeath(packet.LifeId, hasKillCredit: true);
                RecordDeathSequence(packet.VictimId, packet.LifeId);
                OnKillConfirmed?.Invoke(ToKillConfirm(packet));
                OnScoresChanged?.Invoke();
                return true;
            }

            if (IsDuplicateDeath(packet.VictimId, packet.LifeId))
            {
                Log.Debug(Tag, $"Duplicate ScoreEvent for victim {packet.VictimId} life={packet.LifeId}, ignoring");
                return false;
            }

            if (packet.KillerId != 0 && packet.KillerId != packet.VictimId)
            {
                RecordKillInternal(
                    packet.KillerId,
                    packet.VictimId,
                    packet.WeaponName,
                    packet.LifeId,
                    countDeath: true,
                    replaceUncreditedDeath: false);
                _activeDeaths[packet.VictimId] = new ActiveDeath(packet.LifeId, hasKillCredit: true);
                RecordDeathSequence(packet.VictimId, packet.LifeId);
                OnKillConfirmed?.Invoke(ToKillConfirm(packet));
            }
            else
            {
                RecordDeathInternal(
                    packet.VictimId,
                    logFeed: true,
                    reason: string.IsNullOrEmpty(packet.Reason) ? "terrain/self" : packet.Reason,
                    deathSequence: packet.LifeId);
                _activeDeaths[packet.VictimId] = new ActiveDeath(packet.LifeId, hasKillCredit: false);
                RecordDeathSequence(packet.VictimId, packet.LifeId);
                OnDeathConfirmed?.Invoke(ToAircraftDestroyed(packet));
            }

            OnScoresChanged?.Invoke();
            return true;
        }

        private DeathReportPacket NormalizeFriendlyDeathReport(DeathReportPacket report)
        {
            if (report.KillerId != 0
                && report.KillerId != report.VictimId
                && _session.ArePlayersOnSameTeam(report.KillerId, report.VictimId))
            {
                Log.Debug(Tag, $"DeathReport friendly fire converted to uncredited death killer={report.KillerId} victim={report.VictimId}");
                report.KillerId = 0;
                report.Reason = "friendly-fire";
                if (string.IsNullOrEmpty(report.WeaponName))
                    report.WeaponName = "Friendly fire";
            }

            return report;
        }

        private ScoreEventPacket NormalizeFriendlyScoreEvent(ScoreEventPacket packet)
        {
            if (packet.KillerId != 0
                && packet.KillerId != packet.VictimId
                && _session.ArePlayersOnSameTeam(packet.KillerId, packet.VictimId))
            {
                Log.Debug(Tag, $"ScoreEvent friendly fire converted to uncredited death killer={packet.KillerId} victim={packet.VictimId}");
                packet.KillerId = 0;
                packet.Reason = "friendly-fire";
                if (string.IsNullOrEmpty(packet.WeaponName))
                    packet.WeaponName = "Friendly fire";
            }

            return packet;
        }

        private bool ValidateDeathReport(DeathReportPacket report)
        {
            if (report.VictimId == 0 || report.LifeId == 0)
            {
                Log.Warning(Tag, $"Invalid DeathReport victim={report.VictimId} life={report.LifeId}");
                return false;
            }

            var victim = _session.GetPlayer(report.VictimId);
            if (victim == null)
            {
                Log.Warning(Tag, $"DeathReport for unknown victim {report.VictimId}");
                return false;
            }

            if (victim.LifeId != 0 && victim.LifeId != report.LifeId)
            {
                Log.Warning(Tag, $"DeathReport stale/future life for victim {report.VictimId}: current={victim.LifeId} incoming={report.LifeId}");
                return false;
            }

            if (_activeDeaths.TryGetValue(report.VictimId, out var activeDeath)
                && activeDeath.DeathSequence != 0
                && activeDeath.DeathSequence != report.LifeId)
            {
                Log.Warning(Tag, $"DeathReport contradicts active death for victim {report.VictimId}: activeLife={activeDeath.DeathSequence} incomingLife={report.LifeId}");
                return false;
            }

            if (IsDuplicateDeath(report.VictimId, report.LifeId)
                && (!_activeDeaths.TryGetValue(report.VictimId, out activeDeath)
                    || activeDeath.HasKillCredit
                    || report.KillerId == 0
                    || report.KillerId == report.VictimId))
            {
                Log.Debug(Tag, $"Duplicate DeathReport for victim {report.VictimId} life={report.LifeId}, ignoring");
                return false;
            }

            if (report.KillerId == 0 || report.KillerId == report.VictimId)
                return true;

            if (_session.ArePlayersOnSameTeam(report.KillerId, report.VictimId))
            {
                Log.Warning(Tag, $"DeathReport friendly kill ignored killer={report.KillerId} victim={report.VictimId}");
                return false;
            }

            var killer = _session.GetPlayer(report.KillerId);
            if (killer == null)
            {
                Log.Warning(Tag, $"DeathReport for victim {report.VictimId} names unknown killer {report.KillerId}");
                return false;
            }

            return true;
        }

        private bool ValidateScoreEvent(ScoreEventPacket packet)
        {
            if (packet.VictimId == 0 || packet.LifeId == 0)
            {
                Log.Warning(Tag, $"Invalid ScoreEvent victim={packet.VictimId} life={packet.LifeId}");
                return false;
            }

            var victim = _session.GetPlayer(packet.VictimId);
            if (victim == null)
            {
                Log.Warning(Tag, $"ScoreEvent for unknown victim {packet.VictimId}");
                return false;
            }

            if (packet.KillerId == 0 || packet.KillerId == packet.VictimId)
                return true;

            if (_session.ArePlayersOnSameTeam(packet.KillerId, packet.VictimId))
            {
                Log.Warning(Tag, $"ScoreEvent friendly kill ignored killer={packet.KillerId} victim={packet.VictimId}");
                return false;
            }

            var killer = _session.GetPlayer(packet.KillerId);
            if (killer == null)
            {
                Log.Warning(Tag, $"ScoreEvent for victim {packet.VictimId} names unknown killer {packet.KillerId}");
                return false;
            }

            return true;
        }

        private void SendDeathReportToHost(DeathReportPacket packet)
        {
            if (_connection == null)
            {
                Log.Warning(Tag, "Cannot send DeathReport: no connection manager");
                return;
            }

            var payload = PacketSerializer.SerializeDeathReport(packet);
            var data = PacketSerializer.Serialize(PacketType.DeathReport, payload);
            _connection.BroadcastReliable(data);
            Log.Info(Tag, $"DeathReport: victim={packet.VictimId} killer={packet.KillerId} life={packet.LifeId} weapon={packet.WeaponName}");
        }

        private void BroadcastScoreEvent(ScoreEventPacket packet)
        {
            if (_connection == null)
                return;

            var payload = PacketSerializer.SerializeScoreEvent(packet);
            var data = PacketSerializer.Serialize(PacketType.ScoreEvent, payload);
            _connection.BroadcastReliable(data);
            Log.Info(Tag, $"ScoreEvent: victim={packet.VictimId} killer={packet.KillerId} life={packet.LifeId} weapon={packet.WeaponName}");
        }

        private static KillConfirmPacket ToKillConfirm(ScoreEventPacket packet)
        {
            return new KillConfirmPacket
            {
                KillerId = packet.KillerId,
                VictimId = packet.VictimId,
                DeathSequence = packet.LifeId,
                WeaponName = packet.WeaponName
            };
        }

        private static AircraftDestroyedPacket ToAircraftDestroyed(ScoreEventPacket packet)
        {
            return new AircraftDestroyedPacket
            {
                VictimId = packet.VictimId,
                DeathSequence = packet.LifeId,
                Reason = packet.Reason
            };
        }

        private void ClearActiveDeathIfPlayerRespawned(ulong victimId)
        {
            var victim = _session.GetPlayer(victimId);
            if (victim != null && victim.IsAlive)
                _activeDeaths.Remove(victimId);
        }

        private bool IsDuplicateDeath(ulong victimId, uint deathSequence)
        {
            if (deathSequence == 0)
                return _legacyRecordedDeadVictims.Contains(victimId);
            return _recordedDeathSequences.Contains(new DeathKey(victimId, deathSequence));
        }

        private void RecordDeathSequence(ulong victimId, uint deathSequence)
        {
            if (deathSequence == 0)
                _legacyRecordedDeadVictims.Add(victimId);
            else
                _recordedDeathSequences.Add(new DeathKey(victimId, deathSequence));
        }

        // ── Dispose ─────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.Unregister(PacketType.DeathReport, HandleDeathReportRaw);
            _router.Unregister(PacketType.ScoreEvent, HandleScoreEventRaw);
            _session.OnPlayerLeft -= HandlePlayerLeft;

            OnKillFeedUpdated = null;
            OnScoresChanged = null;
            OnKillConfirmed = null;
            OnDeathConfirmed = null;
            _legacyRecordedDeadVictims.Clear();
            _recordedDeathSequences.Clear();
            _activeDeaths.Clear();
            _killFeed.Clear();

            Log.Info(Tag, "Disposed");
        }
    }

    public struct DeathKey : IEquatable<DeathKey>
    {
        public readonly ulong VictimId;
        public readonly uint DeathSequence;

        public DeathKey(ulong victimId, uint deathSequence)
        {
            VictimId = victimId;
            DeathSequence = deathSequence;
        }

        public bool Equals(DeathKey other)
        {
            return VictimId == other.VictimId && DeathSequence == other.DeathSequence;
        }

        public override bool Equals(object obj)
        {
            return obj is DeathKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return (VictimId.GetHashCode() * 397) ^ DeathSequence.GetHashCode();
            }
        }
    }

    public struct ActiveDeath
    {
        public readonly uint DeathSequence;
        public readonly bool HasKillCredit;

        public ActiveDeath(uint deathSequence, bool hasKillCredit)
        {
            DeathSequence = deathSequence;
            HasKillCredit = hasKillCredit;
        }
    }

    /// <summary>
    /// Single entry in the kill feed display.
    /// </summary>
    public struct KillFeedEntry
    {
        public ulong KillerId;
        public ulong VictimId;
        public uint DeathSequence;
        public bool HasKillCredit;
        public string KillerName;
        public string VictimName;
        public string WeaponName;
        public float Timestamp;
        public bool IsSystemMessage;    // renders Message as a plain feed line
        public string Message;
    }
}
