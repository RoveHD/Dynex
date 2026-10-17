namespace Dynex.Core;

/// <summary>Pure bandwidth policy; the caller alone applies and removes OS shaping.</summary>
public sealed class LimitPlanner
{
    private readonly DynexSettings _settings;
    private readonly List<(DateTimeOffset At, double Latency)> _idle = new();
    private string? _target;
    private DateTimeOffset _lastProbe;
    private DateTimeOffset _lastAdjustment;
    private DateTimeOffset _baselineAt;
    private double? _baseline;
    private double _factor = 1;
    private int _high, _low;
    private readonly Dictionary<Guid, PeerFeedback> _feedback = new();
    private readonly HashSet<Guid> _gamingPeers = new();
    private bool _peerAdjusted;
    private int _peerHigh, _peerLow;
    private DateTimeOffset _highSince, _lowSince;
    private DateTimeOffset _lastLocalRecoveryRound;
    private static readonly TimeSpan BaselineLifetime = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan EffectWindow = TimeSpan.FromSeconds(30);
    private ReductionProbe? _reductionProbe;
    private double? _restoreFactor;
    private DateTimeOffset _lastTrustedEvidence, _lastSafetyRecovery;
    private readonly Dictionary<string, FailedEpisode> _failedEpisodes = new();
    private readonly Dictionary<bool, string> _observedSources = new();

    public LimitPlanner(DynexSettings settings)
    {
        settings.Validate(false);
        _settings = settings;
    }

    public LimitDecision Plan(bool localGaming, IReadOnlyList<PeerAdvertisement> peers,
        ProbeStats? probe = null, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        var live = peers.Where(p => p.DeviceId != Guid.Empty && p.DeviceId != _settings.DeviceId &&
            at - p.LastSeenUtc < _settings.PeerTtl && p.LastSeenUtc <= at.AddSeconds(5))
            .GroupBy(p => p.DeviceId).Select(g => g.OrderByDescending(p => p.LastSeenUtc).First()).ToArray();
        var gamers = live.Count(p => p.IsGaming) + (localGaming ? 1 : 0);
        var active = gamers > 0;
        var participants = live.Length + 1;
        var proposals = live.Select(p => p.BudgetProposal).Where(p => p is { IsValid: true })
            .Cast<DownloadBudgetProposal>().ToList();
        // Consumers use authenticated PC proposals without treating their own
        // numerical defaults as a measurement or circulating a negotiated value.
        if (_settings.CapacityMeasured) proposals.Add(DownloadBudgetProposal.FromSettings(_settings));
        // All converged participants see the same original proposals, independent of a leader.
        // Minimize the complete reserve-adjusted proposals, not only the raw line speeds:
        // different headroom and per-gamer settings must not produce different shared pools.
        double? capacity = proposals.Count == 0 ? null : proposals.Min(p => p.LineMbps);
        double? pool = active && proposals.Count > 0 ? proposals.Min(p => p.Remaining(gamers)) : null;
        var uploadProposals = proposals.Where(p => p.UploadCapacityMbps.HasValue).ToArray();
        double? uploadCapacity = uploadProposals.Length == 0 ? null : uploadProposals.Min(p => p.UploadCapacityMbps!.Value);
        double? uploadPool = active && uploadProposals.Length > 0
            ? uploadProposals.Min(p => p.UploadCapacityMbps!.Value * (1 - p.HeadroomPercent / 100)) : null;
        // Gaming exempts game packets in the packet policy, not every download on that PC.
        // Uploads continue to use ALL participants, independently of download activity.
        // Priorities apply only when every live peer announces one; a peer without it (older
        // release) assumes equal shares, so weighting then could overcommit the pool.
        var weighted = live.All(p => p.ShareWeight is { } w && WireAdvertisement.ValidWeight(w));
        var localWeight = weighted ? _settings.ShareWeight : 1;
        var totalWeight = weighted ? localWeight + live.Sum(p => p.ShareWeight!.Value) : participants;
        var localActivityKnown = probe is not null && probe.TimestampUtc <= at.AddSeconds(1) &&
            at - probe.TimestampUtc < _settings.PeerTtl && ValidLimitedLoad(probe.LimitedDownloadMbps);
        var activityComplete = localActivityKnown && live.All(p => ValidLimitedLoad(p.LimitedDownloadMbps));
        var downloadParticipants = participants;
        var downloadWeight = totalWeight;
        var minimumDownloadWeight = weighted ? Math.Min(localWeight, live.Select(p => p.ShareWeight!.Value).DefaultIfEmpty(localWeight).Min()) : 1;
        if (activityComplete)
        {
            var downloading = live.Where(p => p.LimitedDownloadMbps > .05).ToArray();
            // Always include self prospectively. An idle shaper therefore has a bounded
            // starting rate and can report activity without needing an unbounded lane.
            // Already active devices converge after the next heartbeat/planning round;
            // this is not an atomic cross-device token bucket during simultaneous starts.
            downloadParticipants = downloading.Length + 1;
            downloadWeight = localWeight + downloading.Sum(p => weighted ? p.ShareWeight!.Value : 1);
            minimumDownloadWeight = weighted ? Math.Min(localWeight, downloading.Select(p => p.ShareWeight!.Value).DefaultIfEmpty(localWeight).Min()) : 1;
        }
        double? automatic = pool is { } shared ? Math.Max(0.1, shared * localWeight / downloadWeight) : null;
        var allocationRepresentable = pool is null || pool * minimumDownloadWeight / downloadWeight >= .1;
        // Current activity establishes support for active allocation.
        // Capacity consumers (phones) need not invent proposals to participate; legacy
        // peers without this telemetry retain the conservative all-proposal check.
        var complete = (activityComplete && proposals.Count > 0 || proposals.Count == participants) && allocationRepresentable;
        // A manual maximum may be lower than the shared allocation. Adapt both
        // the load threshold and the applied rate from that actual starting budget.
        var adaptiveBudget = automatic;
        if (adaptiveBudget is { } allocation && _settings.ManualDownloadLimitMbps is { } manualMaximum)
            adaptiveBudget = Math.Min(allocation, manualMaximum);

        if (!active || !_settings.AdaptiveEnabled)
        {
            _factor = 1; _high = _low = 0; _peerAdjusted = false;
            _peerHigh = _peerLow = 0; _feedback.Clear(); _gamingPeers.Clear();
            _lastLocalRecoveryRound = default;
            _reductionProbe = null; _restoreFactor = null; _failedEpisodes.Clear(); _observedSources.Clear();
        }
        if (_settings.AdaptiveEnabled)
        {
            var gamingPeers = live.Where(p => p.IsGaming).ToArray();
            var hasPeerBaseline = automatic is not null && gamingPeers.Any(p => ValidBaseline(p, at));
            // A local gamer is itself an authoritative latency source. Remote healthy baselines
            // must not suppress local congestion, or let local health release a remote reduction.
            Observe(probe, at, active, adaptiveBudget, localGaming || !hasPeerBaseline, !hasPeerBaseline);
            if (adaptiveBudget is { } cap) ObserveGamingPeers(gamingPeers, probe, at, cap, hasPeerBaseline,
                localGaming, !localGaming || LocalGameRecovered(probe, at));
        }
        MaintainReduction(at);
        // Round shared budgets down so independent PCs cannot round their sum above the reserve.
        var limit = adaptiveBudget is { } budget
            ? Math.Floor(Math.Max(0.1, budget * _factor) * 100) / 100
            : _settings.ManualDownloadLimitMbps;
        var reason = automatic is not null
            ? (_factor < 0.999 ? "Große Downloads begrenzt · Spiele frei · Latenzreserve erhöht" : "Große Downloads begrenzt · Spiele frei · LAN-Budget geteilt")
            : active && capacity is null ? "Gaming erkannt · keine bestätigte Kapazität · kein automatisches Limit"
            : "Kein Gaming aktiv · automatische Begrenzung frei";
        if (_settings.ManualDownloadLimitMbps is not null) reason += " · manuelles Limit";
        if (active && !complete) reason += !allocationRepresentable
            ? " · gemeinsames Budget unter technischem Mindestlimit"
            : " · gemeinsames Budget noch nicht mit allen PCs abgestimmt";
        return new(limit is { } value ? Math.Round(value, 2) : null, reason, active, _baseline, _factor < 0.999,
            _baseline is null ? null : _baselineAt, pool, participants, gamers, capacity, complete, totalWeight, localWeight,
            uploadCapacity, uploadPool, downloadParticipants, activityComplete, downloadWeight);
    }

    private static bool ValidLimitedLoad(double? load) => load is >= 0 and <= 100000 && double.IsFinite(load.Value);

    private void Observe(ProbeStats? probe, DateTimeOffset now, bool gaming, double? budget, bool adjust, bool allowRecovery)
    {
        if (_baseline is not null && now - _baselineAt > BaselineLifetime)
        {
            _baseline = null; _idle.Clear(); _high = _low = 0;
            if (!_peerAdjusted) _factor = 1;
        }
        if (probe is null || probe.TimestampUtc <= _lastProbe || probe.TimestampUtc > now.AddSeconds(1) ||
            now - probe.TimestampUtc > TimeSpan.FromSeconds(10)) return;
        _lastProbe = probe.TimestampUtc;
        if (_target != probe.TargetId)
        {
            _target = probe.TargetId; _idle.Clear(); _baseline = null; _high = _low = 0;
            if (!_peerAdjusted && adjust) _factor = 1;
        }
        if (!probe.IsGamingRelevant || string.IsNullOrWhiteSpace(probe.TargetId) ||
            probe.ExternalLatencyMs is not { } latency || !double.IsFinite(latency) || latency < 0 || latency > 10000 ||
            (probe.LimitedDownloadMbps ?? probe.DownloadMbps) is not { } load || !double.IsFinite(load) || load < 0)
        { _high = _low = 0; return; }
        if (!gaming && load <= _settings.LineMbps * 0.15)
        {
            _idle.Add((now, latency));
            _idle.RemoveAll(s => now - s.At > TimeSpan.FromMinutes(5));
            if (_idle.Count > 30) _idle.RemoveAt(0);
            if (_idle.Count >= 5 && _idle[^1].At - _idle[0].At >= TimeSpan.FromSeconds(8))
            {
                var sorted = _idle.Select(s => s.Latency).Order().ToArray();
                _baseline = sorted[(sorted.Length - 1) / 4];
                _baselineAt = now;
            }
            return;
        }
        if (!adjust || budget is not { } cap || _baseline is not { } baseline)
        { _high = _low = 0; return; }
        var source = FormattableString.Invariant($"local|{_target}|{baseline:R}|{_baselineAt:O}");
        var elevated = latency > baseline + Math.Max(8, baseline * 0.35) && load >= cap * _factor * 0.75;
        var recovered = latency < baseline + Math.Max(4, baseline * 0.15);
        ObserveReduction(source, latency - baseline, now, load >= cap * _factor * .75,
            load <= cap * _factor * .25, recovered);
        _high = elevated ? _high + 1 : 0;
        _low = recovered ? _low + 1 : 0;
        if (now - _lastAdjustment < TimeSpan.FromSeconds(10)) return;
        if (_high >= 3 && TryReduce(source, latency - baseline, now)) { _high = 0; }
        // Once genuine gaming feedback caused a reduction, losing it cannot turn
        // an unrelated local ICMP proxy into evidence of gamer recovery.
        else if (_low >= 3 && _factor < 1 && !_peerAdjusted && allowRecovery)
        { _factor = Math.Min(1, _factor + 0.1); _lastAdjustment = now; _low = 0; }
    }

    private void ObserveGamingPeers(PeerAdvertisement[] gamers, ProbeStats? local,
        DateTimeOffset now, double budget, bool hasBaseline, bool localGaming, bool localRecovered)
    {
        var ids = gamers.Select(p => p.DeviceId).ToHashSet();
        if (!_gamingPeers.SetEquals(ids))
        {
            ResetPeerWindow();
            _gamingPeers.Clear(); _gamingPeers.UnionWith(ids);
        }
        foreach (var removed in _feedback.Keys.Where(id => !ids.Contains(id)).ToArray()) _feedback.Remove(removed);
        if (!hasBaseline) { ResetPeerWindow(); return; }

        var allValid = true;
        // Reset before accepting this round: doing it halfway through the peer
        // loop would incorrectly consume earlier peers' fresh recovery samples.
        if (gamers.Any(peer => ValidBaseline(peer, now) && ValidLatency(peer.LatencyMs) &&
            peer.LatencySampleSequence is not <= 0 && _feedback.TryGetValue(peer.DeviceId, out var prior) &&
            prior.LastSeen != default && now - prior.LastSeen >= _settings.PeerTtl && peer.LastSeenUtc > prior.LastSeen &&
            (peer.LatencySampleSequence is null || prior.LastSequence is null || peer.LatencySampleSequence > prior.LastSequence)))
            ResetPeerWindow();
        var valid = new List<(PeerFeedback State, double Baseline, double Latency, bool IsNew)>();
        foreach (var peer in gamers)
        {
            if (!ValidBaseline(peer, now) || !ValidLatency(peer.LatencyMs) || peer.LatencySampleSequence is <= 0)
            { allValid = false; continue; }
            if (!_feedback.TryGetValue(peer.DeviceId, out var state) || state.Session != peer.SessionId || state.Target != peer.LatencyTargetId ||
                state.Baseline != peer.IdleBaselineMs || state.BaselineAt != peer.IdleBaselineAtUtc)
            {
                state = new PeerFeedback(peer.SessionId, peer.LatencyTargetId!, peer.IdleBaselineMs!.Value, peer.IdleBaselineAtUtc!.Value);
                _feedback[peer.DeviceId] = state;
                ResetPeerWindow();
            }
            var isNew = peer.LastSeenUtc > state.LastSeen &&
                (peer.LatencySampleSequence is null || state.LastSequence is null || peer.LatencySampleSequence > state.LastSequence);
            if (isNew)
            {
                state.LastSeen = peer.LastSeenUtc;
                state.LastSequence = peer.LatencySampleSequence;
                state.Revision++;
            }
            valid.Add((state, peer.IdleBaselineMs!.Value, peer.LatencyMs!.Value, isNew));
        }

        static bool High((PeerFeedback State, double Baseline, double Latency, bool IsNew) value) =>
            value.Latency - value.Baseline > Math.Max(8, value.Baseline * .35);
        static bool Recovered((PeerFeedback State, double Baseline, double Latency, bool IsNew) value) =>
            value.Latency - value.Baseline < Math.Max(4, value.Baseline * .15);

        var source = "peers|" + string.Join(";", gamers.OrderBy(p => p.DeviceId).Select(p =>
            FormattableString.Invariant($"{p.DeviceId}|{p.SessionId}|{p.LatencyTargetId}|{p.IdleBaselineMs:R}|{p.IdleBaselineAtUtc:O}")));
        var excess = valid.Count == 0 ? 0 : valid.Max(v => v.Latency - v.Baseline);
        // Each gamer must contribute a new sample to an efficacy round. Repeated
        // heartbeats and a healthy subset cannot stand in for a missing gamer.
        var localLoadKnown = local is not null && local.TimestampUtc <= now.AddSeconds(1) &&
            now - local.TimestampUtc <= TimeSpan.FromSeconds(10) &&
            (local.LimitedDownloadMbps ?? local.DownloadMbps) is { } load && double.IsFinite(load) && load >= 0;
        var localLoad = local?.LimitedDownloadMbps ?? local?.DownloadMbps;
        var localLoadValid = localLoadKnown && localLoad >= budget * _factor * .75;
        if (allValid && valid.Count == gamers.Length && valid.All(v => v.State.Revision > v.State.EffectRevision))
        {
            foreach (var item in valid) item.State.EffectRevision = item.State.Revision;
            ObserveReduction(source, excess, now, localLoadValid,
                localLoadKnown && localLoad <= budget * _factor * .25, valid.All(Recovered));
        }

        var newElevated = valid.Any(v => v.IsNew && High(v)) && localLoadValid;
        if (newElevated)
        {
            if (_peerHigh++ == 0) _highSince = now;
        }
        else if (!localLoadValid || !valid.Any(High)) { _peerHigh = 0; _highSince = default; }

        // Recovery requires an entirely new complete round from every current
        // gamer. A healthy peer cannot repeatedly vouch for a missing/old sample.
        var recovered = localRecovered && allValid && valid.Count == gamers.Length && valid.Count > 0 && valid.All(Recovered);
        if (!recovered)
        {
            _peerLow = 0; _lowSince = default;
            foreach (var item in valid) item.State.RecoveryRevision = item.State.Revision;
        }
        var newRecoveryRound = recovered && valid.All(v => v.State.Revision > v.State.RecoveryRevision) &&
            (!localGaming || local!.TimestampUtc > _lastLocalRecoveryRound);
        if (newRecoveryRound)
        {
            if (localGaming) _lastLocalRecoveryRound = local!.TimestampUtc;
            foreach (var item in valid) item.State.RecoveryRevision = item.State.Revision;
            if (_peerLow++ == 0) _lowSince = now;
        }

        if (newElevated && _peerHigh >= 3 && now - _highSince >= TimeSpan.FromSeconds(10) &&
            now - _lastAdjustment >= TimeSpan.FromSeconds(10) && TryReduce(source, excess, now))
        {
            _peerAdjusted = true;
            ResetPeerWindow();
        }
        else if (newRecoveryRound && _peerLow >= 5 && now - _lowSince >= TimeSpan.FromSeconds(20) && _factor < 1 &&
            now - _lastAdjustment >= TimeSpan.FromSeconds(20))
        {
            _factor = Math.Min(1, _factor + .05);
            _lastAdjustment = now;
            ResetPeerWindow();
        }
    }

    private bool TryReduce(string source, double excess, DateTimeOffset now)
    {
        if (_failedEpisodes.ContainsKey(source) || _restoreFactor is not null ||
            _reductionProbe is { Effective: false }) return false;
        var reduced = Math.Max(.1, _factor * .85);
        if (reduced >= _factor) return false;
        _reductionProbe = new(source, excess, now, _factor);
        _observedSources.Clear(); _observedSources[source.StartsWith("local|", StringComparison.Ordinal)] = source;
        _factor = reduced;
        _lastAdjustment = _lastTrustedEvidence = now;
        return true;
    }

    private void ObserveReduction(string source, double excess, DateTimeOffset now, bool loaded, bool idle, bool recovered)
    {
        _observedSources[source.StartsWith("local|", StringComparison.Ordinal)] = source;
        // A failed experiment is not retried merely because time passes. A new
        // measured low-load or recovered-ping phase must first end that episode.
        if (_failedEpisodes.TryGetValue(source, out var failed))
        {
            var resetKind = recovered ? 1 : idle ? 2 : 0;
            if (resetKind == 0 || failed.ResetKind != resetKind)
            { failed.Count = 0; failed.Since = now; failed.ResetKind = resetKind; }
            if (resetKind != 0 && ++failed.Count >= 3 && now - failed.Since >= TimeSpan.FromSeconds(10))
                _failedEpisodes.Remove(source);
        }
        if (_reductionProbe is not { } probe || probe.Source != source) return;
        _lastTrustedEvidence = now;
        if (probe.Effective || now <= probe.Started) return;
        var improved = loaded && excess <= probe.Excess - Math.Max(2, probe.Excess * .15);
        if (!improved) { probe.ImprovedCount = 0; probe.ImprovedSince = default; return; }
        if (probe.ImprovedCount++ == 0) probe.ImprovedSince = now;
        if (probe.ImprovedCount >= 3 && now - probe.ImprovedSince >= TimeSpan.FromSeconds(10))
            probe.Effective = true;
    }

    private void MaintainReduction(DateTimeOffset now)
    {
        if (_factor >= 1) { _reductionProbe = null; _restoreFactor = null; return; }
        // No fresh comparison to the source that caused a reduction is not proof
        // of ongoing congestion. Bound that hold, even if another source is alive.
        double? restore = now - _lastTrustedEvidence >= EffectWindow ? 1
            : _reductionProbe is { Effective: false } probe && now - probe.Started >= EffectWindow ? probe.BeforeFactor : null;
        if (restore is { } target && (_restoreFactor is null || target > _restoreFactor))
        {
            _restoreFactor = target;
            _lastSafetyRecovery = now - TimeSpan.FromSeconds(10);
            // Local and remote observations of the same failed experiment must
            // not take turns restarting it after rollback.
            foreach (var source in _observedSources.Values) _failedEpisodes.TryAdd(source, new(now));
            // Keep current episode locks regardless of age. Only obsolete source
            // signatures may be evicted; time alone never restarts a failed trial.
            foreach (var key in _failedEpisodes.Where(pair => !_observedSources.ContainsValue(pair.Key))
                         .OrderBy(pair => pair.Value.RecordedAt).Take(Math.Max(0, _failedEpisodes.Count - 64))
                         .Select(pair => pair.Key).ToArray())
                _failedEpisodes.Remove(key);
        }
        if (_restoreFactor is not { } ceiling) return;
        if (now - _lastSafetyRecovery >= TimeSpan.FromSeconds(10))
        {
            _factor = Math.Max(_factor, Math.Min(ceiling, _factor + .1));
            _lastSafetyRecovery = _lastAdjustment = now;
        }
        if (_factor >= ceiling)
        {
            _restoreFactor = null;
            if (_reductionProbe is { } prior) prior.Effective = true;
            if (_factor >= 1) { _reductionProbe = null; _peerAdjusted = false; }
        }
    }

    private sealed class FailedEpisode(DateTimeOffset recordedAt)
    {
        internal DateTimeOffset RecordedAt { get; } = recordedAt;
        internal int ResetKind, Count;
        internal DateTimeOffset Since;
    }

    private sealed class ReductionProbe(string source, double excess, DateTimeOffset started, double beforeFactor)
    {
        internal string Source { get; } = source;
        internal double Excess { get; } = excess;
        internal DateTimeOffset Started { get; } = started;
        internal double BeforeFactor { get; } = beforeFactor;
        internal bool Effective { get; set; }
        internal int ImprovedCount { get; set; }
        internal DateTimeOffset ImprovedSince { get; set; }
    }

    private void ResetPeerWindow()
    {
        _peerHigh = _peerLow = 0;
        _highSince = _lowSince = default;
        foreach (var state in _feedback.Values) state.RecoveryRevision = state.Revision;
    }

    private static bool ValidLatency(double? latency) => latency is >= 0 and <= 10000 && double.IsFinite(latency.Value);

    private bool LocalGameRecovered(ProbeStats? probe, DateTimeOffset now) =>
        _baseline is { } baseline && probe is { IsGamingRelevant: true } && probe.TargetId == _target &&
        probe.TimestampUtc <= now.AddSeconds(1) && now - probe.TimestampUtc <= TimeSpan.FromSeconds(10) &&
        ValidLatency(probe.ExternalLatencyMs) && probe.ExternalLatencyMs < baseline + Math.Max(4, baseline * .15);

    private static bool ValidBaseline(PeerAdvertisement peer, DateTimeOffset now) =>
        ValidLatency(peer.IdleBaselineMs) && !string.IsNullOrWhiteSpace(peer.LatencyTargetId) &&
        peer.LatencyTargetId.Length <= 256 && !peer.LatencyTargetId.Any(char.IsControl) &&
        peer.IdleBaselineAtUtc is { } baselineAt && baselineAt <= now.AddSeconds(5) && now - baselineAt <= BaselineLifetime;

    private sealed class PeerFeedback(Guid? session, string target, double baseline, DateTimeOffset baselineAt)
    {
        internal Guid? Session { get; } = session;
        internal string Target { get; } = target;
        internal double Baseline { get; } = baseline;
        internal DateTimeOffset BaselineAt { get; } = baselineAt;
        internal DateTimeOffset LastSeen { get; set; }
        internal long? LastSequence { get; set; }
        internal long Revision { get; set; }
        internal long RecoveryRevision { get; set; }
        internal long EffectRevision { get; set; }
    }
}
