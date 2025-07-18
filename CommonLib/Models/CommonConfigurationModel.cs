using System.ComponentModel.DataAnnotations;
using CommonLib.Attributes;

namespace CommonLib.Models;

public class CommonConfigurationModel
{
    [Display(Name = "Enable Double Click File Support", GroupName = "General", Description = "Link double clicking files to running in Atomos")]
    public bool FileLinkingEnabled { get; set; }
    [Display(Name = "Start on Boot", GroupName = "General", Description = "Start on Computer Boot")]
    public bool StartOnBoot { get; set; }
    [Display(Name = "Enable Beta Builds", GroupName = "Updates", Description = "Enable Beta Builds")]
    public bool IncludePrereleases { get; set; }
    [Display(Name = "Start on FFXIV Boot", GroupName = "General", Description = "Put Atomos into xivLauncher's config to make it run")]
    public bool StartOnFfxivBoot { get; set; }
    [Display(Name = "Enable Sentry", GroupName = "General", Description = "This will send crash logs to use so we can view them")]
    public bool EnableSentry { get; set; }
    [ExcludeFromSettingsUI]
    public bool UserChoseSentry { get; set; }
    [ExcludeFromSettingsUI] 
    public bool FirstRunComplete { get; set; } = false;
}