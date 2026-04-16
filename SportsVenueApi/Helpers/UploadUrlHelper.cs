namespace SportsVenueApi.Helpers;

/// <summary>
/// Rewrites the host portion of an absolute upload URL to a configured base URL.
/// Used to expose `/uploads/...` files hosted on the API machine to clients that
/// can't reach `localhost` (e.g. the mobile app on a different device on the LAN).
/// Non-HTTP URLs (empty, relative paths, data: URIs) are returned unchanged.
/// </summary>
public static class UploadUrlHelper
{
    public static string? Normalize(string? url, string baseUrl)
    {
        if (string.IsNullOrEmpty(url) || !url.StartsWith("http")) return url;
        var idx = url.IndexOf("/uploads/");
        if (idx < 0) return url;
        return baseUrl + url[idx..];
    }
}
