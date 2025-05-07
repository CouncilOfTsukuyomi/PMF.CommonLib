﻿using CommonLib.Consts;
using CommonLib.Interfaces;
using Microsoft.Win32;
using NLog;

namespace CommonLib.Services
{
    public class RegistryHelper : IRegistryHelper
    {
        // Replace Serilog ILogger with NLog's Logger
        private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Indicates whether the registry is supported on the current platform.
        /// </summary>
        public bool IsRegistrySupported => OperatingSystem.IsWindows();

        public RegistryHelper()
        {
            // No additional initialization needed for NLog
        }

        /// <summary>
        /// Retrieves the TexTools installation location from the Windows Registry.
        /// </summary>
        /// <returns>The installation path of TexTools, or null if not found.</returns>
        /// <exception cref="PlatformNotSupportedException">Thrown when called on non-Windows platforms.</exception>
        public string GetTexToolRegistryValue()
        {
            if (!IsRegistrySupported)
            {
                throw new PlatformNotSupportedException("Registry access is only supported on Windows.");
            }

            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(RegistryConsts.RegistryPath);
                var value = key?.GetValue("InstallLocation")?.ToString();
                if (string.IsNullOrEmpty(value))
                {
                    _logger.Warn("Registry value not found at {Path}", RegistryConsts.RegistryPath);
                    return null;
                }
                return value;
            }
            catch (Exception e)
            {
                _logger.Error(e, "Failed to get registry value for {Path}", RegistryConsts.RegistryPath);
                throw new Exception($"Failed to get registry value for {RegistryConsts.RegistryPath}", e);
            }
        }
    }
}