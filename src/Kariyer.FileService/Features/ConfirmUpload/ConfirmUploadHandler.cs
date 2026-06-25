using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.ConfirmUpload;

public record ConfirmUploadRequest(string StorageKey);

public class ConfirmUploadHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<ConfirmUploadHandler> _logger;

    public ConfirmUploadHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<ConfirmUploadHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool?> HandleAsync(ConfirmUploadRequest request)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("ConfirmUploadHandler.Handle");
        
        if (request == null || string.IsNullOrWhiteSpace(request.StorageKey))
        {
            _logger.LogWarning("Confirmation failed: Request or StorageKey is empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Empty request or StorageKey.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(request.StorageKey, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Confirmation failed: Malicious or invalid StorageKey format '{StorageKey}'.", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid StorageKey format.");
            return null;
        }

        activity?.SetTag("file.storage_key", request.StorageKey);

        _logger.LogInformation("Confirming upload for storage key '{StorageKey}'", request.StorageKey);

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == request.StorageKey);
        if (fileRecord == null)
        {
            _logger.LogWarning("Confirmation failed: Storage key '{StorageKey}' does not exist in database.", request.StorageKey);
            FileServiceDiagnostics.FileConfirmedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "not_found"));
            activity?.SetStatus(ActivityStatusCode.Error, "Database metadata not found.");
            return null; // Not found
        }

        // Short-circuit: already Active
        if (fileRecord.Status != "Pending")
        {
            _logger.LogInformation("Confirmation skipped: File '{StorageKey}' is already in {Status} status.", request.StorageKey, fileRecord.Status);
            return true;
        }

        // Validate physical presence and get actual size on Cloudflare R2
        long? actualSize = await _r2Storage.GetObjectSizeAsync(request.StorageKey);
        if (actualSize == null || actualSize.Value <= 0)
        {
            _logger.LogWarning("Confirmation failed: Key '{StorageKey}' is not present or empty in Cloudflare R2 bucket.", request.StorageKey);
            FileServiceDiagnostics.FileConfirmedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "r2_missing"));
            activity?.SetStatus(ActivityStatusCode.Error, "R2 binary payload is missing.");
            return false; // BadRequest
        }

        // Enforce maximum size limit for standard uploads (50MB)
        const long maxStandardLimit = 50L * 1024 * 1024;
        if (actualSize.Value > maxStandardLimit)
        {
            _logger.LogWarning("Confirmation failed: Physical size {ActualSize} bytes exceeds maximum standard upload limit (50MB). Deleting key '{StorageKey}'.", 
                actualSize.Value, request.StorageKey);
            
            // Delete the physical payload from R2 to prevent storage leakage
            await _r2Storage.DeleteFileAsync(request.StorageKey);
            
            // Delete metadata from database
            _dbContext.StoredFiles.Remove(fileRecord);
            await _dbContext.SaveChangesAsync();

            activity?.SetStatus(ActivityStatusCode.Error, "Physical size exceeds limit.");
            return null;
        }

        fileRecord.Status = "Active";
        fileRecord.FileSize = actualSize.Value; // Update file size with exact physical size
        await _dbContext.SaveChangesAsync();

        // Cache the metadata in Garnet
        string cacheKey = $"file:meta:{request.StorageKey}";
        await _cache.SetAsync(cacheKey, fileRecord, TimeSpan.FromHours(24));

        _logger.LogInformation("Confirmation successful: File '{StorageKey}' status updated to Active. Cached in Garnet.", request.StorageKey);

        FileServiceDiagnostics.FileConfirmedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        return true; // Success
    }
}
