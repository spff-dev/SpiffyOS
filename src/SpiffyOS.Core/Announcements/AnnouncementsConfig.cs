namespace SpiffyOS.Core.Announcements;

public sealed class AnnouncementsConfig
{
    public bool Enabled { get; set; } = true;
    public bool OnlineOnly { get; set; } = true;
    public double MinGapMinutes { get; set; } = 30;

    public QuietHoursConfig? QuietHours { get; set; } = null;

    public ActivityConfig Activity { get; set; } = new();

    public List<AnnouncementMessage> Messages { get; set; } = new();
}

public sealed class ActivityConfig
{
    public bool Enabled { get; set; } = false;
    public double NoChatMinutes { get; set; } = 10;
}

public sealed class QuietHoursConfig
{
    // "HH:mm" 24-hour time, local to `Timezone`, e.g. "00:00" .. "08:00"
    public string Start { get; set; } = "00:00";
    public string End { get; set; } = "08:00";
    // IANA identifier, e.g. "Europe/London"
    public string Timezone { get; set; } = "Europe/London";
}

public sealed class AnnouncementMessage
{
    public string Text { get; set; } = "";
    public double MinIntervalMinutes { get; set; } = 30;
    public int Weight { get; set; } = 1;
}
