using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Features.GetDownloadUrl;

namespace Kariyer.FileService.Tests.HandlerTests;

public class GetDownloadUrlHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;
    private readonly IConfiguration _config;
    private readonly ILogger<GetDownloadUrlHandler> _logger;
    private readonly GetDownloadUrlHandler _handler;

    public GetDownloadUrlHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
        _logger = Substitute.For<ILogger<GetDownloadUrlHandler>>();

        // Setup mock config
        _config = Substitute.For<IConfiguration>();
        _config["R2:CdnUrl"].Returns("https://cdn.kariyerzamani.com");

        _handler = new GetDownloadUrlHandler(_dbContext, _r2Storage, _cache, _config, _logger);
    }

    [Fact]
    public async Task HandleAsync_WithNonExistentKey_ShouldReturnNull()
    {
        // Act
        FileUrlResponse? result = await _handler.HandleAsync("public/images/01HXP123456789012345678901-missing.png", "user-id", null);

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task HandleAsync_WhenFileIsPublic_ShouldReturnCdnUrlDirectly()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            IsPublic = true,
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act
        FileUrlResponse? result = await _handler.HandleAsync(key, "any-user", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("https://cdn.kariyerzamani.com/public/images/01HXP123456789012345678901-logo.png", result.Url);
        
        // Should not generate presigned URLs or call private cache
        _r2Storage.DidNotReceive().GeneratePresignedDownloadUrl(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task HandleAsync_WhenPrivateAndUserIsUnauthorized_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        string key = "private/documents/01HXP123456789012345678901-secret.pdf";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "secret.pdf",
            ContentType = "application/pdf",
            IsPublic = false,
            UserId = "owner-id",
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => 
            _handler.HandleAsync(key, "unauthorized-user-id", "employee")
        );
    }

    [Fact]
    public async Task HandleAsync_WhenPrivateAndCacheHit_ShouldReturnCachedUrl()
    {
        // Arrange
        string key = "private/documents/01HXP123456789012345678901-cv.pdf";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "cv.pdf",
            ContentType = "application/pdf",
            IsPublic = false,
            UserId = "owner-id",
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        string cachedUrl = "https://r2.storage/cv.pdf?token=cached";
        _cache.GetAsync<string>($"file:download:{key}").Returns(cachedUrl);

        // Act
        FileUrlResponse? result = await _handler.HandleAsync(key, "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(cachedUrl, result.Url);

        // Should bypass R2 signature generation
        _r2Storage.DidNotReceive().GeneratePresignedDownloadUrl(Arg.Any<string>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task HandleAsync_WhenPrivateAndCacheMiss_ShouldGenerateAndCacheUrl()
    {
        // Arrange
        string key = "private/documents/01HXP123456789012345678901-cv.pdf";
        StoredFile file = new()
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "cv.pdf",
            ContentType = "application/pdf",
            IsPublic = false,
            UserId = "owner-id",
            Status = "Active"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        string generatedUrl = "https://r2.storage/cv.pdf?token=generated";
        _r2Storage.GeneratePresignedDownloadUrl(key, Arg.Any<TimeSpan>()).Returns(generatedUrl);
        _cache.GetAsync<string>($"file:download:{key}").Returns((string?)null); // Cache miss

        // Act
        FileUrlResponse? result = await _handler.HandleAsync(key, "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(generatedUrl, result.Url);

        // Verify R2 generation was called
        _r2Storage.Received(1).GeneratePresignedDownloadUrl(key, Arg.Any<TimeSpan>());

        // Verify result was stored in Garnet cache
        await _cache.Received(1).SetAsync($"file:download:{key}", generatedUrl, Arg.Any<TimeSpan>());
    }
}
