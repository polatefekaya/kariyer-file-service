using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.UpdateFileDetails;

public record UpdateFileRequest(string? OriginalFileName, bool? IsPublic);

public class UpdateFileDetailsHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<UpdateFileDetailsHandler> _logger;

    public UpdateFileDetailsHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        ILogger<UpdateFileDetailsHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _logger = logger;
    }

    public async Task<StoredFile?> HandleAsync(string id, UpdateFileRequest request, string currentUserId, string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("UpdateFileDetailsHandler.Handle");
        activity?.SetTag("user.id", currentUserId);
        activity?.SetTag("user.role", userRole);
        activity?.SetTag("file.id", id);

        if (request == null)
        {
            _logger.LogWarning("Update failed: Request object is null.");
            activity?.SetStatus(ActivityStatusCode.Error, "Null request.");
            return null;
        }

        _logger.LogInformation("Updating file details for {FileId} requested by user {UserId} (role: {Role}). Requested name update: '{FileName}', public toggle: {IsPublic}",
            id, currentUserId, userRole, request.OriginalFileName, request.IsPublic);

        if (string.IsNullOrWhiteSpace(id) || id.Length != 26 || !Regex.IsMatch(id, "^[a-zA-Z0-9]{26}$"))
        {
            _logger.LogWarning("Update failed: Invalid or malformed ULID format for FileId '{FileId}'.", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid ULID format.");
            return null;
        }

        bool isAdmin = userRole == "admin" || userRole == "super_admin";

        StoredFile? fileRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == id);
        if (fileRecord == null)
        {
            _logger.LogWarning("File not found for update: FileId {FileId}.", id);
            FileServiceDiagnostics.FileDetailsUpdatedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "not_found"));
            activity?.SetStatus(ActivityStatusCode.Error, "File not found.");
            return null; // Not found
        }

        // Security check: Only owner or admin can update details
        if (fileRecord.UserId != currentUserId && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {CurrentUserId} attempted to update file details for {FileId} owned by {OwnerId}.",
                currentUserId, id, fileRecord.UserId);
            FileServiceDiagnostics.FileDetailsUpdatedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "unauthorized"));
            activity?.SetStatus(ActivityStatusCode.Error, "Unauthorized update attempt.");
            throw new UnauthorizedAccessException("You do not have permission to update this file.");
        }

        bool changed = false;
        bool isKeyChanged = false;
        string oldKey = fileRecord.StorageKey;
        string newKey = oldKey;

        if (request.OriginalFileName != null)
        {
            string safeFileName = Path.GetFileName(request.OriginalFileName);
            safeFileName = Regex.Replace(safeFileName, @"[^a-zA-Z0-9\._-]", "");
            if (safeFileName.Length > 255)
            {
                _logger.LogWarning("Update blocked: Sanitized filename '{FileName}' length exceeds 255 characters limit.", safeFileName);
                activity?.SetStatus(ActivityStatusCode.Error, "Filename too long.");
                return null;
            }

            if (!string.IsNullOrWhiteSpace(safeFileName))
            {
                _logger.LogInformation("Updating OriginalFileName for {FileId} from '{OldName}' to '{NewName}'.",
                    id, fileRecord.OriginalFileName, safeFileName);
                fileRecord.OriginalFileName = safeFileName;
                changed = true;
            }
            else
            {
                _logger.LogWarning("Rejecting OriginalFileName update for {FileId}: sanitized string was empty or invalid.", id);
            }
        }

        if (request.IsPublic.HasValue && fileRecord.IsPublic != request.IsPublic.Value)
        {
            _logger.LogInformation("Updating IsPublic for {FileId} from {OldVal} to {NewVal}.",
                id, fileRecord.IsPublic, request.IsPublic.Value);

            // Compute new StorageKey under correct prefix folder
            if (oldKey.StartsWith("public/"))
            {
                newKey = "private/" + oldKey.Substring("public/".Length);
            }
            else if (oldKey.StartsWith("private/"))
            {
                newKey = "public/" + oldKey.Substring("private/".Length);
            }

            if (newKey != oldKey)
            {
                _logger.LogInformation("Relocating physical object in Cloudflare R2 from '{OldKey}' to '{NewKey}' due to visibility toggle.", 
                    oldKey, newKey);
                
                bool moved = await _r2Storage.MoveFileAsync(oldKey, newKey);
                if (!moved)
                {
                    _logger.LogError("Failed to move physical object from '{OldKey}' to '{NewKey}' in R2. Aborting update.", 
                        oldKey, newKey);
                    activity?.SetStatus(ActivityStatusCode.Error, "Physical move failed.");
                    return null; // Return null to reject/abort
                }

                fileRecord.StorageKey = newKey;
                isKeyChanged = true;
            }

            fileRecord.IsPublic = request.IsPublic.Value;
            changed = true;
        }

        if (changed)
        {
            await _dbContext.SaveChangesAsync();

            // Cache Invalidation (Evict outdated values immediately)
            await _cache.InvalidateAsync($"file:meta:{oldKey}");
            await _cache.InvalidateAsync($"file:download:{oldKey}");

            if (isKeyChanged)
            {
                await _cache.InvalidateAsync($"file:meta:{newKey}");
                await _cache.InvalidateAsync($"file:download:{newKey}");
            }

            _logger.LogInformation("Successfully updated details and evicted cache keys for {FileId} (storage key: {StorageKey}).",
                id, fileRecord.StorageKey);

            FileServiceDiagnostics.FileDetailsUpdatedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        }
        else
        {
            _logger.LogInformation("No changes detected during update for file {FileId}.", id);
            FileServiceDiagnostics.FileDetailsUpdatedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "no_change"));
        }

        return fileRecord;
    }
}

