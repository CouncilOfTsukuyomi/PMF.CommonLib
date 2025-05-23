﻿using CommonLib.Services;

namespace CommonLib.Interfaces;

public interface IUpdateService
{
    Task<List<string>> GetUpdateZipLinksAsync(string currentVersion, string repository);
    Task<bool> NeedsUpdateAsync(string currentVersion, string repository);
    Task<string> GetMostRecentVersionAsync(string repository);
    Task<UpdateService.GitHubRelease?> GetLatestReleaseAsync(bool includePrerelease, string repository);
}