using System.Globalization;
using Dafa.Contracts.Core.Abstractions;

namespace Dafa.Contracts.Core.Domain;

/// <summary>Next run times for Off / Hourly / Daily / Weekly schedules, in Europe/Copenhagen local time.</summary>
public static class ScheduleCalculator
{
    public static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }
        var parts = text.Trim().Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var hours)
            || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes)
            || hours < 0 || hours > 23 || minutes < 0 || minutes > 59)
        {
            return false;
        }
        time = new TimeSpan(hours, minutes, 0);
        return true;
    }

    /// <summary>The first run strictly after <paramref name="afterUtc"/>, or null when the schedule is off. day: 1 = Monday ... 7 = Sunday.</summary>
    public static DateTime? NextRun(ScheduleFrequency frequency, string? time, int? day, DateTime afterUtc) =>
        NextRun(frequency, time, day, afterUtc, UtcClock.BusinessTimeZone);

    public static DateTime? NextRun(ScheduleFrequency frequency, string? time, int? day, DateTime afterUtc, TimeZoneInfo zone)
    {
        if (frequency == ScheduleFrequency.Off)
        {
            return null;
        }
        var utc = DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc);
        if (frequency == ScheduleFrequency.Hourly)
        {
            return utc.AddHours(1);
        }
        if (!TryParseTime(time, out var timeOfDay))
        {
            timeOfDay = new TimeSpan(6, 0, 0);
        }
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        var candidate = DateTime.SpecifyKind(local.Date + timeOfDay, DateTimeKind.Unspecified);
        if (frequency == ScheduleFrequency.Weekly)
        {
            // day: 1 = Monday ... 7 = Sunday (CONTRACT_V3); DayOfWeek: 0 = Sunday.
            var wanted = Math.Clamp(day ?? 1, 1, 7) % 7;
            var shift = (wanted - (int)candidate.DayOfWeek + 7) % 7;
            candidate = candidate.AddDays(shift);
            if (candidate <= local)
            {
                candidate = candidate.AddDays(7);
            }
        }
        else if (candidate <= local)
        {
            candidate = candidate.AddDays(1);
        }
        return ToUtc(candidate, zone);
    }

    private static DateTime ToUtc(DateTime local, TimeZoneInfo zone)
    {
        // A time that does not exist (spring forward) moves one hour later.
        for (var i = 0; i < 3; i++)
        {
            if (!zone.IsInvalidTime(local))
            {
                return TimeZoneInfo.ConvertTimeToUtc(local, zone);
            }
            local = local.AddHours(1);
        }
        return TimeZoneInfo.ConvertTimeToUtc(local, zone);
    }

    public static ScheduleFrequency ParseFrequency(string? text, ScheduleFrequency fallback)
    {
        if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse<ScheduleFrequency>(text.Trim(), true, out var value)
            && Enum.IsDefined(typeof(ScheduleFrequency), value))
        {
            return value;
        }
        return fallback;
    }
}
