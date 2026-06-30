using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.PresignedUpload;

public record PresignedUploadRequest(string FileName, string ContentType, long FileSize, bool IsPublic);
public record PresignedUploadResponse(string FileId, string StorageKey, string PresignedUrl);

public class PresignedUploadHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ILogger<PresignedUploadHandler> _logger;

    public PresignedUploadHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ILogger<PresignedUploadHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _logger = logger;
    }

    public async Task<PresignedUploadResponse?> HandleAsync(PresignedUploadRequest request, string userId)
    {
        // Start diagnostic activity tracing with rich semantic tags
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("PresignedUploadHandler.Handle");
        activity?.SetTag("user.id", userId);
        
        if (request == null)
        {
            _logger.LogWarning("Upload blocked: Null request received.");
            activity?.SetStatus(ActivityStatusCode.Error, "Null request.");
            return null;
        }

        activity?.SetTag("file.requested_name", request.FileName);
        activity?.SetTag("file.content_type", request.ContentType);
        activity?.SetTag("file.size", request.FileSize);
        activity?.SetTag("file.is_public", request.IsPublic);

        _logger.LogInformation("Initiating presigned upload request for user {UserId}. File: {FileName}, Type: {ContentType}, Size: {FileSize} bytes", 
            userId, request.FileName, request.ContentType, request.FileSize);

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            _logger.LogWarning("Upload blocked: Requested filename is empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Filename is empty.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ContentType))
        {
            _logger.LogWarning("Upload blocked: Requested Content-Type is empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "ContentType is empty.");
            return null;
        }

        // 1. Sanitize file name to prevent path traversal/injection
        string safeFileName = Path.GetFileName(request.FileName);
        // Remove null-bytes, URL-encoded nulls, or control characters explicitly
        safeFileName = safeFileName.Replace("\0", "").Replace("%00", "");
        safeFileName = Regex.Replace(safeFileName, @"[^a-zA-Z0-9\._-]", ""); // Only allow safe alphanumeric, dot, underscore, dash
        
        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            _logger.LogWarning("Upload blocked: Filename '{FileName}' was completely stripped or is invalid after sanitization.", request.FileName);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "invalid_filename"));
            activity?.SetStatus(ActivityStatusCode.Error, "Filename sanitization resulted in empty string.");
            return null;
        }

        if (safeFileName.Length > 255)
        {
            _logger.LogWarning("Upload blocked: Sanitized filename '{FileName}' length {Length} exceeds 255 characters limit.", 
                safeFileName, safeFileName.Length);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "filename_too_long"));
            activity?.SetStatus(ActivityStatusCode.Error, "Filename too long.");
            return null;
        }

        // 2. Validate MIME type
        string contentType = request.ContentType.ToLowerInvariant();
        if (!IsValidMimeType(contentType))
        {
            _logger.LogWarning("Upload blocked: Unsupported Content-Type '{ContentType}' for file '{FileName}'.", contentType, safeFileName);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "unsafe_mimetype"));
            activity?.SetStatus(ActivityStatusCode.Error, "Unsupported or unsafe media type.");
            return null;
        }

        // 3. Prevent MIME Spoofing: Validate file extension compatibility
        string extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrEmpty(extension) || !IsExtensionCompatible(extension, contentType))
        {
            _logger.LogWarning("Upload blocked: Extension/MIME mismatch. Extension '{Extension}' is not compatible with Content-Type '{ContentType}'.", 
                extension, contentType);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "extension_mismatch"));
            activity?.SetStatus(ActivityStatusCode.Error, "MIME type and file extension mismatch.");
            return null;
        }

        // 4. Enforce strict size boundaries (max 50MB, minimum 1 byte)
        const long maxLimit = 50 * 1024 * 1024;
        if (request.FileSize <= 0 || request.FileSize > maxLimit)
        {
            _logger.LogWarning("Upload blocked: File size {FileSize} bytes is outside allowed limits (1B - 50MB) for file '{FileName}'.", 
                request.FileSize, safeFileName);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "invalid_size"));
            activity?.SetStatus(ActivityStatusCode.Error, "File size out of range.");
            return null;
        }

        try
        {
            string ulid = System.Ulid.NewUlid().ToString();
            string folder = request.IsPublic ? "public" : "private";
            
            // Map category subfolders
            string category = contentType.StartsWith("image/") ? "images" 
                             : contentType.StartsWith("video/") ? "videos" 
                             : "documents";

            string storageKey = $"{folder}/{category}/{ulid}-{safeFileName}";
            activity?.SetTag("file.storage_key", storageKey);
            activity?.SetTag("file.id", ulid);

            // 5. Generate presigned PUT URL for direct R2 upload
            string presignedUrl = _r2Storage.GeneratePresignedUploadUrl(
                storageKey, 
                contentType, 
                request.FileSize, 
                TimeSpan.FromMinutes(15));

            // 6. Create database metadata entry as Pending
            StoredFile fileRecord = new()
            {
                Id = ulid,
                StorageKey = storageKey,
                OriginalFileName = safeFileName,
                ContentType = contentType,
                FileSize = request.FileSize,
                UserId = userId,
                IsPublic = request.IsPublic,
                Status = "Pending",
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.StoredFiles.Add(fileRecord);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Successfully generated presigned upload URL. FileId: {FileId}, StorageKey: {StorageKey}", ulid, storageKey);

            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "success"), 
                new KeyValuePair<string, object?>("is_public", request.IsPublic.ToString()));

            return new PresignedUploadResponse(ulid, storageKey, presignedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Exception occurred generating presigned upload URL for user {UserId}", userId);
            FileServiceDiagnostics.PresignedUploadRequestedCounter.Add(1, 
                new KeyValuePair<string, object?>("outcome", "failure"), 
                new KeyValuePair<string, object?>("reason", "exception"));
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static bool IsValidMimeType(string mimeType)
    {
        var allowedTypes = new HashSet<string>
        {
            "image/jpeg", "image/png", "image/gif", "image/webp", "image/svg+xml",
            "video/mp4", "video/webm", "video/ogg", "video/quicktime",
            "application/pdf",
            "application/msword", "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            "application/vnd.ms-excel", "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
        };
        return allowedTypes.Contains(mimeType);
    }

    private static bool IsExtensionCompatible(string extension, string contentType)
    {
        string ext = extension.TrimStart('.').ToLowerInvariant();
        return contentType switch
        {
            "image/jpeg" => ext is "jpg" or "jpeg",
            "image/png" => ext == "png",
            "image/gif" => ext == "gif",
            "image/webp" => ext == "webp",
            "image/svg+xml" => ext == "svg",
            "video/mp4" => ext == "mp4",
            "video/webm" => ext == "webm",
            "video/ogg" => ext == "ogg",
            "video/quicktime" => ext == "mov",
            "application/pdf" => ext == "pdf",
            "application/msword" => ext == "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ext == "docx",
            "application/vnd.ms-excel" => ext == "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ext == "xlsx",
            _ => false
        };
    }
}
