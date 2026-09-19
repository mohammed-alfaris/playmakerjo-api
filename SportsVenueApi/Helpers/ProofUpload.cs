using System.Text.RegularExpressions;

namespace SportsVenueApi.Helpers;

/// <summary>
/// Recognises a payment proof the app has already uploaded through POST /uploads.
///
/// The app does not send proof bytes to upload-proof: it uploads the screenshot first and
/// sends back the URL it was given. Validating that URL as base64 — which upload-proof did
/// for a while — rejected every proof the app ever sent, so no CliQ payment could complete.
///
/// A URL is accepted only when it names a file that is actually in our proofs folder. The one
/// writer of that folder is UploadsController, which has already checked size, extension and
/// the image bytes, so a file there is a proof we validated. The host is deliberately not
/// compared: stored URLs are re-based on read by <see cref="UploadUrlHelper.Normalize"/>, and a
/// host check would only make proofs fail when the API sits behind a different hostname than
/// the one that handed the URL out.
/// </summary>
public static partial class ProofUpload
{
    /// <summary>
    /// Exactly the shape UploadsController writes: a GUID file name with an image extension,
    /// directly in /uploads/proofs/. No further slashes and no dots before the extension, so
    /// nothing like "../" can reach a file outside the folder.
    /// </summary>
    [GeneratedRegex(@"^/uploads/proofs/[A-Za-z0-9-]{1,64}\.(jpg|jpeg|png|webp|heic|heif)$", RegexOptions.IgnoreCase)]
    private static partial Regex StoredProofPath();

    /// <summary>
    /// True when the value points at an upload — as opposed to inline base64 image data.
    ///
    /// UploadsController returns an absolute URL when Uploads:BaseUrl is set, and a root-relative
    /// "/uploads/..." path when it is not (appsettings ships it empty), so both are references.
    /// Note "starts with '/'" alone would be wrong: base64 JPEG data begins "/9j/". No real
    /// image's base64 begins "/uploads/", so that prefix is unambiguous.
    /// </summary>
    public static bool IsUploadReference(string value) =>
        value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || value.StartsWith("/uploads/", StringComparison.Ordinal);

    /// <param name="reference">What the client sent: an absolute URL or a root-relative path.</param>
    /// <param name="contentRoot">The API's content root; uploads live under wwwroot beneath it.</param>
    public static bool IsStoredProof(string reference, string contentRoot)
    {
        string path;
        if (reference.StartsWith('/'))
        {
            if (reference.Contains('?') || reference.Contains('#')) return false;
            path = reference;
        }
        else
        {
            if (!Uri.TryCreate(reference, UriKind.Absolute, out var uri)) return false;
            if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;
            if (uri.Query.Length > 0 || uri.Fragment.Length > 0) return false;
            path = uri.AbsolutePath;
        }

        if (!StoredProofPath().IsMatch(path)) return false;

        var file = Path.Combine(contentRoot, "wwwroot", "uploads", "proofs", Path.GetFileName(path));
        return File.Exists(file);
    }
}
