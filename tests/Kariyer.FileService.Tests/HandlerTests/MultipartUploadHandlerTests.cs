using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Amazon.S3.Model;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.MultipartUpload;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Tests.HandlerTests;

public class MultipartUploadHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;

    public MultipartUploadHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
    }

    [Fact]
    public async Task Initiate_WithValidParams_ShouldCallS3AndSavePendingRecord()
    {
        // Arrange
        var request = new MultipartInitiateRequest("portfolio.pdf", "application/pdf", 100 * 1024 * 1024, false);
        _r2Storage.InitiateMultipartUploadAsync(Arg.Any<string>(), "application/pdf").Returns("s3-upload-id");

        var logger = Substitute.For<ILogger<MultipartInitiateHandler>>();
        var handler = new MultipartInitiateHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("s3-upload-id", result.UploadId);
        Assert.StartsWith("private/documents/", result.StorageKey);

        // Verify DB record
        var dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.Id == result.FileId);
        Assert.Equal("Pending", dbRecord.Status);
        Assert.Equal("s3-upload-id", dbRecord.UploadId);
        Assert.Equal("portfolio.pdf", dbRecord.OriginalFileName);
    }

    [Fact]
    public async Task Initiate_WithOverlyLargeFileSize_ShouldReturnNull()
    {
        // Arrange - 600MB exceeds the 500MB limit
        var request = new MultipartInitiateRequest("portfolio.pdf", "application/pdf", 600L * 1024 * 1024, false);

        var logger = Substitute.For<ILogger<MultipartInitiateHandler>>();
        var handler = new MultipartInitiateHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Initiate_WithOverlySmallFileSize_ShouldReturnNull()
    {
        // Arrange - 4MB is below the 5MB multipart minimum limit
        var request = new MultipartInitiateRequest("portfolio.pdf", "application/pdf", 4L * 1024 * 1024, false);

        var logger = Substitute.For<ILogger<MultipartInitiateHandler>>();
        var handler = new MultipartInitiateHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task PresignParts_WithValidRequest_ShouldReturnUrls()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024, // 10MB
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.GeneratePresignedPartUploadUrl(key, "s3-upload-id", 1, Arg.Any<TimeSpan>()).Returns("https://r2.storage/part1");
        _r2Storage.GeneratePresignedPartUploadUrl(key, "s3-upload-id", 2, Arg.Any<TimeSpan>()).Returns("https://r2.storage/part2");

        var request = new MultipartPresignPartsRequest(key, "s3-upload-id", new List<int> { 1, 2 });
        var logger = Substitute.For<ILogger<MultipartPresignPartsHandler>>();
        var handler = new MultipartPresignPartsHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal(2, result.Parts.Count);
        Assert.Equal("https://r2.storage/part1", result.Parts.First(p => p.PartNumber == 1).PresignedUrl);
    }

    [Fact]
    public async Task PresignParts_WithUploadIdMismatch_ShouldReturnNull()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        var request = new MultipartPresignPartsRequest(key, "mismatched-upload-id", new List<int> { 1 });
        var logger = Substitute.For<ILogger<MultipartPresignPartsHandler>>();
        var handler = new MultipartPresignPartsHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task Complete_WithValidRequest_ShouldActivateRecordAndClearUploadId()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.CompleteMultipartUploadAsync(key, "s3-upload-id", Arg.Any<IEnumerable<PartETag>>()).Returns(true);
        _r2Storage.GetObjectSizeAsync(key).Returns(10L * 1024 * 1024);

        var request = new MultipartCompleteRequest(key, "s3-upload-id", new List<CompletedPartDto> { new CompletedPartDto(1, "etag1") });
        var logger = Substitute.For<ILogger<MultipartCompleteHandler>>();
        var handler = new MultipartCompleteHandler(_dbContext, _r2Storage, _cache, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("Active", result.Status);
        Assert.Null(result.UploadId);

        // Verify DB update
        var dbRecord = await _dbContext.StoredFiles.FirstAsync(f => f.Id == "01HXP123456789012345678901");
        Assert.Equal("Active", dbRecord.Status);

        // Verify Cache eviction & update
        await _cache.Received(1).SetAsync($"file:meta:{key}", dbRecord, Arg.Any<TimeSpan>());
        await _cache.Received(1).InvalidateAsync($"file:download:{key}");
    }

    [Fact]
    public async Task Abort_WithValidRequest_ShouldRemoveRecord()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.AbortMultipartUploadAsync(key, "s3-upload-id").Returns(true);

        var request = new MultipartAbortRequest(key, "s3-upload-id");
        var logger = Substitute.For<ILogger<MultipartAbortHandler>>();
        var handler = new MultipartAbortHandler(_dbContext, _r2Storage, _cache, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.True(result);

        // Verify DB record removed
        Assert.Null(await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == "01HXP123456789012345678901"));

        // Verify Cache invalidation
        await _cache.Received(1).InvalidateAsync($"file:meta:{key}");
        await _cache.Received(1).InvalidateAsync($"file:download:{key}");
    }

    [Fact]
    public async Task Complete_WhenSizeViolatesLimits_ShouldDeleteFromR2RemoveFromDBAndReturnNull()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024,
            Status = "Pending"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        _r2Storage.CompleteMultipartUploadAsync(key, "s3-upload-id", Arg.Any<IEnumerable<PartETag>>()).Returns(true);
        // Completed size is 1MB, violating minimum 5MB limit
        _r2Storage.GetObjectSizeAsync(key).Returns(1L * 1024 * 1024);
        _r2Storage.DeleteFileAsync(key).Returns(true);

        var request = new MultipartCompleteRequest(key, "s3-upload-id", new List<CompletedPartDto> { new CompletedPartDto(1, "etag1") });
        var logger = Substitute.For<ILogger<MultipartCompleteHandler>>();
        var handler = new MultipartCompleteHandler(_dbContext, _r2Storage, _cache, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.Null(result);

        // Verify record removed from DB
        var dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == "01HXP123456789012345678901");
        Assert.Null(dbRecord);

        // Verify physical deletion called
        await _r2Storage.Received(1).DeleteFileAsync(key);
    }

    [Fact]
    public async Task PresignParts_WhenStatusNotPending_ShouldReturnNull()
    {
        // Arrange
        string key = "public/images/01HXP123456789012345678901-logo.png";
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = key,
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id",
            UploadId = "s3-upload-id",
            FileSize = 10L * 1024 * 1024,
            Status = "Active" // Status is Active, not Pending
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        var request = new MultipartPresignPartsRequest(key, "s3-upload-id", new List<int> { 1 });
        var logger = Substitute.For<ILogger<MultipartPresignPartsHandler>>();
        var handler = new MultipartPresignPartsHandler(_dbContext, _r2Storage, logger);

        // Act
        var result = await handler.HandleAsync(request, "owner-id", "employee");

        // Assert
        Assert.Null(result);
    }
}
