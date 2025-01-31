﻿using System.ComponentModel.DataAnnotations;

namespace PenumbraModForwarder.Common.Models;

public class UIConfigurationModel
{
    [Display(Name = "Enable Notifications", GroupName = "Notification", Description = "Display Notifications")]
    public bool NotificationEnabled { get; set; } = true;

    [Display(Name = "Enable Notification Sound", GroupName = "Notification", Description = "Sound for Notifications")]
    public bool NotificationSoundEnabled { get; set; } 
    [Display(Name = "Minimise To Tray", GroupName = "User Interface", Description = "Minimise the Main Window to tray")]
    public bool MinimiseToTray { get; set; } = false;
}