using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Features.GetDownloadUrl;

public record FileUrlResponse(string Url);

public class GetDownloadUrlHandler
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly string _cdnUrl;
    private readonly ILogger<GetDownloadUrlHandler> _logger;

    public GetDownloadUrlHandler(
        FileDbContext dbContext,
        IR2StorageService r2Storage,
        ICacheService cache,
        IConfiguration configuration,
        ILogger<GetDownloadUrlHandler> logger)
    {
        _dbContext = dbContext;
        _r2Storage = r2Storage;
        _cache = cache;
        _cdnUrl = (configuration["R2:CdnUrl"] ?? "").TrimEnd('/');
        _logger = logger;
    }

    public async Task<FileUrlResponse?> HandleAsync(string key, string userId, string? userRole)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            _logger.LogWarning("Download URL generation failed: Key parameter is empty.");
            return null;
        }

        // Strict StorageKey format validation to prevent path traversal, directory injection, or bucket escape
        if (!Regex.IsMatch(key, @"^(public|private)/(images|videos|documents)/[a-zA-Z0-9]{26}-[a-zA-Z0-9\._-]+$"))
        {
            _logger.LogWarning("Download URL generation failed: Malicious or invalid key format '{Key}'.", key);
            return null;
        }

        // Try getting file metadata from Garnet cache first
        string metaCacheKey = $"file:meta:{key}";
        StoredFile? fileRecord = await _cache.GetAsync<StoredFile>(metaCacheKey);

        if (fileRecord == null)
        {
            fileRecord = await _dbContext.StoredFiles.AsNoTracking().FirstOrDefaultAsync(f => f.StorageKey == key);
            if (fileRecord == null)
            {
                return null; // NotFound
            }
            // Populate metadata cache
            await _cache.SetAsync(metaCacheKey, fileRecord, TimeSpan.FromHours(24));
        }

        // State protection: only Active files can have download URLs generated
        if (fileRecord.Status != "Active")
        {
            _logger.LogWarning("Download URL generation blocked: File '{StorageKey}' is not Active (current status: {Status}).", 
                key, fileRecord.Status);
            return null; // NotFound
        }

        // If file is public, return CDN URL immediately
        if (fileRecord.IsPublic)
        {
            FileServiceDiagnostics.DownloadUrlGeneratedCounter.Add(1, new KeyValuePair<string, object?>("source", "cdn_direct"));
            return new FileUrlResponse($"{_cdnUrl}/{fileRecord.StorageKey}");
        }

        // If file is private, verify ownership or admin role (Security check)
        bool isOwner = fileRecord.UserId == userId;
        bool isAdmin = userRole == "admin" || userRole == "super_admin";
        if (!isOwner && !isAdmin)
        {
            throw new UnauthorizedAccessException("You do not have permission to access this file.");
        }

        // Try retrieving presigned GET URL from Garnet cache
        string urlCacheKey = $"file:download:{key}";
        string? cachedUrl = await _cache.GetAsync<string>(urlCacheKey);
        if (!string.IsNullOrEmpty(cachedUrl))
        {
            FileServiceDiagnostics.DownloadUrlGeneratedCounter.Add(1, new KeyValuePair<string, object?>("source", "cache_hit"));
            return new FileUrlResponse(cachedUrl);
        }

        // Cache miss: generate new URL and save to cache
        try
        {
            string url = _r2Storage.GeneratePresignedDownloadUrl(key, TimeSpan.FromMinutes(15));
            
            // Cache the url for 10 minutes (expires before the 15m presigned token)
            await _cache.SetAsync(urlCacheKey, url, TimeSpan.FromMinutes(10));

            FileServiceDiagnostics.DownloadUrlGeneratedCounter.Add(1, new KeyValuePair<string, object?>("source", "cache_miss"));
            return new FileUrlResponse(url);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error producing download URL for key '{Key}'", key);
            throw;
        }
    }
}
