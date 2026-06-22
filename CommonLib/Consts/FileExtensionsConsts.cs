namespace CommonLib.Consts;

public static class FileExtensionsConsts
{
    public static readonly string[] ModFileTypes = { ".pmp", ".ttmp2", ".ttmp" };
    public static readonly string[] ArchiveFileTypes = { ".zip", ".rar", ".7z" };
    public static readonly string[] PoseFileTypes = { ".pose" };

    public static readonly string[] AllowedExtensions = ModFileTypes.Concat(ArchiveFileTypes).Concat(PoseFileTypes).ToArray();
}