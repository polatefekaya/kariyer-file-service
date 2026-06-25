using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.PresignedUpload;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.OverwriteFileContent;

public class OverwriteFileContentHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<OverwriteFileContentHandler> _logger;

    public OverwriteFileContentHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<OverwriteFileContentHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PresignedUploadResponse?> HandleAsync(string id, string currentUserId, string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("OverwriteFileContentHandler.Handle");
        activity?.SetTag("user.id", currentUserId);
        activity?.SetTag("user.role", userRole);
        activity?.SetTag("file.id", id);

        _logger.LogInformation("Overwriting file content for {FileId} requested by user {UserId} (role: {Role}).",
            id, currentUserId, userRole);

        if (string.IsNullOrWhiteSpace(id) || id.Length != 26 || !Regex.IsMatch(id, "^[a-zA-Z0-9]{26}$"))
        {
            _logger.LogWarning("Overwrite failed: Invalid or malformed ULID format for FileId '{FileId}'.", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid ULID format.");
            return null;
        }

        bool isAdmin = userRole == "admin" || userRole == "super_admin";

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == id);
        if (fileRecord == null)
        {
            _logger.LogWarning("File not found for overwrite: FileId {FileId}.", id);
            activity?.SetStatus(ActivityStatusCode.Error, "File not found.");
            return null; // Not found
        }

        // Security check: Only owner or admin can overwrite file content
        if (fileRecord.UserId != currentUserId && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {CurrentUserId} attempted to overwrite file {FileId} owned by {OwnerId}.",
                currentUserId, id, fileRecord.UserId);
            activity?.SetStatus(ActivityStatusCode.Error, "Unauthorized overwrite attempt.");
            throw new UnauthorizedAccessException("You do not have permission to overwrite this file.");
        }

        try
        {
            // Abort any existing pending multipart upload session to release R2 resources
            if (!string.IsNullOrWhiteSpace(fileRecord.UploadId))
            {
                _logger.LogInformation("Overwriting pending multipart upload for file '{FileId}'. Aborting R2 UploadId '{UploadId}'.", 
                    id, fileRecord.UploadId);
                
                await _r2Storage.AbortMultipartUploadAsync(fileRecord.StorageKey, fileRecord.UploadId);
                fileRecord.UploadId = null;
            }

            // Generate upload URL mapping back to the same StorageKey (overwriting it in R2)
            string presignedUrl = _r2Storage.GeneratePresignedUploadUrl(
                fileRecord.StorageKey, 
                fileRecord.ContentType, 
                fileRecord.FileSize, 
                TimeSpan.FromMinutes(15));

            // Set status back to Pending until confirm-upload is executed again
            fileRecord.Status = "Pending";
            await _dbContext.SaveChangesAsync();

            // Invalidate cached metadata/download URL
            await _cache.InvalidateAsync($"file:meta:{fileRecord.StorageKey}");
            await _cache.InvalidateAsync($"file:download:{fileRecord.StorageKey}");

            _logger.LogInformation("Successfully initiated overwrite. Status set to Pending. Evicted cache for storage key '{StorageKey}'",
                fileRecord.StorageKey);

            return new PresignedUploadResponse(fileRecord.Id, fileRecord.StorageKey, presignedUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initiate overwrite for file '{FileId}'", id);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}

