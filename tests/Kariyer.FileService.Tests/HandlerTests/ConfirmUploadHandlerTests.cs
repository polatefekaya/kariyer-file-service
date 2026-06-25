using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Features.ConfirmUpload;

namespace Kariyer.FileService.Tests.HandlerTests;

public class ConfirmUploadHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<ConfirmUploadHandler> _logger;
    private readonly ConfirmUploadHandler _handler;

    public ConfirmUploadHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
        _logger = Substitute.For<ILogger<ConfirmUploadHandler>>();
        _handler = new ConfirmUploadHandler(_dbContext, _r2Storage, _cache, _logger);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentKey_ShouldReturnNull()
    {
        // Arrange
        ConfirmUploadRequest request = new("public/images/01HXP123456789012345678901-missing.png");

        // Act
        bool? result = await _handler.HandleAsync(request);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_WhenR2FileDoesNotExist_ShouldReturnFalseAndNotActivate()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-test.png";
        StoredFile fileRecord = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "test.png",
            ContentType = "image/png",
            FileSize = 100,
            UserId = "user-id",
            IsPublic = true,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(fileRecord);
        await _dbContext.SaveChangesAsync();

        ConfirmUploadRequest request = new(storageKey);
        _r2Storage.GetObjectSizeAsync(storageKey).Returns((long?)null);

        // Act
        bool? result = await _handler.HandleAsync(request);

        // Assert
        Assert.False(result);
        
        // State should remain Pending
        StoredFile dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.StorageKey == storageKey);
        Assert.Equal("Pending", dbRecord.Status);
        
        // Cache should not be populated
        await _cache.DidNotReceive().SetAsync(Arg.Any<string>(), Arg.Any<StoredFile>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task HandleAsync_WhenFileExistsOnR2_ShouldActivateAndCache()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-test.png";
        StoredFile fileRecord = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "test.png",
            ContentType = "image/png",
            FileSize = 100,
            UserId = "user-id",
            IsPublic = true,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(fileRecord);
        await _dbContext.SaveChangesAsync();

        ConfirmUploadRequest request = new(storageKey);
        _r2Storage.GetObjectSizeAsync(storageKey).Returns(100L);

        // Act
        bool? result = await _handler.HandleAsync(request);

        // Assert
        Assert.True(result);

        // Verify status changed to Active
        StoredFile dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.StorageKey == storageKey);
        Assert.Equal("Active", dbRecord.Status);

        // Verify metadata was stored in cache
        string expectedCacheKey = $"file:meta:{storageKey}";
        await _cache.Received(1).SetAsync(
            expectedCacheKey, 
            Arg.Is<StoredFile>(f => f.StorageKey == storageKey && f.Status == "Active"), 
            Arg.Any<TimeSpan>()
        );
    }

    [Fact]
    public async Task HandleAsync_WhenSizeExceedsLimit_ShouldDeleteFromR2RemoveFromDBAndReturnNull()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-test.png";
        StoredFile fileRecord = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "test.png",
            ContentType = "image/png",
            FileSize = 100,
            UserId = "user-id",
            IsPublic = true,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(fileRecord);
        await _dbContext.SaveChangesAsync();

        ConfirmUploadRequest request = new(storageKey);
        // Returns 60MB, which exceeds 50MB limit
        _r2Storage.GetObjectSizeAsync(storageKey).Returns(60L * 1024 * 1024);
        _r2Storage.DeleteFileAsync(storageKey).Returns(true);

        // Act
        bool? result = await _handler.HandleAsync(request);

        // Assert
        Assert.Null(result);
        
        // Verify record removed from DB
        var dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == storageKey);
        Assert.Null(dbRecord);

        // Verify physical deletion called
        await _r2Storage.Received(1).DeleteFileAsync(storageKey);
    }
}
