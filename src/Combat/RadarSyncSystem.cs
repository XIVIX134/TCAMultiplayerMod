using System;
using System.Collections.Generic;
using UnityEngine;
using Falcon.Targeting;
using Falcon.UniversalAircraft;
using TCAMultiplayer.Core;
using TCAMultiplayer.Protocol;
using TCAMultiplayer.Sync;

namespace TCAMultiplayer.Combat
{
    /// <summary>
    /// Synchronizes radar lock/unlock between peers. Remote radars are added to
    /// Radar.ActiveRadars so ThreatWarning automatically triggers RWR on victims.
    /// </summary>
    public class RadarSyncSystem : IDisposable
    {
        private const string Tag = "RADAR-SYNC";
        private const float RadarCheckInterval = 0.1f;

        private readonly GameSession _session;
        private readonly ConnectionManager _connection;
        private readonly PacketRouter _router;
        private readonly RemoteAircraftManager _remoteManager;
        private readonly Func<UniAircraft> _localAircraftProvider;

        private readonly Dictionary<ulong, Radar> _remoteRadars = new Dictionary<ulong, Radar>();
        private readonly Dictionary<ulong, RadarLockPacket> _pendingLocks = new Dictionary<ulong, RadarLockPacket>();
        private readonly Dictionary<ulong, Target> _forcedLocalThreatTargets = new Dictionary<ulong, Target>();

        private Target _lastLockedTarget;
        private ulong _lastLockedPeerId;
        private bool _lastRadarActive;
        private Radar _localRadar;
        private float _nextCheckTime;
        private bool _disposed;

        public RadarSyncSystem(
            GameSession session,
            ConnectionManager connection,
            PacketRouter router,
            RemoteAircraftManager remoteManager,
            Func<UniAircraft> localAircraftProvider = null)
        {
            _session = session ?? throw new ArgumentNullException(nameof(session));
            _connection = connection ?? throw new ArgumentNullException(nameof(connection));
            _router = router ?? throw new ArgumentNullException(nameof(router));
            _remoteManager = remoteManager ?? throw new ArgumentNullException(nameof(remoteManager));
            _localAircraftProvider = localAircraftProvider;

            _router.Register(PacketType.RadarLock, HandleRadarLockRaw);
            Log.Info(Tag, "Initialized");
        }

        /// <summary>Poll local radar for state changes. Call each frame.</summary>
        public void Update()
        {
            if (_disposed) return;
            ApplyPendingLocks();
            MaintainForcedLocalThreats();
            if (Time.time < _nextCheckTime) return;
            _nextCheckTime = Time.time + RadarCheckInterval;

            // Re-resolve the local radar EVERY check. Caching it until the reference
            // "dies" is unsafe: a destroyed plane's Radar object never throws, so after
            // a death + respawn the cached reference silently points at the dead plane's
            // radar forever — and new locks are never detected or synced. (Same stale-
            // reference-after-respawn pattern that froze gear sync.)
            var currentRadar = FindLocalRadar();
            if (!ReferenceEquals(_localRadar, currentRadar))
            {
                _localRadar = currentRadar;
                if (_localRadar == null) return;
                _lastRadarActive = _localRadar.IsActive;
                _lastLockedTarget = null;
                _lastLockedPeerId = 0;
                Log.Info(Tag, "Local radar (re)acquired — monitoring for locks");
            }
            if (_localRadar == null) return;

            CheckRadarActiveState();
            if (!_lastRadarActive) return;
            CheckLockedTargetState();
        }

        private void CheckRadarActiveState()
        {
            bool currentActive = _localRadar.IsActive;
            if (currentActive == _lastRadarActive) return;

            _lastRadarActive = currentActive;
            if (!currentActive && _lastLockedTarget != null)
            {
                SendRadarLockPacket(0, false);
                _lastLockedTarget = null;
                Log.Info(Tag, "Radar deactivated, sent unlock");
            }
        }

        private void CheckLockedTargetState()
        {
            Target currentTarget = _localRadar.LockedTarget;
            if (ReferenceEquals(currentTarget, _lastLockedTarget)) return;

            ulong targetPeerId = ResolvePeerId(currentTarget);
            if (currentTarget != null)
            {
                if (targetPeerId != 0)
                {
                    SendRadarLockPacket(targetPeerId, true);
                    Log.Info(Tag, $"Locked peer {targetPeerId} — lock packet sent");
                }
                else
                {
                    if (_lastLockedPeerId != 0)
                    {
                        SendRadarLockPacket(0, false);
                        Log.Info(Tag, "Lock moved off peer, sent unlock");
                    }
                    // Always log non-peer locks: if a player locks another player and THIS
                    // line appears instead of "Locked peer", peer resolution is the bug.
                    Log.Info(Tag, $"Local radar locked non-peer target '{currentTarget.name}' (not synced)");
                }
            }
            else if (_lastLockedTarget != null)
            {
                SendRadarLockPacket(0, false);
                Log.Info(Tag, "Lock released, sent unlock");
            }

            _lastLockedTarget = currentTarget;
            _lastLockedPeerId = targetPeerId;
        }

        // ── Outbound: send radar lock/unlock to all peers ────────────

        private void SendRadarLockPacket(ulong targetId, bool isLocked)
        {
            var packet = new RadarLockPacket
            {
                LockerId = _session.LocalPeerId,
                TargetId = targetId,
                IsLocked = isLocked,
                LockType = 0 // radar
            };

            var payload = PacketSerializer.SerializeRadarLock(packet);
            var frame = PacketSerializer.Serialize(PacketType.RadarLock, payload);
            _connection.BroadcastReliable(frame);
        }

        // ── Inbound: handle radar lock/unlock from remote peers ──────

        private void HandleRadarLockRaw(ulong fromPeerId, byte[] data)
        {
            if (_disposed) return;

            var (_, payload) = PacketSerializer.Deserialize(data);
            if (payload == null)
            {
                Log.Warning(Tag, $"Null RadarLock payload from peer {fromPeerId}");
                return;
            }

            var packet = PacketSerializer.DeserializeRadarLock(payload);
            if (_session.IsHost && fromPeerId != _session.LocalPeerId && packet.LockerId != fromPeerId)
            {
                Log.Warning(Tag, $"Rejected RadarLock from peer {fromPeerId} for locker {packet.LockerId}");
                return;
            }
            HandleRadarLockPacket(packet);
        }

        private void HandleRadarLockPacket(RadarLockPacket packet)
        {
            if (!TryGetRemoteRadar(packet.LockerId, out var remoteRadar))
            {
                remoteRadar = SetupRemoteRadar(packet.LockerId);
                if (remoteRadar == null)
                {
                    if (packet.IsLocked)
                        _pendingLocks[packet.LockerId] = packet;
                    Log.Debug(Tag, $"Cannot setup radar for peer {packet.LockerId}, queued={packet.IsLocked}");
                    return;
                }
            }

            if (packet.IsLocked)
            {
                Target lockTarget = ResolveTarget(packet.TargetId);
                if (lockTarget != null)
                {
                    // checkAgainstRadar=false: skip range/cone checks
                    // Sets LockedTarget. The local TWD is updated below when
                    // this lock targets the player.
                    if (!IsLocalTarget(lockTarget))
                        ClearForcedLocalThreat(packet.LockerId, remoteRadar, refreshThreatWarning: false);

                    ApplyRemoteLock(remoteRadar, lockTarget);
                    ForceLocalThreatIfNeeded(packet.LockerId, remoteRadar, lockTarget);
                    Log.Info(Tag, $"Peer {packet.LockerId} locked target {packet.TargetId}");
                }
                else
                {
                    if (packet.TargetId != _session.LocalPeerId)
                        ClearForcedLocalThreat(packet.LockerId, remoteRadar, refreshThreatWarning: true);

                    _pendingLocks[packet.LockerId] = packet;
                    Log.Debug(Tag, $"Cannot resolve target {packet.TargetId} for lock from peer {packet.LockerId}, queued");
                }
            }
            else
            {
                _pendingLocks.Remove(packet.LockerId);
                remoteRadar.UnlockTarget();
                ClearForcedLocalThreat(packet.LockerId, remoteRadar, refreshThreatWarning: false);
                RefreshLocalThreatWarning();
                Log.Info(Tag, $"Peer {packet.LockerId} unlocked");
            }
        }

        private void ApplyPendingLocks()
        {
            if (_pendingLocks.Count == 0) return;

            var lockerIds = new List<ulong>(_pendingLocks.Keys);
            foreach (var lockerId in lockerIds)
            {
                if (!_pendingLocks.TryGetValue(lockerId, out var packet)) continue;
                if (!packet.IsLocked)
                {
                    _pendingLocks.Remove(lockerId);
                    continue;
                }

                if (!TryGetRemoteRadar(packet.LockerId, out var remoteRadar))
                {
                    remoteRadar = SetupRemoteRadar(packet.LockerId);
                    if (remoteRadar == null) continue;
                }

                var target = ResolveTarget(packet.TargetId);
                if (target == null) continue;

                if (!IsLocalTarget(target))
                    ClearForcedLocalThreat(packet.LockerId, remoteRadar, refreshThreatWarning: false);

                ApplyRemoteLock(remoteRadar, target);
                ForceLocalThreatIfNeeded(packet.LockerId, remoteRadar, target);
                _pendingLocks.Remove(lockerId);
                Log.Info(Tag, $"Applied queued lock: peer {packet.LockerId} -> {packet.TargetId}");
            }
        }

        private static void ApplyRemoteLock(Radar radar, Target target)
        {
            if (radar == null || target == null)
                return;

            if (!ReferenceEquals(radar.LockedTarget, target))
                radar.UnlockTarget();

            radar.LockTarget(target, false);
        }

        // ── Remote radar lifecycle ───────────────────────────────────

        /// <summary>
        /// Resolve and activate the native Radar for a remote peer's aircraft.
        /// Adds to Radar.ActiveRadars so ThreatWarning can detect its locks.
        /// </summary>
        private Radar SetupRemoteRadar(ulong peerId)
        {
            var remoteAircraft = _remoteManager.GetAircraft(peerId);
            if (remoteAircraft == null)
            {
                Log.Warning(Tag, $"No aircraft for peer {peerId}, cannot create radar");
                return null;
            }

            try
            {
                var target = remoteAircraft.GetComponentInChildren<Target>();
                if (target == null)
                {
                    Log.Warning(Tag, $"No Target component on aircraft for peer {peerId}");
                    return null;
                }

                var radar = remoteAircraft.Radar;
                if (radar == null || radar.OwnTarget == null)
                {
                    radar = new Radar(target, target.transform);
                    if (remoteAircraft.Data?.Radar != null)
                        radar.Initialize(remoteAircraft.Data.Radar);
                    Log.Warning(Tag, $"Created fallback radar for peer {peerId}; native radar missing");
                }

                // Native aircraft spawns already activate their Radar. Calling SetActive(true)
                // on an active Radar adds duplicates to Radar.ActiveRadars, so only activate
                // when the native lifecycle has not already done it.
                if (!radar.IsActive)
                    radar.SetActive(true);

                _remoteRadars[peerId] = radar;
                Log.Info(Tag, $"Using native remote radar for peer {peerId}");
                return radar;
            }
            catch (Exception ex)
            {
                Log.Error(Tag, $"Failed to create radar for peer {peerId}: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Deactivate and remove a remote peer's radar.
        /// Call on peer disconnect or aircraft destruction.
        /// </summary>
        public void CleanupPeerRadar(ulong peerId)
        {
            if (!_remoteRadars.TryGetValue(peerId, out var radar)) return;

            try
            {
                radar.UnlockTarget();
                ClearForcedLocalThreat(peerId, radar, refreshThreatWarning: false);
                radar.SetActive(false);
                RefreshLocalThreatWarning();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Error deactivating radar for peer {peerId}: {ex.Message}");
            }

            _remoteRadars.Remove(peerId);
            Log.Info(Tag, $"Cleaned up radar for peer {peerId}");
        }


        /// <summary>Find local player's radar.</summary>
        private Radar FindLocalRadar()
        {
            // The player's radar is directly available on their aircraft — use it.
            // NEVER rely on scanning Radar.ActiveRadars for this: that static list
            // contains every radar in the scene (AI planes, SAM sites, clones) ordered
            // by spawn time, so "first non-clone radar" silently grabs a ground radar
            // on missions that have them — and then player locks are never detected.
            var localAircraft = FindLocalPlayerAircraft();
            if (localAircraft != null && localAircraft.Radar != null
                && localAircraft.Radar.OwnTarget != null)
            {
                return localAircraft.Radar;
            }

            // Fallback only if the aircraft's own radar is unavailable.
            var activeRadars = Radar.ActiveRadars;
            if (activeRadars == null || activeRadars.Count == 0) return null;

            for (int i = 0; i < activeRadars.Count; i++)
            {
                var radar = activeRadars[i];
                if (radar == null || !radar.IsActive) continue;
                if (IsRemoteRadar(radar)) continue;
                if (radar.OwnTarget != null && IsRemoteCloneTarget(radar.OwnTarget)) continue;
                Log.Warning(Tag, "Using fallback ActiveRadars scan for local radar — " +
                                 "lock detection may be unreliable");
                return radar;
            }

            return null;
        }

        private bool IsRemoteRadar(Radar radar)
        {
            foreach (var remote in _remoteRadars.Values)
            {
                if (ReferenceEquals(radar, remote)) return true;
            }
            return false;
        }

        private bool TryGetRemoteRadar(ulong peerId, out Radar radar)
        {
            if (_remoteRadars.TryGetValue(peerId, out radar) && IsRemoteRadarCurrent(peerId, radar))
                return true;

            if (radar != null)
                DeactivateRemoteRadar(peerId, radar);

            _remoteRadars.Remove(peerId);
            radar = null;
            return false;
        }

        private bool IsRemoteRadarCurrent(ulong peerId, Radar radar)
        {
            if (!IsRadarAlive(radar))
                return false;
            if (!radar.IsActive)
                return false;

            var aircraft = _remoteManager.GetAircraft(peerId);
            if (aircraft == null)
                return false;

            if (ReferenceEquals(radar, aircraft.Radar))
                return true;

            var target = aircraft.GetComponentInChildren<Target>();
            return target != null && ReferenceEquals(radar.OwnTarget, target);
        }

        private void DeactivateRemoteRadar(ulong peerId, Radar radar)
        {
            try
            {
                radar.UnlockTarget();
                ClearForcedLocalThreat(peerId, radar, refreshThreatWarning: false);
                if (radar.IsActive)
                    radar.SetActive(false);
                RefreshLocalThreatWarning();
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"Error deactivating radar for peer {peerId}: {ex.Message}");
            }
        }

        private bool IsRemoteCloneTarget(Target target)
        {
            if (target == null) return false;
            var ownerAircraft = target.GetComponentInParent<UniAircraft>();
            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft != null && ownerAircraft == aircraft) return true;
            }
            return false;
        }

        private void ForceLocalThreatIfNeeded(ulong lockerId, Radar radar, Target target)
        {
            if (!IsLocalTarget(target))
                return;

            _forcedLocalThreatTargets[lockerId] = target;
            RefreshLocalThreatWarning();
            EnsureLocalThreatState(radar, target);
            LogRwrState($"lock-applied by peer {lockerId}");
        }

        private void MaintainForcedLocalThreats()
        {
            if (_forcedLocalThreatTargets.Count == 0)
                return;

            var lockerIds = new List<ulong>(_forcedLocalThreatTargets.Keys);
            foreach (var lockerId in lockerIds)
            {
                if (!_forcedLocalThreatTargets.TryGetValue(lockerId, out var target)
                    || !IsLocalTarget(target))
                {
                    ClearForcedLocalThreat(lockerId, refreshThreatWarning: true);
                    continue;
                }

                if (!TryGetRemoteRadar(lockerId, out var radar))
                {
                    ClearForcedLocalThreat(lockerId, refreshThreatWarning: true);
                    continue;
                }

                if (!ReferenceEquals(radar.LockedTarget, target))
                {
                    ClearForcedLocalThreat(lockerId, radar, refreshThreatWarning: true);
                    continue;
                }

                EnsureLocalThreatState(radar, target);
            }
        }

        private void EnsureLocalThreatState(Radar radar, Target target)
        {
            if (radar == null || target == null)
                return;

            if (!radar.IsActive)
                radar.SetActive(true);

            AddForcedDetectedTarget(radar, target);

            var localAircraft = FindLocalPlayerAircraft();
            if (localAircraft?.ThreatWarning == null)
                return;

            localAircraft.ThreatWarning.IsActive = true;
            localAircraft.ThreatWarning.Threats.Add(radar);

            // Periodic deep diagnostic while a forced threat is active — this is the
            // exact data the HUD warning depends on, so any break in the chain shows here.
            if (Time.time - _lastRwrDiagnosticTime > 5f)
            {
                _lastRwrDiagnosticTime = Time.time;
                LogRwrState("maintain");
            }
        }

        private float _lastRwrDiagnosticTime;

        /// <summary>
        /// Dumps the complete chain the HUD's RWR warning depends on:
        /// UniAircraft.Player identity, ThreatWarning contents, lock matches,
        /// and the native visibility check inputs.
        /// </summary>
        private void LogRwrState(string context)
        {
            try
            {
                var localAircraft = FindLocalPlayerAircraft();
                var hudAircraft = UniAircraft.Player;
                var sb = new System.Text.StringBuilder(512);
                sb.Append($"[RWR-DBG] {context}: ");

                if (localAircraft == null)
                {
                    Log.Info(Tag, sb.Append("no local aircraft").ToString());
                    return;
                }

                // The HUD reads UniAircraft.Player.ThreatWarning — if that's a different
                // aircraft than the one we populate, the warning can never show.
                sb.Append($"sameAircraftAsHud={ReferenceEquals(localAircraft, hudAircraft)} ");

                var tw = localAircraft.ThreatWarning;
                if (tw == null)
                {
                    Log.Info(Tag, sb.Append("ThreatWarning=NULL").ToString());
                    return;
                }

                var ownTarget = tw.OwnRadar != null ? tw.OwnRadar.OwnTarget : null;
                sb.Append($"twActive={tw.IsActive} threats={tw.Threats.Count} ")
                  .Append($"lockedThreats={tw.GetNumberOfLockedThreats()} ")
                  .Append($"ownTarget={(ownTarget != null ? ownTarget.name : "<null>")} ");

                foreach (var threat in tw.Threats)
                {
                    if (threat == null) { sb.Append("| <null radar> "); continue; }
                    string lockedName = threat.LockedTarget != null ? threat.LockedTarget.name : "<none>";
                    bool matchesOwn = ownTarget != null && ReferenceEquals(threat.LockedTarget, ownTarget);
                    string distance = "?";
                    string visRange = "?";
                    try
                    {
                        if (threat.Transform != null && tw.OwnRadar != null && tw.OwnRadar.Transform != null)
                        {
                            float dist = Vector3.Distance(threat.Position, tw.OwnRadar.Position);
                            float angle = Vector3.Angle(tw.OwnRadar.Position - threat.Position, threat.Forward);
                            bool inCone = angle < threat.Data.FieldOfView * 0.6f;
                            float range = threat.Data.EffectiveRange * threat.Data.Detectability * (inCone ? 2f : 0.5f);
                            distance = dist.ToString("F0");
                            visRange = $"{range:F0}(cone={inCone})";
                        }
                    }
                    catch { }
                    sb.Append($"| threat: invalid={threat.IsInvalid} active={threat.IsActive} ")
                      .Append($"locked={lockedName} matchesOwn={matchesOwn} dist={distance} visRange={visRange} ");
                }

                Log.Info(Tag, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Warning(Tag, $"[RWR-DBG] failed: {ex.Message}");
            }
        }

        private void ClearForcedLocalThreat(
            ulong lockerId,
            Radar radar,
            bool refreshThreatWarning)
        {
            if (_forcedLocalThreatTargets.TryGetValue(lockerId, out var target))
                RemoveForcedDetectedTarget(radar, target);

            _forcedLocalThreatTargets.Remove(lockerId);

            var localAircraft = FindLocalPlayerAircraft();
            if (localAircraft?.ThreatWarning != null && radar != null)
                localAircraft.ThreatWarning.Threats.Remove(radar);

            if (refreshThreatWarning)
                RefreshLocalThreatWarning();
        }

        private void ClearForcedLocalThreat(ulong lockerId, bool refreshThreatWarning)
        {
            _remoteRadars.TryGetValue(lockerId, out var radar);
            ClearForcedLocalThreat(lockerId, radar, refreshThreatWarning);
        }

        private static void AddForcedDetectedTarget(Radar radar, Target target)
        {
            if (radar == null || target == null)
                return;

            radar.DetectedTargets.Add(target);
            if (!Radar.RadarTracking.TryGetValue(target, out var trackingRadars))
            {
                trackingRadars = new HashSet<Radar>();
                Radar.RadarTracking[target] = trackingRadars;
            }
            trackingRadars.Add(radar);
        }

        private static void RemoveForcedDetectedTarget(Radar radar, Target target)
        {
            if (radar == null || target == null)
                return;

            radar.DetectedTargets.Remove(target);
            if (Radar.RadarTracking.TryGetValue(target, out var trackingRadars))
            {
                trackingRadars.Remove(radar);
                if (trackingRadars.Count == 0)
                    Radar.RadarTracking.Remove(target);
            }
        }

        private bool IsLocalTarget(Target target)
        {
            if (target == null || IsRemoteCloneTarget(target))
                return false;

            var localAircraft = FindLocalPlayerAircraft();
            return localAircraft != null
                && target.GetComponentInParent<UniAircraft>() == localAircraft;
        }

        private void RefreshLocalThreatWarning()
        {
            try
            {
                var localAircraft = FindLocalPlayerAircraft();
                if (localAircraft?.ThreatWarning == null)
                    return;

                localAircraft.ThreatWarning.IsActive = true;
                localAircraft.ThreatWarning.Refresh();
            }
            catch (Exception ex)
            {
                Log.Debug(Tag, $"Threat warning refresh failed: {ex.Message}");
            }
        }

        private bool IsRadarAlive(Radar radar)
        {
            if (radar == null) return false;
            try
            {
                var _ = radar.OwnTarget;
                return true;
            }
            catch (Exception)
            {
                Log.Debug(Tag, "Local radar reference stale, will re-acquire");
                return false;
            }
        }

        /// <summary>Resolve Target → peer ID via remote aircraft lookup. Returns 0 if unknown.</summary>
        private ulong ResolvePeerId(Target target)
        {
            if (target == null) return 0;

            foreach (var peerId in _remoteManager.GetAllPeerIds())
            {
                var aircraft = _remoteManager.GetAircraft(peerId);
                if (aircraft == null) continue;

                var peerTarget = aircraft.GetComponentInChildren<Target>();
                if (ReferenceEquals(peerTarget, target)) return peerId;
            }

            return 0;
        }

        /// <summary>Resolve peer ID → Target component (local or remote).</summary>
        private Target ResolveTarget(ulong peerId)
        {
            if (peerId == _session.LocalPeerId)
            {
                return FindLocalPlayerTarget()
                    ?? _localRadar?.OwnTarget
                    ?? FindLocalRadar()?.OwnTarget;
            }

            var aircraft = _remoteManager.GetAircraft(peerId);
            if (aircraft == null) return null;
            return aircraft.GetComponentInChildren<Target>();
        }

        private Target FindLocalPlayerTarget()
        {
            var providedAircraft = FindLocalPlayerAircraft();
            if (providedAircraft != null)
            {
                var target = providedAircraft.GetComponentInChildren<Target>();
                if (target != null) return target;
            }

            return null;
        }

        private UniAircraft FindLocalPlayerAircraft()
        {
            var providedAircraft = _localAircraftProvider?.Invoke();
            if (providedAircraft != null && !IsRemoteCloneTarget(providedAircraft.Target))
                return providedAircraft;

            if (UniAircraft.Player != null && !IsRemoteCloneTarget(UniAircraft.Player.Target))
                return UniAircraft.Player;

            var allAircraft = UnityEngine.Object.FindObjectsByType<UniAircraft>(FindObjectsSortMode.None);
            foreach (var aircraft in allAircraft)
            {
                if (aircraft == null) continue;
                var target = aircraft.GetComponentInChildren<Target>();
                if (target != null && !IsRemoteCloneTarget(target))
                    return aircraft;
            }
            return null;
        }

        // ── Dispose ──────────────────────────────────────────────────

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _router.Unregister(PacketType.RadarLock, HandleRadarLockRaw);

            foreach (var kvp in _remoteRadars)
            {
                try
                {
                    kvp.Value.UnlockTarget();
                    kvp.Value.SetActive(false);
                }
                catch (Exception ex)
                {
                    Log.Warning(Tag, $"Error cleaning up radar for peer {kvp.Key}: {ex.Message}");
                }
            }
            _remoteRadars.Clear();
            _pendingLocks.Clear();
            _forcedLocalThreatTargets.Clear();

            _localRadar = null;
            _lastLockedTarget = null;
            _lastLockedPeerId = 0;
            Log.Info(Tag, "Disposed");
        }
    }
}
