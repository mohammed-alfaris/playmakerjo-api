namespace SportsVenueApi.Helpers;

/// <summary>
/// Content-based image validation. Extension and MIME are attacker-controlled, so
/// upload paths verify the actual leading bytes match an allowed image type.
/// </summary>
public static class ImageBytes
{
    /// <summary>
    /// True if the leading bytes match JPEG, PNG, WEBP, or HEIC/HEIF.
    ///
    /// Each format is tested against the bytes it actually needs rather than behind a
    /// single blanket length gate: a PNG is identifiable from its 8-byte signature and
    /// a JPEG from 3, so demanding 12 up front rejected short-but-valid input for a
    /// reason that had nothing to do with the format. Only WEBP and HEIC genuinely
    /// need 12, because their discriminating bytes sit at offsets 8-11 and 4-7.
    /// </summary>
    public static bool HasAllowedSignature(ReadOnlySpan<byte> head)
    {
        // JPEG: FF D8 FF
        if (head.Length >= 3
            && head[0] == 0xFF && head[1] == 0xD8 && head[2] == 0xFF) return true;

        // PNG: the full 8-byte signature. The trailing CR/LF/EOF bytes are what make
        // this robust against a file that merely opens with the four ASCII characters.
        if (head.Length >= 8
            && head[0] == 0x89 && head[1] == 0x50 && head[2] == 0x4E && head[3] == 0x47
            && head[4] == 0x0D && head[5] == 0x0A && head[6] == 0x1A && head[7] == 0x0A) return true;

        // "RIFF" ???? "WEBP"
        if (head.Length >= 12
            && head[0] == 0x52 && head[1] == 0x49 && head[2] == 0x46 && head[3] == 0x46
            && head[8] == 0x57 && head[9] == 0x45 && head[10] == 0x42 && head[11] == 0x50) return true;

        // ISO-BMFF "ftyp" box — HEIC/HEIF. NOTE: the brand at offsets 8-11 is not
        // inspected, so an MP4 also satisfies this. Tightening it needs the full list
        // of image brands and is left alone here rather than guessed at.
        if (head.Length >= 12
            && head[4] == 0x66 && head[5] == 0x74 && head[6] == 0x79 && head[7] == 0x70) return true;

        return false;
    }
}
