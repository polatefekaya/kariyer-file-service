using System;
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

namespace Kariyer.FileService.Features.MultipartUpload;

public record MultipartAbortRequest(string StorageKey, string UploadId);

public class MultipartAbortHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<MultipartAbortHandler> _logger;

    public MultipartAbortHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<MultipartAbortHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<bool?> HandleAsync(
        MultipartAbortRequest request, 
        string userId, 
        string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("MultipartAbortHandler.Handle");
        activity?.SetTag("user.id", userId);
        activity?.SetTag("user.role", userRole);

        if (request == null || string.IsNullOrWhiteSpace(request.StorageKey) || string.IsNullOrWhiteSpace(request.UploadId))
        {
            _logger.LogWarning("Multipart abort blocked: Request parameters are empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid parameters.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(request.StorageKey, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Multipart abort failed: Malicious or invalid key format '{Key}'.", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid key format.");
            return null;
        }

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == request.StorageKey);
        if (fileRecord == null)
        {
            _logger.LogWarning("Multipart abort failed: Storage key '{StorageKey}' not found in DB.", request.StorageKey);
            return null;
        }

        // Verification: Only Pending files can be aborted
        if (fileRecord.Status != "Pending")
        {
            _logger.LogWarning("Multipart abort failed: File '{StorageKey}' is not in Pending status.", request.StorageKey);
            return null;
        }

        // Security check: Only owner or admin can abort upload
        bool isOwner = fileRecord.UserId == userId;
        bool isAdmin = userRole == "admin" || userRole == "super_admin";
        if (!isOwner && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {UserId} attempted to abort upload for file owned by {OwnerId}.",
                userId, fileRecord.UserId);
            throw new UnauthorizedAccessException("You do not have permission to abort upload for this file.");
        }

        // Security check: Verify request UploadId matches stored UploadId
        if (fileRecord.UploadId != request.UploadId)
        {
            _logger.LogWarning("Multipart abort failed: Request UploadId '{ReqUploadId}' does not match stored UploadId '{DbUploadId}'.",
                request.UploadId, fileRecord.UploadId);
            return null;
        }

        try
        {
            // Abort the upload session on Cloudflare R2
            bool success = await _r2Storage.AbortMultipartUploadAsync(request.StorageKey, request.UploadId);
            if (!success)
            {
                _logger.LogWarning("Multipart abort failed on Cloudflare R2 for key '{StorageKey}'. Proceeding to remove record anyway.", 
                    request.StorageKey);
            }

            // Remove database metadata record
            _dbContext.StoredFiles.Remove(fileRecord);
            await _dbContext.SaveChangesAsync();

            // Evict caches just in case
            await _cache.InvalidateAsync($"file:meta:{request.StorageKey}");
            await _cache.InvalidateAsync($"file:download:{request.StorageKey}");

            _logger.LogInformation("Successfully aborted S3 Multipart Upload for key '{StorageKey}' and deleted database record.", 
                request.StorageKey);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to abort multipart upload for key '{StorageKey}'", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
