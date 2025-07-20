using CommonLib.Interfaces;

namespace CommonLib.Services;

public class FileSystemHelper : IFileSystemHelper
{
    public bool FileExists(string path)
    {
        return File.Exists(path);
    }

    public IEnumerable<string> GetStandardTexToolsPaths()
    {
        var standardPaths = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            // Add standard Windows installation paths
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);

            standardPaths.Add(Path.Combine(programFiles, "FFXIV TexTools", "FFXIV_TexTools", "ConsoleTools.exe"));
            standardPaths.Add(Path.Combine(programFilesX86, "FFXIV TexTools", "FFXIV_TexTools", "ConsoleTools.exe"));
        }

        else if (OperatingSystem.IsLinux())
        {
            // Path used on a commonly used Linux install script.
            standardPaths.Add(Path.Combine("opt", "tt", "consoletools"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            // TODO: These need to be double checked
            // Add standard Mac installation paths
            standardPaths.Add("/usr/local/bin/FFXIV_TexTools/ConsoleTools");
            standardPaths.Add("/usr/bin/FFXIV_TexTools/ConsoleTools");
            // Add other common paths as needed
        }



    return standardPaths;
    }
}