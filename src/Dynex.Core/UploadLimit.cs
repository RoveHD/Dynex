namespace Dynex.Core;

/// <summary>
/// Upload budget for large non-game uploads (cloud sync, backups, uploads in a browser) while someone games.
/// Uses the same background share and PC priorities as downloads. Games and voice are never limited;
/// the helper only paces sustained bulk flows of other programs.
/// </summary>
public static class UploadLimit
{
    public static double? Compute(LimitDecision decision, double? uploadCapacityMbps, double backgroundPercent,
        double? manualLimitMbps, bool enabled, bool paused)
    {
        if (!enabled || paused || !decision.GamingActive) return null;
        double? automatic = null;
        var pool = decision.SharedUploadBudgetMbps;
        if (pool is null && uploadCapacityMbps is { } capacity && double.IsFinite(capacity) && capacity > 0)
            pool = capacity * Math.Clamp(backgroundPercent, 10, 95) / 100;
        if (pool is { } available && double.IsFinite(available) && available >= 0)
        {
            var total = decision.ShareWeightTotal is > 0 ? decision.ShareWeightTotal.Value : Math.Max(1, decision.ParticipantCount);
            var share = available * decision.LocalShareWeight / total;
            automatic = Math.Floor(Math.Max(0.5, share) * 100) / 100;
        }
        if (manualLimitMbps is { } manual && double.IsFinite(manual) && manual >= 0.1)
            return automatic is { } auto ? Math.Min(auto, manual) : manual;
        return automatic;
    }
}
