﻿using System.ComponentModel.DataAnnotations;
using CommonLib.Attributes;
using CommonLib.Consts;

namespace CommonLib.Models;

public class BackgroundWorkerConfigurationModel
{
    [Display(Name = "Auto Delete Files", GroupName = "General", Description = "Automatically delete files after we are done with them")]
    public bool AutoDelete { get; set; } = true;

    [Display(Name = "Install All Mods", GroupName = "General", Description = "Install every mod inside an archive")]
    public bool InstallAll { get; set; }

    [Display(Name = "Mod Processing Path", GroupName = "Pathing", Description = "Where to move the mods to for processing (This should not be the same as your Penumbra Path)")]
    public string ModFolderPath { get; set; } = ConfigurationConsts.ModsPath;

    [ExcludeFromSettingsUI] private List<string> _downloadPath = [];
    
    [Display(Name = "Download Path", GroupName = "Pathing", Description = "The path to check for modded files")]
    public List<string> DownloadPath
    {
        get => _downloadPath;
        set => _downloadPath = value.Distinct().ToList();
    }

    [Display(Name = "TexTool ConsoleTools.exe Path", GroupName = "Pathing", Description = "The path to Textool's Console Tools.exe")]
    public string TexToolPath { get; set; } = string.Empty;
    [Display(Name = "Skip Endwalker and below mods", GroupName = "General", Description = "Skip endwalker and below mods")]
    public bool SkipPreDt  { get; set; } = true;
    [ExcludeFromSettingsUI]
    public string PenumbraModFolderPath { get; set; } = string.Empty;
    [Display(Name = "Relocate Mod/Archive Files", GroupName = "General", Description = "Relocate mod/archive files when they are detected")]
    public bool RelocateFiles { get; set; } = true;
}