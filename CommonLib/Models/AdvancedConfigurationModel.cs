﻿using System.ComponentModel.DataAnnotations;

namespace PenumbraModForwarder.Common.Models;

public class AdvancedConfigurationModel
{
    [Display(Name = "Minimise To Tray", GroupName = "Mod Forwarder", Description = "Minimise the Main Window to tray")]
    public bool MinimiseToTray { get; set; } = false;
    [Display(Name = "Penumbra Api Timeout (Seconds)", GroupName = "Penumbra", Description = "How long to wait for the Penumbra Api to respond")]
    public int PenumbraTimeOutInSeconds { get; set; } = 60;
    [Display(Name = "Show Watchdog Window", GroupName = "Advanced", Description = "Show the console application window for Watchdog")]
    public bool ShowWatchDogWindow { get; set; } = false;
    [Display(Name = "Enable Debug Logs", GroupName = "Advanced", Description = "Enable Debug Logs")]
    public bool EnableDebugLogs { get; set; }
    [Display(Name = "XIV Mod Archive Cookie", GroupName = "Advanced", Description = "Allow the display of NSFW mods")]
    public string XIVModArchiveCookie { get; set; }
}