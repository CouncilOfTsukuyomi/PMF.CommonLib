namespace CommonLib.Interfaces;

public interface IPenumbraService
{
    void InitializePenumbraPath();
    (string folderPath, string modName) InstallMod(string sourceFilePath);
}