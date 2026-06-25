using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.BackgroundServices;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Tests.HandlerTests;

public class OrphanedUploadCleanupServiceTests
{
    [Fact]
    public async Task CleanupService_ShouldRemoveStalePendingUploads_AndKeepRecentOnes()
    {
        // Arrange
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using (var setupDb = new FileDbContext(dbOptions))
        {
            // 1. Stale pending file (30 hours old) - should be deleted
            setupDb.StoredFiles.Add(new StoredFile
            {
                Id = "01HXP123456789012345678901",
                StorageKey = "public/images/01HXP123456789012345678901-stale.png",
                OriginalFileName = "stale.png",
                ContentType = "image/png",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow.AddHours(-30)
            });

            // 2. Recent pending file (1 hour old) - should NOT be deleted
            setupDb.StoredFiles.Add(new StoredFile
            {
                Id = "01HXP123456789012345678902",
                StorageKey = "public/images/01HXP123456789012345678902-recent.png",
                OriginalFileName = "recent.png",
                ContentType = "image/png",
                Status = "Pending",
                CreatedAt = DateTime.UtcNow.AddHours(-1)
            });

            // 3. Stale active file (30 hours old but Active status) - should NOT be deleted
            setupDb.StoredFiles.Add(new StoredFile
            {
                Id = "01HXP123456789012345678903",
                StorageKey = "public/images/01HXP123456789012345678903-active.png",
                OriginalFileName = "active.png",
                ContentType = "image/png",
                Status = "Active",
                CreatedAt = DateTime.UtcNow.AddHours(-30)
            });

            await setupDb.SaveChangesAsync();
        }

        // Mock Service Provider and Scope Factory
        var serviceProvider = Substitute.For<IServiceProvider>();
        var dbContextForScope = new FileDbContext(dbOptions);
        var r2Storage = Substitute.For<IR2StorageService>();
        serviceProvider.GetService(typeof(FileDbContext)).Returns(dbContextForScope);
        serviceProvider.GetService(typeof(IR2StorageService)).Returns(r2Storage);

        var serviceScope = Substitute.For<IServiceScope>();
        serviceScope.ServiceProvider.Returns(serviceProvider);

        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        scopeFactory.CreateScope().Returns(serviceScope);

        var logger = Substitute.For<ILogger<OrphanedUploadCleanupService>>();
        var service = new OrphanedUploadCleanupService(scopeFactory, logger);

        // Act - Start service, let it run first pass, then cancel
        var cts = new CancellationTokenSource();
        Task serviceTask = service.StartAsync(cts.Token);

        // Wait brief moment for background thread to query/save
        await Task.Delay(100);

        // Cancel to stop the loop
        cts.Cancel();
        await service.StopAsync(CancellationToken.None);

        // Assert database state
        using (var assertDb = new FileDbContext(dbOptions))
        {
            var remainingFiles = await assertDb.StoredFiles.ToListAsync();
            
            // Should contain exactly 2 files (recent pending and stale active)
            Assert.Equal(2, remainingFiles.Count);
            
            // Stale pending file must be deleted
            Assert.Null(remainingFiles.FirstOrDefault(f => f.Id == "01HXP123456789012345678901"));
            
            // Recent pending file remains
            Assert.NotNull(remainingFiles.FirstOrDefault(f => f.Id == "01HXP123456789012345678902"));
            
            // Stale active file remains
            Assert.NotNull(remainingFiles.FirstOrDefault(f => f.Id == "01HXP123456789012345678903"));
        }
    }
}
