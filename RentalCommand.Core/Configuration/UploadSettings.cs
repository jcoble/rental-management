namespace RentalCommand.Core.Configuration;

/// <summary>
/// File-upload settings (storage location and validation limits). Bound from the
/// "Upload" configuration section.
/// </summary>
public class UploadSettings
{
    public const string SectionName = "Upload";

    /// <summary>Base directory/prefix where uploaded files are stored.</summary>
    public string BasePath { get; set; } = string.Empty;

    /// <summary>Maximum accepted file size in bytes.</summary>
    public int MaxFileSizeBytes { get; set; }

    /// <summary>Allowed MIME types for uploads.</summary>
    public string[] AllowedMimeTypes { get; set; } = Array.Empty<string>();
}
