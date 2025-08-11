namespace CommonLib.Models;

public record ModFocusData(string Path, string Name)
{
    public ModFocusData()
        : this(string.Empty, string.Empty)
    { }
}