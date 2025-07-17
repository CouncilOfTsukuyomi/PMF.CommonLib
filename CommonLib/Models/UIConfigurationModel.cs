using System.ComponentModel.DataAnnotations;
using CommonLib.Attributes;

namespace CommonLib.Models;

public class UIConfigurationModel
{
    [Display(Name = "Enable Notifications", GroupName = "Notification", Description = "Display Notifications")]
    public bool NotificationEnabled { get; set; } = true;

    [Display(Name = "Enable Notification Sound", GroupName = "Notification", Description = "Sound for Notifications")]
    public bool NotificationSoundEnabled { get; set; } 
    [Display(Name = "Minimise To Tray", GroupName = "User Interface", Description = "Minimise the application to tray")]
    public bool MinimiseToTray { get; set; } = false;
    [Display(Name = "Minimise on Close", GroupName = "User Interface", Description = "Minimise the application when you click the X button")]
    public bool MinimiseOnClose { get; set; } = false;
    [Display(Name = "Show Notification Button", GroupName = "User Interface", Description = "Show the Notification Button")]
    public bool ShowNotificationButton { get; set; } = true;
    [ExcludeFromSettingsUI]
    public double NotificationButtonX { get; set; } = 20;
    [ExcludeFromSettingsUI]
    public double NotificationButtonY { get; set; } = 60;

}