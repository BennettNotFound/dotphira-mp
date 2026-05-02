using System.Text.Json;

namespace DotPmp.Server;

public class ShareStationService
{
    private static readonly HttpClient HttpClient = new();
    private readonly ServerConfig _config;

    public ShareStationService(ServerConfig config)
    {
        _config = config;
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_config.ShareStationUrl) &&
        !string.IsNullOrWhiteSpace(_config.ShareStationToken);

    public record UploadResult(long ReplayId);

    public async Task<UploadResult> UploadReplayAsync(
        string path,
        long userId,
        bool show = true,
        string? chartName = "",
        string? username = "",
        string? illustration = "",
        string? chartLink = "",
        CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Share station is not configured");

        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri("/upload_direct"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ShareStationToken);

        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(path);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(path));
        form.Add(new StringContent(chartName ?? string.Empty), "chart_name");
        form.Add(new StringContent(username ?? string.Empty), "username");
        form.Add(new StringContent(illustration ?? string.Empty), "illustration");
        form.Add(new StringContent(chartLink ?? string.Empty), "chart_link");
        request.Content = form;

        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);
        using var json = JsonDocument.Parse(responseBody);
        if (json.RootElement.TryGetProperty("success", out var successElement) &&
            successElement.ValueKind == JsonValueKind.False)
        {
            var message = json.RootElement.TryGetProperty("message", out var messageElement)
                ? messageElement.ToString()
                : "unknown share station error";
            throw new InvalidOperationException($"Share station upload failed: {message}. Response: {responseBody}");
        }

        var replayId = ReadInt64(json.RootElement, "replay_id", "replayId", "score_id", "scoreId", "id");

        if (show)
            await SetVisibilityAsync(replayId, true, cancellationToken);

        return new UploadResult(replayId);
    }

    private async Task SetVisibilityAsync(long scoreId, bool visible, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, BuildUri(visible ? $"/show/{scoreId}" : $"/hide/{scoreId}"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _config.ShareStationToken);
        using var response = await HttpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private Uri BuildUri(string relativePath)
    {
        return new Uri($"{_config.ShareStationUrl!.TrimEnd('/')}{relativePath}");
    }

    private static long ReadInt64(JsonElement root, params string[] names)
    {
        foreach (var name in names)
        {
            if (root.TryGetProperty(name, out var value))
            {
                if (value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var number))
                    return number;
                if (value.ValueKind == JsonValueKind.String && long.TryParse(value.GetString(), out number))
                    return number;
            }
        }

        throw new InvalidOperationException($"Missing expected id field in share station response: {root}");
    }
}
