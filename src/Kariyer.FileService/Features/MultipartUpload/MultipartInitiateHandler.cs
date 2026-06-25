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

namespace Kariyer.FileService.Features.MultipartUpload;

public record MultipartInitiateRequest(string FileName, string ContentType, long FileSize, bool IsPublic);
public record MultipartInitiateResponse(string FileId, string StorageKey, string UploadId);

public class MultipartInitiateHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ILogger<MultipartInitiateHandler> _logger;

    public MultipartInitiateHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ILogger<MultipartInitiateHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _logger = logger;
    }

    public async Task<MultipartInitiateResponse?> HandleAsync(MultipartInitiateRequest request, string userId)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("MultipartInitiateHandler.Handle");
        activity?.SetTag("user.id", userId);

        if (request == null)
        {
            _logger.LogWarning("Multipart initiate blocked: Null request received.");
            activity?.SetStatus(ActivityStatusCode.Error, "Null request.");
            return null;
        }

        activity?.SetTag("file.requested_name", request.FileName);
        activity?.SetTag("file.content_type", request.ContentType);
        activity?.SetTag("file.size", request.FileSize);
        activity?.SetTag("file.is_public", request.IsPublic);

        _logger.LogInformation("Initiating multipart upload for user {UserId}. File: {FileName}, Type: {ContentType}, Size: {FileSize} bytes", 
            userId, request.FileName, request.ContentType, request.FileSize);

        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            _logger.LogWarning("Multipart initiate blocked: Requested filename is empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Filename is empty.");
            return null;
        }

        if (string.IsNullOrWhiteSpace(request.ContentType))
        {
            _logger.LogWarning("Multipart initiate blocked: Requested Content-Type is empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "ContentType is empty.");
            return null;
        }

        // 1. Sanitize filename
        string safeFileName = Path.GetFileName(request.FileName);
        safeFileName = safeFileName.Replace("\0", "").Replace("%00", "");
        safeFileName = Regex.Replace(safeFileName, @"[^a-zA-Z0-9\._-]", "");

        if (string.IsNullOrWhiteSpace(safeFileName))
        {
            _logger.LogWarning("Multipart initiate blocked: Filename '{FileName}' is invalid after sanitization.", request.FileName);
            activity?.SetStatus(ActivityStatusCode.Error, "Sanitization resulted in empty filename.");
            return null;
        }

        if (safeFileName.Length > 255)
        {
            _logger.LogWarning("Multipart initiate blocked: Sanitized filename exceeds 255 characters limit.");
            activity?.SetStatus(ActivityStatusCode.Error, "Filename too long.");
            return null;
        }

        // 2. Validate MIME type
        string contentType = request.ContentType.ToLowerInvariant();
        if (!IsValidMimeType(contentType))
        {
            _logger.LogWarning("Multipart initiate blocked: Unsupported Content-Type '{ContentType}'.", contentType);
            activity?.SetStatus(ActivityStatusCode.Error, "Unsupported MIME type.");
            return null;
        }

        // 3. Prevent MIME Spoofing
        string extension = Path.GetExtension(safeFileName);
        if (string.IsNullOrEmpty(extension) || !IsExtensionCompatible(extension, contentType))
        {
            _logger.LogWarning("Multipart initiate blocked: Extension '{Extension}' is mismatch with MIME '{ContentType}'.", extension, contentType);
            activity?.SetStatus(ActivityStatusCode.Error, "MIME and extension mismatch.");
            return null;
        }

        // 4. Enforce size limits (Multipart requires minimum 5MB for S3 chunk constraints, max 500MB)
        const long minMultipartLimit = 5L * 1024 * 1024;
        const long maxMultipartLimit = 500L * 1024 * 1024;
        if (request.FileSize < minMultipartLimit || request.FileSize > maxMultipartLimit)
        {
            _logger.LogWarning("Multipart initiate blocked: Size {FileSize} bytes is outside allowed multipart limits (5MB - 500MB).", request.FileSize);
            activity?.SetStatus(ActivityStatusCode.Error, "File size out of range.");
            return null;
        }

        try
        {
            string ulid = System.Ulid.NewUlid().ToString();
            string folder = request.IsPublic ? "public" : "private";
            string category = contentType.StartsWith("image/") ? "images" 
                             : contentType.StartsWith("video/") ? "videos" 
                             : "documents";

            string storageKey = $"{folder}/{category}/{ulid}-{safeFileName}";
            activity?.SetTag("file.storage_key", storageKey);
            activity?.SetTag("file.id", ulid);

            // Initiate multipart upload in S3/R2 to obtain UploadId
            string uploadId = await _r2Storage.InitiateMultipartUploadAsync(storageKey, contentType);
            activity?.SetTag("file.upload_id", uploadId);

            // Create db metadata record in Pending status
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
                UploadId = uploadId,
                CreatedAt = DateTime.UtcNow
            };

            _dbContext.StoredFiles.Add(fileRecord);
            await _dbContext.SaveChangesAsync();

            _logger.LogInformation("Successfully initiated S3 Multipart Upload. FileId: {FileId}, UploadId: {UploadId}", ulid, uploadId);
            return new MultipartInitiateResponse(ulid, storageKey, uploadId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate multipart upload for user {UserId}", userId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }

    private static bool IsValidMimeType(string mimeType)
    {
        var allowedTypes = new HashSet<string>
        {
            "image/jpeg", "image/png", "image/gif", "image/webp", "image/svg+xml",
            "video/mp4", "video/webm", "video/ogg",
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
            "application/pdf" => ext == "pdf",
            "application/msword" => ext == "doc",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document" => ext == "docx",
            "application/vnd.ms-excel" => ext == "xls",
            "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet" => ext == "xlsx",
            _ => false
        };
    }
}
