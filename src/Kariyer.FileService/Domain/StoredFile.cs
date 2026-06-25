using System;

namespace Kariyer.FileService.Domain;

public class StoredFile
{
    public string Id { get; set; } = string.Empty; // ULID
    public string StorageKey { get; set; } = string.Empty; // e.g., public/images/ulid-name.png
    public string OriginalFileName { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public string? UserId { get; set; } // Owner ID (from Supabase JWT claim)
    public bool IsPublic { get; set; }
    public string Status { get; set; } = "Pending"; // "Pending" or "Active"
    public string? UploadId { get; set; } // S3/R2 Multipart Upload ID (if applicable)
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
