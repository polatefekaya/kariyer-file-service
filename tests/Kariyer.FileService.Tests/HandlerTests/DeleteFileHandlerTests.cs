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
using Kariyer.FileService.Features.DeleteFile;

namespace Kariyer.FileService.Tests.HandlerTests;

public class DeleteFileHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly ILogger<DeleteFileHandler> _logger;
    private readonly DeleteFileHandler _handler;

    public DeleteFileHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
        _logger = Substitute.For<ILogger<DeleteFileHandler>>();
        _handler = new DeleteFileHandler(_dbContext, _r2Storage, _cache, _logger);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentFile_ShouldReturnNull()
    {
        // Act
        bool? result = await _handler.HandleAsync("public/images/01HXP123456789012345678901-missing.png", "owner-id", null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsNotOwnerOrAdmin_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        string key = "private/documents/01HXP123456789012345678901-secret.pdf";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "secret.pdf",
            ContentType = "application/pdf",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _handler.HandleAsync(key, "unauthorized-user-id", "employee")
        );
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsOwner_ShouldDeleteFileAndInvalidateCache()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-avatar.png";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "avatar.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.DeleteFileAsync(key).Returns(true);

        // Act
        bool? result = await _handler.HandleAsync(key, "owner-id", "employee");

        // Assert
        Assert.True(result);

        // Verify removed from DB
        StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == key);
        Assert.Null(dbRecord);

        // Verify R2 deletion was called
        await _r2Storage.Received(1).DeleteFileAsync(key);

        // Verify cache keys evicted
        await _cache.Received(1).InvalidateAsync($"file:meta:{key}");
        await _cache.Received(1).InvalidateAsync($"file:download:{key}");
    }

    [Fact]
    public async Task HandleAsync_WhenUserIsAdmin_ShouldDeleteFileAndInvalidateCache()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.DeleteFileAsync(key).Returns(true);

        // Act
        bool? result = await _handler.HandleAsync(key, "admin-user-id", "admin");

        // Assert
        Assert.True(result);

        // Verify database entry removed
        Assert.Null(await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.StorageKey == key));

        // Verify invalidation
        await _cache.Received(1).InvalidateAsync($"file:meta:{key}");
        await _cache.Received(1).InvalidateAsync($"file:download:{key}");
    }
}
