using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.MultipartUpload;

public record MultipartPresignPartsRequest(string StorageKey, string UploadId, List<int> PartNumbers);
public record PartUrlResponse(int PartNumber, string PresignedUrl);
public record MultipartPresignPartsResponse(List<PartUrlResponse> Parts);

public class MultipartPresignPartsHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ILogger<MultipartPresignPartsHandler> _logger;

    public MultipartPresignPartsHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ILogger<MultipartPresignPartsHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _logger = logger;
    }

    public async Task<MultipartPresignPartsResponse?> HandleAsync(
        MultipartPresignPartsRequest request, 
        string userId, 
        string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("MultipartPresignPartsHandler.Handle");
        activity?.SetTag("user.id", userId);
        activity?.SetTag("user.role", userRole);

        if (request == null || string.IsNullOrWhiteSpace(request.StorageKey) || string.IsNullOrWhiteSpace(request.UploadId))
        {
            _logger.LogWarning("Multipart presign parts blocked: Request parameters are empty.");
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid parameters.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(request.StorageKey, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Multipart presign parts failed: Malicious or invalid key format '{Key}'.", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid key format.");
            return null;
        }

        if (request.PartNumbers == null || request.PartNumbers.Count == 0)
        {
            _logger.LogWarning("Multipart presign parts blocked: PartNumbers list is empty.");
            return null;
        }

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == request.StorageKey);
        if (fileRecord == null)
        {
            _logger.LogWarning("Multipart presign parts failed: Storage key '{StorageKey}' not found in DB.", request.StorageKey);
            return null;
        }

        // State protection: only Pending records can have part URLs generated
        if (fileRecord.Status != "Pending")
        {
            _logger.LogWarning("Multipart presign parts failed: File '{StorageKey}' is not in Pending status (current status: {Status}).", 
                request.StorageKey, fileRecord.Status);
            return null;
        }

        // Security check: Only owner or admin can generate presigned part URLs
        bool isOwner = fileRecord.UserId == userId;
        bool isAdmin = userRole == "admin" || userRole == "super_admin";
        if (!isOwner && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {UserId} attempted to generate presigned part URLs for file owned by {OwnerId}.",
                userId, fileRecord.UserId);
            throw new UnauthorizedAccessException("You do not have permission to upload parts to this file.");
        }

        // Security check: Verify request UploadId matches stored UploadId
        if (fileRecord.UploadId != request.UploadId)
        {
            _logger.LogWarning("Multipart presign parts failed: Request UploadId '{ReqUploadId}' does not match stored UploadId '{DbUploadId}'.",
                request.UploadId, fileRecord.UploadId);
            return null;
        }

        try
        {
            var partsResponse = new List<PartUrlResponse>();
            
            // Deduplicate requested parts
            var distinctPartNumbers = request.PartNumbers.Distinct().OrderBy(p => p).ToList();

            // Calculate mathematical maximum number of parts based on S3 minimum part size constraint (5MB)
            int maxPossibleParts = (int)Math.Ceiling((double)fileRecord.FileSize / (5L * 1024 * 1024));

            foreach (int partNumber in distinctPartNumbers)
            {
                if (partNumber < 1 || partNumber > 10000) // S3 supports maximum 10,000 parts
                {
                    _logger.LogWarning("Multipart presign parts failed: PartNumber {PartNumber} is out of S3 limits.", partNumber);
                    return null;
                }

                if (partNumber > maxPossibleParts)
                {
                    _logger.LogWarning("Multipart presign parts failed: PartNumber {PartNumber} exceeds maximum possible parts ({MaxParts}) for file size {FileSize} bytes.", 
                        partNumber, maxPossibleParts, fileRecord.FileSize);
                    return null;
                }

                string presignedUrl = _r2Storage.GeneratePresignedPartUploadUrl(
                    request.StorageKey, 
                    request.UploadId, 
                    partNumber, 
                    TimeSpan.FromMinutes(15));

                partsResponse.Add(new PartUrlResponse(partNumber, presignedUrl));
            }

            _logger.LogInformation("Successfully generated {Count} presigned part URLs for key '{StorageKey}'.",
                partsResponse.Count, request.StorageKey);

            return new MultipartPresignPartsResponse(partsResponse);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to generate presigned part URLs for key '{StorageKey}'", request.StorageKey);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            throw;
        }
    }
}
