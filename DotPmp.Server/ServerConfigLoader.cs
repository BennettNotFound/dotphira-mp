namespace DotPmp.Server;

public static class ServerConfigLoader
{
    public static void ApplyYamlFile(ServerConfig config, string path)
    {
        if (!File.Exists(path))
            return;

        string? section = null;
        foreach (var rawLine in File.ReadAllLines(path))
        {
            var line = rawLine.TrimEnd();
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                continue;

            if (!char.IsWhiteSpace(rawLine, 0) && line.EndsWith(':'))
            {
                section = line[..^1].Trim();
                continue;
            }

            var trimmed = line.Trim();
            var split = trimmed.IndexOf(':');
            if (split <= 0)
                continue;

            var key = trimmed[..split].Trim();
            var value = Unquote(trimmed[(split + 1)..].Trim());

            if (string.Equals(section, "share_station", StringComparison.OrdinalIgnoreCase))
            {
                ApplyShareStation(config, key, value);
                continue;
            }

            ApplyRoot(config, key, value);
        }
    }

    private static void ApplyRoot(ServerConfig config, string key, string value)
    {
        switch (key)
        {
            case "http_service":
                if (bool.TryParse(value, out var httpService)) config.HttpService = httpService;
                break;
            case "http_port":
                if (int.TryParse(value, out var httpPort)) config.HttpPort = httpPort;
                break;
            case "game_port":
                if (int.TryParse(value, out var gamePort)) config.GamePort = gamePort;
                break;
            case "server_name":
                config.ServerName = value;
                break;
            case "welcome_message":
                config.WelcomeMessage = value;
                break;
            case "admin_token":
                config.AdminToken = value;
                break;
            case "view_token":
                config.ViewToken = value;
                break;
            case "admin_data_path":
                config.AdminDataPath = value;
                break;
            case "game_session_idle_timeout_seconds":
                if (int.TryParse(value, out var idleTimeout)) config.GameSessionIdleTimeoutSeconds = idleTimeout;
                break;
            case "authorization_cache_minutes":
                if (int.TryParse(value, out var cacheMinutes)) config.AuthorizationCacheMinutes = cacheMinutes;
                break;
        }
    }

    private static void ApplyShareStation(ServerConfig config, string key, string value)
    {
        switch (key)
        {
            case "url":
                config.ShareStationUrl = value;
                break;
            case "token":
                config.ShareStationToken = value;
                break;
        }
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }
}
