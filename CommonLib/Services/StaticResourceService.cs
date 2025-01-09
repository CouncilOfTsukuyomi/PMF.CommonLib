using System.Text;
using Newtonsoft.Json;
using PenumbraModForwarder.Common.Extensions;
using PenumbraModForwarder.Common.Interfaces;
using PenumbraModForwarder.Common.Models;

namespace PenumbraModForwarder.Common.Services;

public class StaticResourceService : IStaticResourceService
{
    private const string GitHubApiBaseUrl = "https://api.github.com/repos/CouncilOfTsukuyomi/StaticResources/contents/";
    private const string GitHubApiRefQuery = "?ref=main";

    private readonly HttpClient _httpClient;

    public StaticResourceService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }
        
    private class GitHubContentResponse
    {
        [JsonProperty("content")]
        public string? Content { get; set; }

        [JsonProperty("encoding")]
        public string? Encoding { get; set; }
    }
        
    public async Task<(GithubStaticResources.InformationJson?, GithubStaticResources.UpdaterInformationJson?)> GetResourcesUsingGithubApiAsync()
    {
        var info = await GetInformationJsonAsync();
        GithubStaticResources.UpdaterInformationJson? updater = null;
        
        if (!string.IsNullOrEmpty(info?.UpdaterInfo))
        {
            updater = await GetUpdaterInformationJsonAsync(info.UpdaterInfo);
        }
        
        return (info, updater);
    }

    private async Task<GithubStaticResources.InformationJson?> GetInformationJsonAsync()
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GitHubApiBaseUrl}information.json{GitHubApiRefQuery}");
        request.Headers.Add("User-Agent", "PenumbraModForwarder");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var githubResponse = await response.Content.ReadAsJsonAsync<GitHubContentResponse>();
        if (githubResponse?.Content == null || githubResponse.Encoding != "base64")
            return null;

        var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(githubResponse.Content));
        return JsonConvert.DeserializeObject<GithubStaticResources.InformationJson>(decodedJson);
    }

    private async Task<GithubStaticResources.UpdaterInformationJson?> GetUpdaterInformationJsonAsync(string path)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{GitHubApiBaseUrl}{path}{GitHubApiRefQuery}");
        request.Headers.Add("User-Agent", "PenumbraModForwarder");
        request.Headers.Add("Accept", "application/vnd.github.v3+json");

        var response = await _httpClient.SendAsync(request);
        if (!response.IsSuccessStatusCode)
            return null;

        var githubResponse = await response.Content.ReadAsJsonAsync<GitHubContentResponse>();
        if (githubResponse?.Content == null || githubResponse.Encoding != "base64")
            return null;

        var decodedJson = Encoding.UTF8.GetString(Convert.FromBase64String(githubResponse.Content));
        var updaterInfo = JsonConvert.DeserializeObject<GithubStaticResources.UpdaterInformationJson>(decodedJson);
        
        var folderPath = Path.GetDirectoryName(path) ?? string.Empty;
        var rawUrlBase = "https://raw.githubusercontent.com/CouncilOfTsukuyomi/StaticResources/refs/heads/main/";

        if (updaterInfo?.Backgrounds?.Images != null)
        {
            var imagesList = updaterInfo.Backgrounds.Images.ToList();

            for (int i = 0; i < imagesList.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(imagesList[i]) && imagesList[i].StartsWith("./"))
                {
                    imagesList[i] = imagesList[i].TrimStart('.', '/');
                }

                imagesList[i] = $"{rawUrlBase}{folderPath}/{imagesList[i]}";
            }

            updaterInfo.Backgrounds.Images = imagesList.ToArray();
        }

        return updaterInfo;
    }
}