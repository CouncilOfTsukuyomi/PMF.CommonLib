namespace CommonLib.Helper;

internal static class EnvironmentDetector
{
    public static bool IsRunningInTestEnvironment()
    {
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        
        return assemblies.Any(a => a.FullName?.StartsWith("xunit", StringComparison.OrdinalIgnoreCase) == true) ||
               assemblies.Any(a => a.FullName?.StartsWith("nunit", StringComparison.OrdinalIgnoreCase) == true) ||
               assemblies.Any(a => a.FullName?.StartsWith("Microsoft.VisualStudio.TestPlatform", StringComparison.OrdinalIgnoreCase) == true) ||
               Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ||
               Environment.GetEnvironmentVariable("TF_BUILD") == "True" ||
               Environment.GetEnvironmentVariable("GITHUB_ACTIONS") == "true" ||
               Environment.GetEnvironmentVariable("CI") == "true";
    }
}