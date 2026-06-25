using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.UpdateFileDetails;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Tests.HandlerTests;

public class UpdateFileDetailsHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly UpdateFileDetailsHandler _handler;

    public UpdateFileDetailsHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<UpdateFileDetailsHandler>>();
        _handler = new UpdateFileDetailsHandler(_dbContext, _r2Storage, _cache, logger);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentId_ShouldReturnNull()
    {
        // Act
        var result = await _handler.HandleAsync("missing-id", new UpdateFileRequest("new.png", true), "user-id", "employee");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthorizedUserAttemptsUpdate_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = "public/images/01HXP123456789012345678901-logo.png",
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _handler.HandleAsync("01HXP123456789012345678901", new UpdateFileRequest("new.png", null), "intruder-id", "employee")
        );
    }

    [Fact]
    public async Task HandleAsync_WhenOwnerUpdates_ShouldModifyRecordAndInvalidateCache()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            IsPublic = false
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.MoveFileAsync(Arg.Any<string>(), Arg.Any<string>()).Returns(true);

        // Act
        var result = await _handler.HandleAsync(
            "01HXP123456789012345678901", 
            new UpdateFileRequest("safe_new_name.png", true), 
            "owner-id", 
            "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("safe_new_name.png", result.OriginalFileName);
        Assert.True(result.IsPublic);

        // Verify changes saved in DB
        var dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.Id == "01HXP123456789012345678901");
        Assert.Equal("safe_new_name.png", dbRecord.OriginalFileName);
        Assert.True(dbRecord.IsPublic);

        // Verify cache keys evicted
        await _cache.Received(1).InvalidateAsync($"file:meta:{storageKey}");
        await _cache.Received(1).InvalidateAsync($"file:download:{storageKey}");
    }

    [Fact]
    public async Task HandleAsync_WithUnsafeNameUpdate_ShouldSanitizeFileName()
    {
        // Arrange
        string storageKey = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = storageKey,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act - Update with traversal path and special chars
        var result = await _handler.HandleAsync(
            "01HXP123456789012345678901", 
            new UpdateFileRequest("../../../malicious_name#$.png", null), 
            "owner-id", 
            "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("malicious_name.png", result.OriginalFileName);
    }
}
