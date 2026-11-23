using System.Globalization;

namespace Plicbox.Core;

public static class MessageDates
{
    /// <summary>
    /// The message list's date heading, like eM Client: Today, Yesterday, this week's days, Last Week, Earlier This
    /// Month, Last Month, then this year's months by name ("August") and one heading per earlier year ("2019").
    /// </summary>
    public static string Group(DateTime day, DateTime today)
    {
        day = day.Date;
        today = today.Date;
        var days = (today - day).TotalDays;
        if (days <= 0) return L.T("Today");
        if (days == 1) return L.T("Yesterday");
        var weekStart = today.AddDays(-(((int)today.DayOfWeek + 6) % 7)); // Monday
        if (day >= weekStart) return day.ToString("dddd", CultureInfo.CurrentCulture);
        if (day >= weekStart.AddDays(-7)) return L.T("Last Week");
        if (day.Year == today.Year && day.Month == today.Month) return L.T("Earlier This Month");
        if (day >= new DateTime(today.Year, today.Month, 1).AddMonths(-1)) return L.T("Last Month");
        if (day.Year == today.Year) return day.ToString("MMMM", CultureInfo.CurrentCulture);
        return day.Year.ToString(CultureInfo.InvariantCulture);
    }
}
