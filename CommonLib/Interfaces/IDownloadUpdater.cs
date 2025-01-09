namespace PenumbraModForwarder.Common.Interfaces;

public interface IDownloadUpdater
{
    Task<string?> DownloadAndExtractLatestUpdaterAsync(string outputFolder, CancellationToken ct);
}