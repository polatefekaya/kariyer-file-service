using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.DeleteFile;

public class DeleteFileHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<DeleteFileHandler> _logger;

    public DeleteFileHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<DeleteFileHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool?> HandleAsync(string key, string userId, string? userRole)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Deletion failed: Key parameter is empty.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(key, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Deletion failed: Malicious or invalid key format '{Key}'.", key);
            return null;
        }

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == key);
        if (fileRecord == null)
        {
            FileServiceDiagnostics.FileDeletedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "not_found"));
            return null; // Not found
        }

        // Security check: Only owner or admin can delete
        bool isOwner = fileRecord.UserId == userId;
        bool isAdmin = userRole == "admin" || userRole == "super_admin";
        if (!isOwner && !isAdmin)
        {
            throw new UnauthorizedAccessException("You do not have permission to delete this file.");
        }

        try
        {
            // 1. Delete/Abort from Cloudflare R2
            bool deleted;
            if (fileRecord.Status == "Pending" && !string.IsNullOrWhiteSpace(fileRecord.UploadId))
            {
                // Abort the pending multipart upload on R2
                deleted = await _r2Storage.AbortMultipartUploadAsync(key, fileRecord.UploadId);
            }
            else
            {
                // Standard delete
                deleted = await _r2Storage.DeleteFileAsync(key);
            }

            if (!deleted)
            {
                FileServiceDiagnostics.FileDeletedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "r2_failed"));
                return false; // Error executing delete/abort
            }

            // 2. Remove metadata from database
            _dbContext.StoredFiles.Remove(fileRecord);
            await _dbContext.SaveChangesAsync();

            // 3. Cache Invalidation (Evict Garnet cached values)
            await _cache.InvalidateAsync($"file:meta:{key}");
            await _cache.InvalidateAsync($"file:download:{key}");

            FileServiceDiagnostics.FileDeletedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error deleting file '{Key}'", key);
            FileServiceDiagnostics.FileDeletedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "exception"));
            throw;
        }
    }
}
