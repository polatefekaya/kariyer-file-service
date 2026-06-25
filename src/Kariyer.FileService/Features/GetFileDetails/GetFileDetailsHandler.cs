using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.GetFileDetails;

public class GetFileDetailsHandler
{
    private readonly FileDbContext _dbContext;
    private readonly ILogger<GetFileDetailsHandler> _logger;

    public GetFileDetailsHandler(FileDbContext dbContext, ILogger<GetFileDetailsHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<StoredFile?> HandleAsync(string id, string currentUserId, string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("GetFileDetailsHandler.Handle");
        activity?.SetTag("user.id", currentUserId);
        activity?.SetTag("user.role", userRole);
        activity?.SetTag("file.id", id);

        _logger.LogInformation("Retrieving file details for file {FileId} requested by user {UserId} (role: {Role}).",
            id, currentUserId, userRole);

        if (string.IsNullOrWhiteSpace(id) || id.Length != 26 || !Regex.IsMatch(id, "^[a-zA-Z0-9]{26}$"))
        {
            _logger.LogWarning("Get Details failed: Invalid or malformed ULID format for FileId '{FileId}'.", id);
            activity?.SetStatus(ActivityStatusCode.Error, "Invalid ULID format.");
            return null;
        }

        bool isAdmin = userRole == "admin" || userRole == "super_admin";

        StoredFile? fileRecord = await _dbContext.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
        if (fileRecord == null)
        {
            _logger.LogWarning("File details not found for FileId {FileId}.", id);
            FileServiceDiagnostics.FileDetailsRequestedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "not_found"));
            activity?.SetStatus(ActivityStatusCode.Error, "File not found.");
            return null; // Not found
        }

        // Security check: Only owner or admin can view file details
        if (fileRecord.UserId != currentUserId && !isAdmin)
        {
            _logger.LogWarning("Access Denied: User {CurrentUserId} attempted to view file details for {FileId} owned by {OwnerId}.",
                currentUserId, id, fileRecord.UserId);
            FileServiceDiagnostics.FileDetailsRequestedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "unauthorized"));
            activity?.SetStatus(ActivityStatusCode.Error, "Unauthorized access attempt.");
            throw new UnauthorizedAccessException("You do not have permission to view this file details.");
        }

        _logger.LogInformation("File details successfully retrieved for {FileId}.", id);
        FileServiceDiagnostics.FileDetailsRequestedCounter.Add(1, new KeyValuePair<string, object?>("outcome", "success"));
        return fileRecord;
    }
}

