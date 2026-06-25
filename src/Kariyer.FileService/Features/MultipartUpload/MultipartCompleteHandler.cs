using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Amazon.S3.Model;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.MultipartUpload;

public record CompletedPartDto(int PartNumber, string ETag);
public record MultipartCompleteRequest(string StorageKey, string UploadId, List<CompletedPartDto> Parts);

public class MultipartCompleteHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<MultipartCompleteHandler> _logger;

    public MultipartCompleteHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<MultipartCompleteHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<StoredFile?> HandleAsync(
        MultipartCompleteRequest request, 
        string userId, 
        string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("MultipartCompleteHandler.Handle");
        activity?.SetTag("user.id", userId);
        activity?.SetTag("user.role", userRole);

        if (request == null || string.IsNullOrWhiteSpace(request.StorageKey) || string.IsNullOrWhiteSpace(request.UploadId))
        {
            _logger.LogWarning("Multipart complete blocked: Request parameters are empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid parameters.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(request.StorageKey, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Multipart complete failed: Malicious or invalid key format '{Key}'.", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid key format.");
            return null;
        }

        if (request.Parts == null || request.Parts.Count == 0)
        {
            _logger.LogWarning("Multipart complete blocked: Completed parts list is empty.");
            return null;
        }

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == request.StorageKey);
        if (fileRecord == null)
        {
            _logger.LogWarning("Multipart complete failed: Storage key '{StorageKey}' not found in DB.", request.StorageKey);
            return null;
        }

        // Verification: Only Pending files can be completed
        if (fileRecord.Status != "Pending")
        {
            _logger.LogWarning("Multipart complete failed: File '{StorageKey}' is not in Pending status (current status: {Status}).", 
                request.StorageKey, fileRecord.Status);
            return null;
        }

        // Verification: Part numbers must be unique
        if (request.Parts.Select(p => p.PartNumber).Distinct().Count() != request.Parts.Count)
        {
            _logger.LogWarning("Multipart complete failed: Duplicate part numbers found in completion request.");
            return null;
        }

        // Security check: Only owner or admin can complete the upload
        bool isOwner = fileRecord.UserId == userId;
        bool isAdmin = userRole == "admin" || userRole == "super_admin";
        if (!isOwner && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {UserId} attempted to complete upload for file owned by {OwnerId}.",
                userId, fileRecord.UserId);
            throw new UnauthorizedAccessException("You do not have permission to complete upload for this file.");
        }

        // Security check: Verify request UploadId matches stored UploadId
        if (fileRecord.UploadId != request.UploadId)
        {
            _logger.LogWarning("Multipart complete failed: Request UploadId '{ReqUploadId}' does not match stored UploadId '{DbUploadId}'.",
                request.UploadId, fileRecord.UploadId);
            return null;
        }

        try
        {
            // Map and explicitly sort by PartNumber ascending (Amazon S3 / R2 strict requirement)
            var s3PartETags = request.Parts
                .OrderBy(p => p.PartNumber)
                .Select(p => new PartETag(p.PartNumber, p.ETag))
                .ToList();

            // Complete the multipart upload on Cloudflare R2
            bool success = await _r2Storage.CompleteMultipartUploadAsync(request.StorageKey, request.UploadId, s3PartETags);
            if (!success)
            {
                _logger.LogWarning("Multipart complete failed on Cloudflare R2 for key '{StorageKey}'.", request.StorageKey);
                return null;
            }

            // Fetch the actual completed file size from R2
            long? actualSize = await _r2Storage.GetObjectSizeAsync(request.StorageKey);
            if (actualSize == null || actualSize.Value <= 0)
            {
                _logger.LogWarning("Multipart complete failed: Completed object size could not be retrieved from Cloudflare R2 for key '{StorageKey}'.", 
                    request.StorageKey);
                return null;
            }

            // Enforce size limits: minimum 5MB, maximum 500MB
            const long minMultipartLimit = 5L * 1024 * 1024;
            const long maxMultipartLimit = 500L * 1024 * 1024;
            if (actualSize.Value < minMultipartLimit || actualSize.Value > maxMultipartLimit)
            {
                _logger.LogWarning("Multipart complete failed: Completed size {ActualSize} bytes violates allowed multipart limits (5MB - 500MB). Deleting key '{StorageKey}'.", 
                    actualSize.Value, request.StorageKey);

                // Physical cleanup: delete the object from R2
                await _r2Storage.DeleteFileAsync(request.StorageKey);

                // DB cleanup: remove the metadata record
                _dbContext.StoredFiles.Remove(fileRecord);
                await _dbContext.SaveChangesAsync();

                return null;
            }

            // Update database record to Active, set exact physical size, and clear UploadId
            fileRecord.Status = "Active";
            fileRecord.FileSize = actualSize.Value;
            fileRecord.UploadId = null;
            await _dbContext.SaveChangesAsync();

            // Cache the activated metadata in Garnet cache
            string metaCacheKey = $"file:meta:{request.StorageKey}";
            await _cache.SetAsync(metaCacheKey, fileRecord, TimeSpan.FromHours(24));

            // Invalidate any stale URL caches
            string downloadUrlCacheKey = $"file:download:{request.StorageKey}";
            await _cache.InvalidateAsync(downloadUrlCacheKey);

            _logger.LogInformation("Successfully completed S3 Multipart Upload for key '{StorageKey}'. Cached metadata in Garnet.", 
                request.StorageKey);

            return fileRecord;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to complete multipart upload for key '{StorageKey}'", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
