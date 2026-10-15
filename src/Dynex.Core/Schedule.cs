namespace Dynex.Core;

/// <summary>Time-based rules: a nightly window without limits and an optional weekend profile.</summary>
public sealed record ScheduleSettings(bool NightFree = false, TimeOnly NightFrom = default, TimeOnly NightTo = default, string? WeekendProfile = null);

public readonly record struct ScheduleState(bool LimitsOff, string? Profile, string? Reason);

public static class Schedule
{
    public static ScheduleState Evaluate(ScheduleSettings settings, DateTime localNow)
    {
        if (settings.NightFree && settings.NightFrom != settings.NightTo && InWindow(TimeOnly.FromDateTime(localNow), settings.NightFrom, settings.NightTo))
            return new(true, null, $"Zeitplan: {settings.NightFrom:HH\\:mm}–{settings.NightTo:HH\\:mm} Uhr ohne Limits");
        if (settings.WeekendProfile is { Length: > 0 } profile && localNow.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return new(false, profile, $"Zeitplan: Wochenende mit Profil {profile}");
        return new(false, null, null);
    }

    /// <summary>Supports windows across midnight (e.g. 01:00–08:00 and 23:00–06:00).</summary>
    internal static bool InWindow(TimeOnly now, TimeOnly from, TimeOnly to) =>
        from < to ? now >= from && now < to : now >= from || now < to;
}
