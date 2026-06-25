using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;
using Kariyer.FileService.Features.PresignedUpload;

namespace Kariyer.FileService.Tests.HandlerTests;

public class PresignedUploadHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ILogger<PresignedUploadHandler> _logger;
    private readonly PresignedUploadHandler _handler;

    public PresignedUploadHandlerTests()
    {
        // Mock DbContext using EF Core InMemory provider
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        
        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _logger = Substitute.For<ILogger<PresignedUploadHandler>>();
        _handler = new PresignedUploadHandler(_dbContext, _r2Storage, _logger);
    }

    [Fact]
    public async Task HandleAsync_WithValidParams_ShouldInsertPendingRecordAndReturnPresignedUrl()
    {
        // Arrange
        PresignedUploadRequest request = new("profile.png", "image/png", 1024, true);
        string userId = "test-user-uuid";
        _r2Storage.GeneratePresignedUploadUrl(Arg.Any<string>(), "image/png", 1024, Arg.Any<TimeSpan>())
            .Returns("https://r2.storage/presigned-put-url");

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, userId);

        // Assert
        Assert.NotNull(response);
        Assert.NotEmpty(response.FileId);
        Assert.StartsWith("public/images/", response.StorageKey);
        Assert.Contains(response.FileId, response.StorageKey);
        Assert.Equal("https://r2.storage/presigned-put-url", response.PresignedUrl);

        // Verify DB entry
        Domain.StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == response.FileId);
        Assert.NotNull(dbRecord);
        Assert.Equal("profile.png", dbRecord.OriginalFileName);
        Assert.Equal("image/png", dbRecord.ContentType);
        Assert.Equal(1024, dbRecord.FileSize);
        Assert.Equal(userId, dbRecord.UserId);
        Assert.Equal("Pending", dbRecord.Status);
        Assert.True(dbRecord.IsPublic);
    }

    [Fact]
    public async Task HandleAsync_WithUnsafeFilename_ShouldSanitizeFileName()
    {
        // Arrange
        PresignedUploadRequest request = new("../../../../unsafe-name#$.png", "image/png", 1024, true);
        _r2Storage.GeneratePresignedUploadUrl(Arg.Any<string>(), "image/png", 1024, Arg.Any<TimeSpan>())
            .Returns("https://r2.storage/presigned-put-url");

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.NotNull(response);
        Assert.Contains("unsafe-name.png", response.StorageKey);
        
        Domain.StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == response.FileId);
        Assert.NotNull(dbRecord);
        Assert.Equal("unsafe-name.png", dbRecord.OriginalFileName);
    }

    [Fact]
    public async Task HandleAsync_WithUnsafeMimeType_ShouldReturnNull()
    {
        // Arrange
        PresignedUploadRequest request = new("virus.exe", "application/x-msdownload", 1024, true);

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.Null(response);
        Assert.Empty(_dbContext.StoredFiles);
    }

    [Fact]
    public async Task HandleAsync_WithSizeExceedingLimits_ShouldReturnNull()
    {
        // Arrange
        long size51Mb = 51L * 1024 * 1024;
        PresignedUploadRequest request = new("movie.mp4", "video/mp4", size51Mb, true);

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.Null(response);
        Assert.Empty(_dbContext.StoredFiles);
    }

    [Fact]
    public async Task HandleAsync_WithMimeSpoofing_ShouldReturnNull()
    {
        // Arrange - Attacker names it payload.exe but sends image/png ContentType
        PresignedUploadRequest request = new("payload.exe", "image/png", 1024, true);

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.Null(response);
        Assert.Empty(_dbContext.StoredFiles);
    }

    [Fact]
    public async Task HandleAsync_WithNullByteInFileName_ShouldStripNullByteAndSucceedIfSafe()
    {
        // Arrange
        PresignedUploadRequest request = new("avatar.png\0", "image/png", 1024, true);
        _r2Storage.GeneratePresignedUploadUrl(Arg.Any<string>(), "image/png", 1024, Arg.Any<TimeSpan>())
            .Returns("https://r2.storage/url");

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.NotNull(response);
        Assert.Contains("avatar.png", response.StorageKey);

        Domain.StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == response.FileId);
        Assert.NotNull(dbRecord);
        Assert.Equal("avatar.png", dbRecord.OriginalFileName);
    }

    [Fact]
    public async Task HandleAsync_WithSqlInjectionInFileName_ShouldSanitizeSuccessfully()
    {
        // Arrange
        PresignedUploadRequest request = new("logo'; DROP TABLE storage.\"StoredFiles\";--.png", "image/png", 1024, true);
        _r2Storage.GeneratePresignedUploadUrl(Arg.Any<string>(), "image/png", 1024, Arg.Any<TimeSpan>())
            .Returns("https://r2.storage/url");

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.NotNull(response);
        Assert.Contains("logoDROPTABLEstorage.StoredFiles--.png", response.StorageKey);

        Domain.StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == response.FileId);
        Assert.NotNull(dbRecord);
        Assert.Equal("logoDROPTABLEstorage.StoredFiles--.png", dbRecord.OriginalFileName);
    }

    [Fact]
    public async Task HandleAsync_WithDoubleDotPathTraversal_ShouldResolveToFileNameOnly()
    {
        // Arrange
        PresignedUploadRequest request = new("....//....//etc/passwd.pdf", "application/pdf", 1024, false);
        _r2Storage.GeneratePresignedUploadUrl(Arg.Any<string>(), "application/pdf", 1024, Arg.Any<TimeSpan>())
            .Returns("https://r2.storage/url");

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.NotNull(response);
        Assert.Contains("passwd.pdf", response.StorageKey);

        Domain.StoredFile? dbRecord = await _dbContext.StoredFiles.FirstOrDefaultAsync(f => f.Id == response.FileId);
        Assert.NotNull(dbRecord);
        Assert.Equal("passwd.pdf", dbRecord.OriginalFileName);
    }

    [Fact]
    public async Task HandleAsync_WithNegativeSize_ShouldReturnNull()
    {
        // Arrange
        PresignedUploadRequest request = new("cv.pdf", "application/pdf", -100, false);

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.Null(response);
        Assert.Empty(_dbContext.StoredFiles);
    }

    [Fact]
    public async Task HandleAsync_WithZeroSize_ShouldReturnNull()
    {
        // Arrange
        PresignedUploadRequest request = new("cv.pdf", "application/pdf", 0, false);

        // Act
        PresignedUploadResponse? response = await _handler.HandleAsync(request, "user-id");

        // Assert
        Assert.Null(response);
        Assert.Empty(_dbContext.StoredFiles);
    }
}
