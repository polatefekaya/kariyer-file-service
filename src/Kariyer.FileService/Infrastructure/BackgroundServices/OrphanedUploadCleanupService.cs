using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Infrastructure.BackgroundServices;

public class OrphanedUploadCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<OrphanedUploadCleanupService> _logger;
    private readonly TimeSpan _cleanupInterval = TimeSpan.FromHours(1);

    public OrphanedUploadCleanupService(IServiceScopeFactory scopeFactory, ILogger<OrphanedUploadCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("OrphanedUploadCleanupService background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Execute cleanup check
                await CleanOrphanedUploadsAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred executing orphaned upload cleanup.");
            }

            try
            {
                // Wait for next execution cycle
                await Task.Delay(_cleanupInterval, stoppingToken);
            }
            catch (TaskCanceledException)
            {
                // Service shutting down
                break;
            }
        }

        _logger.LogInformation("OrphanedUploadCleanupService background service stopping.");
    }

    private async Task CleanOrphanedUploadsAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Initiating cleanup check for orphaned Pending uploads...");

        using IServiceScope scope = _scopeFactory.CreateScope();
        FileDbContext dbContext = scope.ServiceProvider.GetRequiredService<FileDbContext>();
        IR2StorageService r2Storage = scope.ServiceProvider.GetRequiredService<IR2StorageService>();

        DateTime threshold = DateTime.UtcNow.AddHours(-24);

        // Fetch stale records (bounded to prevent memory bloat)
        var staleFiles = await dbContext.StoredFiles
            .Where(f => f.Status == "Pending" && f.CreatedAt < threshold)
            .Take(100)
            .ToListAsync(stoppingToken);

        if (staleFiles.Any())
        {
            _logger.LogInformation("Found {Count} orphaned Pending uploads to clean up.", staleFiles.Count);

            foreach (var file in staleFiles)
            {
                if (!string.IsNullOrWhiteSpace(file.UploadId))
                {
                    _logger.LogInformation("Aborting active R2 Multipart Upload session '{UploadId}' for key '{Key}' during cleanup.", 
                        file.UploadId, file.StorageKey);
                    
                    bool aborted = await r2Storage.AbortMultipartUploadAsync(file.StorageKey, file.UploadId);
                    if (!aborted)
                    {
                        _logger.LogWarning("Failed to abort multipart upload for key '{Key}' during cleanup. Proceeding to delete record anyway.", 
                            file.StorageKey);
                    }
                }
            }

            dbContext.StoredFiles.RemoveRange(staleFiles);
            await dbContext.SaveChangesAsync(stoppingToken);

            _logger.LogInformation("Successfully deleted {Count} orphaned Pending uploads from database.", staleFiles.Count);
        }
        else
        {
            _logger.LogInformation("No orphaned Pending uploads found to clean up.");
        }
    }
}
