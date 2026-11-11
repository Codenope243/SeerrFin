using System.Net.Http.Headers;
using System.Text;
using Jellyfin.Plugin.SeerrFin.Configuration;
using Jellyfin.Plugin.SeerrFin.Helpers;
using Jellyfin.Plugin.SeerrFin.Model;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.SeerrFin.Services;

public class JellyseerrProxyService
{
    private readonly IHttpClientFactory _clientFactory;
    private readonly ILogger<JellyseerrProxyService> _logger;

    public JellyseerrProxyService(IHttpClientFactory clientFactory, ILogger<JellyseerrProxyService> logger)
    {
        _clientFactory = clientFactory;
        _logger = logger;
    }

    public async Task<(int StatusCode, string Body, string ContentType)> ProxyAsync(
        string username,
        HttpMethod method,
        string relativePath,
        string? requestBody,
        CancellationToken cancellationToken
    )
    {
        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl) || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return (400, "{\"error\":true,\"message\":\"Seerr is not configured in SeerrFin.\"}", "application/json");
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            return (401, "{\"error\":true,\"message\":\"User not found.\"}", "application/json");
        }

        using HttpClient client = _clientFactory.CreateClient(SeerrCsrfHandler.ClientName);
        client.BaseAddress = new Uri(config.JellyseerrUrl!);
        client.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);

        int? jellyseerrUserId = await ResolveJellyseerrUserIdAsync(client, username, cancellationToken).ConfigureAwait(false);
        if (jellyseerrUserId == null)
        {
            return (404, "{\"error\":true,\"message\":\"Seerr user not linked.\"}", "application/json");
        }

        client.DefaultRequestHeaders.Add("X-Api-User", jellyseerrUserId.ToString());

        // Accept both full seerr paths and short paths from client proxy
        string apiPath = relativePath.StartsWith("/api/v1/", StringComparison.OrdinalIgnoreCase)
            ? relativePath
            : $"/api/v1/{relativePath.TrimStart('/')}";

        using HttpRequestMessage request = new(method, apiPath);

        if (requestBody != null && method != HttpMethod.Get && method != HttpMethod.Head)
        {
            request.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        }

        try
        {
            using HttpResponseMessage response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            string contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";

            return ((int)response.StatusCode, body, contentType);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SeerrFin • Seerr proxy failed for {Path}", apiPath);

            return (502, "{\"error\":true,\"message\":\"Failed to reach Seerr.\"}", "application/json");
        }
    }

    public async Task<Dictionary<string, JToken?>> GetRatingsAsync(
        string username,
        IEnumerable<RatingBatchRequestItemDto> items,
        CancellationToken cancellationToken
    )
    {
        List<RatingBatchRequestItemDto> uniqueItems = items
            .Where(item =>
                item.TmdbId > 0
                && (
                    string.Equals(item.MediaType, "movie", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(item.MediaType, "tv", StringComparison.OrdinalIgnoreCase)
                )
            )
            .GroupBy(item => GetRatingKey(item.MediaType, item.TmdbId))
            .Select(group => group.First())
            .ToList();
        Dictionary<string, JToken?> ratings = uniqueItems.ToDictionary(
            item => GetRatingKey(item.MediaType, item.TmdbId),
            _ => (JToken?)null
        );
        if (uniqueItems.Count == 0)
        {
            return ratings;
        }

        PluginConfiguration config = SeerrFinPlugin.Instance.Configuration;
        if (string.IsNullOrWhiteSpace(config.JellyseerrUrl) || string.IsNullOrWhiteSpace(config.JellyseerrApiKey))
        {
            return ratings;
        }

        using HttpClient client = _clientFactory.CreateClient(SeerrCsrfHandler.ClientName);
        client.BaseAddress = new Uri(config.JellyseerrUrl);
        client.DefaultRequestHeaders.Add("X-Api-Key", config.JellyseerrApiKey);

        int? jellyseerrUserId = await ResolveJellyseerrUserIdAsync(client, username, cancellationToken).ConfigureAwait(false);
        if (jellyseerrUserId == null)
        {
            return ratings;
        }

        client.DefaultRequestHeaders.Add("X-Api-User", jellyseerrUserId.ToString());
        (string Key, JToken? Rating)[] resolved = await Task
            .WhenAll(uniqueItems.Select(item => FetchRatingAsync(client, item, cancellationToken)))
            .ConfigureAwait(false);

        foreach ((string key, JToken? rating) in resolved)
        {
            ratings[key] = rating;
        }

        return ratings;
    }

    private async Task<(string Key, JToken? Rating)> FetchRatingAsync(
        HttpClient client,
        RatingBatchRequestItemDto item,
        CancellationToken cancellationToken
    )
    {
        string mediaType = item.MediaType.ToLowerInvariant();
        string key = GetRatingKey(mediaType, item.TmdbId);
        string path = mediaType == "tv"
            ? $"/api/v1/tv/{item.TmdbId}/ratings"
            : $"/api/v1/movie/{item.TmdbId}/ratingscombined";

        try
        {
            using HttpResponseMessage response = await client.GetAsync(path, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                if ((int)response.StatusCode != 404)
                {
                    _logger.LogWarning(
                        "SeerrFin • Ratings request for {MediaType}/{TmdbId} returned {StatusCode}",
                        mediaType,
                        item.TmdbId,
                        (int)response.StatusCode
                    );
                }

                return (key, null);
            }

            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(body) ? (key, null) : (key, JObject.Parse(body));
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "SeerrFin • Ratings request failed for {MediaType}/{TmdbId}", mediaType, item.TmdbId);
            return (key, null);
        }
        catch (JsonReaderException ex)
        {
            _logger.LogWarning(ex, "SeerrFin • Ratings response was invalid for {MediaType}/{TmdbId}", mediaType, item.TmdbId);
            return (key, null);
        }
    }

    private static string GetRatingKey(string mediaType, int tmdbId) => $"{mediaType.ToLowerInvariant()}:{tmdbId}";

    private static async Task<int?> ResolveJellyseerrUserIdAsync(HttpClient client, string username, CancellationToken cancellationToken)
    {
        using HttpResponseMessage usersResponse = await client
            .GetAsync($"/api/v1/user?q={Uri.EscapeDataString(username)}", cancellationToken)
            .ConfigureAwait(false);
        string userResponseRaw = await usersResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!usersResponse.IsSuccessStatusCode)
        {
            return null;
        }

        return Newtonsoft.Json.Linq.JObject.Parse(userResponseRaw).Value<Newtonsoft.Json.Linq.JArray>("results")
            ?.OfType<Newtonsoft.Json.Linq.JObject>()
            .FirstOrDefault(x => string.Equals(x.Value<string>("jellyfinUsername"), username, StringComparison.OrdinalIgnoreCase))
            ?.Value<int>("id");
    }
}
