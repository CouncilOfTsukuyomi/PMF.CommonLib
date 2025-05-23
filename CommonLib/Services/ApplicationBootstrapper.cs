﻿namespace CommonLib.Services;

public static class ApplicationBootstrapper
{
    private static bool _isInitializedByWatchdog;
    private static readonly bool _isDevMode;

    static ApplicationBootstrapper()
    {
        _isDevMode = Environment.GetEnvironmentVariable("DEV_MODE") == "true";
    }

    public static void SetWatchdogInitialization()
    {
        _isInitializedByWatchdog = true;
    }
}