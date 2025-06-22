﻿using CommonLib.Models;

namespace CommonLib.Interfaces;

public interface IAria2Service
{
    string Aria2Folder { get; }
    string Aria2ExePath { get; }
        
    Task<bool> EnsureAria2AvailableAsync(CancellationToken ct);
        
    Task<bool> DownloadFileAsync(
        string fileUrl, 
        string downloadDirectory, 
        CancellationToken ct,
        IProgress<DownloadProgress>? progress = null);
}