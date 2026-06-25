using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.GetFileDetails;
using Kariyer.FileService.Infrastructure.Persistence;

namespace Kariyer.FileService.Tests.HandlerTests;

public class GetFileDetailsHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly GetFileDetailsHandler _handler;

    public GetFileDetailsHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<GetFileDetailsHandler>>();
        _handler = new GetFileDetailsHandler(_dbContext, logger);
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
    public async Task HandleAsync_WhenOwnerRequests_ShouldReturnFileDetails()
    {
        // Arrange
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = "public/images/logo.png",
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _handler.HandleAsync("01HXP123456789012345678901", "owner-id", "employee");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("01HXP123456789012345678901", result.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenAdminRequestsOtherUserFile_ShouldReturnFileDetails()
    {
        // Arrange
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = "public/images/logo.png",
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _handler.HandleAsync("01HXP123456789012345678901", "admin-user", "admin");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("01HXP123456789012345678901", result.Id);
    }

    [Fact]
    public async Task HandleAsync_WhenUnauthorizedUserRequests_ShouldThrowUnauthorizedAccessException()
    {
        // Arrange
        var file = new StoredFile
        {
            Id = "01HXP123456789012345678901",
            StorageKey = "public/images/logo.png",
            OriginalFileName = "logo.png",
            ContentType = "image/png",
            UserId = "owner-id"
        };
        _dbContext.StoredFiles.Add(file);
        await _dbContext.SaveChangesAsync();

        // Act & Assert
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            _handler.HandleAsync("01HXP123456789012345678901", "intruder-id", "employee")
        );
    }
}
