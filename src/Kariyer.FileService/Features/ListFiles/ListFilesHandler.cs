using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.ListFiles;

public record PagedResult<T>(List<T> Items, int Page, int PageSize, int TotalCount, int TotalPages);

public class ListFilesHandler
{
    private readonly FileDbContext _dbContext;
    private readonly ILogger<ListFilesHandler> _logger;

    public ListFilesHandler(FileDbContext dbContext, ILogger<ListFilesHandler> logger)
    {
        _dbContext = dbContext;
        _logger = logger;
    }

    public async Task<PagedResult<StoredFile>> HandleAsync(
        int page,
        int pageSize,
        string? userId,
        string? status,
        bool? isPublic,
        string currentUserId,
        string? userRole)
    {
        using Activity? activity = FileServiceDiagnostics.ActivitySource.StartActivity("ListFilesHandler.Handle");
        activity?.SetTag("user.id", currentUserId);
        activity?.SetTag("user.role", userRole);
        activity?.SetTag("filter.requested_user_id", userId);
        activity?.SetTag("filter.status", status);
        activity?.SetTag("filter.is_public", isPublic);
        activity?.SetTag("page.number", page);
        activity?.SetTag("page.size", pageSize);

        _logger.LogInformation("Listing files requested by user {CurrentUserId} (role: {Role}). Requested filter userId: {UserId}, status: {Status}, public: {IsPublic}",
            currentUserId, userRole, userId, status, isPublic);

        bool isAdmin = userRole == "admin" || userRole == "super_admin";

        // Security check: Non-admins are forced to query only their own files
        if (!isAdmin)
        {
            if (!string.IsNullOrWhiteSpace(userId) && userId != currentUserId)
            {
                _logger.LogWarning("Security enforcement: Non-admin user {CurrentUserId} attempted to query files for user {UserId}. Overriding to current user ID.",
                    currentUserId, userId);
            }
            userId = currentUserId;
        }

        var query = _dbContext.StoredFiles.AsNoTracking().AsQueryable();

        if (!string.IsNullOrWhiteSpace(userId))
        {
            query = query.Where(f => f.UserId == userId);
        }
        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(f => f.Status == status);
        }
        if (isPublic.HasValue)
        {
            query = query.Where(f => f.IsPublic == isPublic.Value);
        }

        int totalCount = await query.CountAsync();
        
        // Paging defaults & boundaries
        page = page < 1 ? 1 : page;
        pageSize = pageSize < 1 ? 10 : pageSize > 100 ? 100 : pageSize;

        var items = await query
             .OrderByDescending(f => f.CreatedAt)
             .Skip((page - 1) * pageSize)
             .Take(pageSize)
             .ToListAsync();

        int totalPages = (int)Math.Ceiling((double)totalCount / pageSize);

        _logger.LogInformation("Returning {Count} files out of total {TotalCount}. Page {Page} of {TotalPages}", 
            items.Count, totalCount, page, totalPages);

        FileServiceDiagnostics.FileListedCounter.Add(1, 
            new KeyValuePair<string, object?>("outcome", "success"),
            new KeyValuePair<string, object?>("is_admin", isAdmin.ToString()));

        return new PagedResult<StoredFile>(items, page, pageSize, totalCount, totalPages);
    }
}

