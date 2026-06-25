using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.OverwriteFileContent;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Tests.HandlerTests;

public class OverwriteFileContentHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly OverwriteFileContentHandler _handler;

    public OverwriteFileContentHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
        
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<OverwriteFileContentHandler>>();
        _handler = new OverwriteFileContentHandler(_dbContext, _r2Storage, _cache, logger);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentId_ShouldReturnNull()
    {
        // Act
        var result = await _handler.HandleAsync("missing-id", "user-id", "employee");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthorizedUserAttemptsOverwrite_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = "public/images/01HXP123456789012345678901-logo.png",
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _handler.HandleAsync("01HXP123456789012345678901", "intruder-id", "employee")
        );
    }

    [Fact]
    public async Task HandleAsync_WhenOwnerOverwrites_ShouldReturnPresignedUrlAndResetStatusAndEvictCache()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            FileSize = 2048,
            UserId = "owner-id",
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        string expectedUrl = "https://r2.storage/presigned-put-url";
        _r2Storage.GeneratePresignedUploadUrl(storageKey, "image/png", 2048, Arg.Any<TimeSpan>())
            .Returns(expectedUrl);

        // Act
        var result = await _handler.HandleAsync("01HXP123456789012345678901", "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("01HXP123456789012345678901", result.FileId);
        Assert.Equal(storageKey, result.StorageKey);
        Assert.Equal(expectedUrl, result.PresignedUrl);

        // Verify status reverted to Pending
        var dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.Id == "01HXP123456789012345678901");
        Assert.Equal("Pending", dbRecord.Status);

        // Verify R2 service was invoked with original properties
        _r2Storage.Received(1).GeneratePresignedUploadUrl(storageKey, "image/png", 2048, Arg.Any<TimeSpan>());

        // Verify cache keys evicted
        await _cache.Received(1).InvalidateAsync($"file:meta:{storageKey}");
        await _cache.Received(1).InvalidateAsync($"file:download:{storageKey}");
    }
}
