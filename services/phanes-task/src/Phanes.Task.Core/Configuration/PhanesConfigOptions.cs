namespace Phanes.Task.Core.Configuration;

/// <summary>
/// Where to pull dynamic configuration from. Bound from
/// "PhanesTask:ConfigService". Empty Url = disabled (appsettings only).
/// </summary>
public sealed class PhanesConfigOptions
{
    public string Url         { get; set; } = string.Empty;
    public string Namespace   { get; set; } = "phanes-task";
    public string Key         { get; set; } = "options";
    public double PollSeconds { get; set; } = 15;
}
