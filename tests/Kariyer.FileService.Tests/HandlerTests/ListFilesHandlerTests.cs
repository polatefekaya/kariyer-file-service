using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.ListFiles;
using Kariyer.FileService.Infrastructure.Persistence;

namespace Kariyer.FileService.Tests.HandlerTests;

public class ListFilesHandlerTests
{
    private readonly FileDbContext _dbContext;
    private readonly ListFilesHandler _handler;

    public ListFilesHandlerTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        var logger = Substitute.For<Microsoft.Extensions.Logging.ILogger<ListFilesHandler>>();
        _handler = new ListFilesHandler(_dbContext, logger);
    }

    [Fact]
    public async Task HandleAsync_NonAdmin_ShouldBeForcedToOwnFilesOnly()
    {
        // Arrange
        var file1 = new StoredFile { Id = "1", StorageKey = "k1", UserId = "user-a", OriginalFileName = "a.pdf", Status = "Active" };
        var file2 = new StoredFile { Id = "2", StorageKey = "k2", UserId = "user-b", OriginalFileName = "b.pdf", Status = "Active" };
        _dbContext.StoredFiles.AddRange(file1, file2);
        await _dbContext.SaveChangesAsync();

        // Act - user-a requests files, attempts to filter by user-b
        var result = await _handler.HandleAsync(
            page: 1,
            pageSize: 10,
            userId: "user-b", // Attempted bypass
            status: null,
            isPublic: null,
            currentUserId: "user-a",
            userRole: "employee");

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("user-a", result.Items[0].UserId);
    }

    [Fact]
    public async Task HandleAsync_Admin_CanQueryOtherUsersFiles()
    {
        // Arrange
        var file1 = new StoredFile { Id = "1", StorageKey = "k1", UserId = "user-a", OriginalFileName = "a.pdf", Status = "Active" };
        var file2 = new StoredFile { Id = "2", StorageKey = "k2", UserId = "user-b", OriginalFileName = "b.pdf", Status = "Active" };
        _dbContext.StoredFiles.AddRange(file1, file2);
        await _dbContext.SaveChangesAsync();

        // Act - admin requests files for user-b
        var result = await _handler.HandleAsync(
            page: 1,
            pageSize: 10,
            userId: "user-b",
            status: null,
            isPublic: null,
            currentUserId: "admin-id",
            userRole: "admin");

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("user-b", result.Items[0].UserId);
    }

    [Fact]
    public async Task HandleAsync_PagingParametersBoundaries_ShouldClampValues()
    {
        // Arrange
        for (int i = 1; i <= 150; i++)
        {
            _dbContext.StoredFiles.Add(new StoredFile 
            { 
                Id = i.ToString(), 
                StorageKey = $"key-{i}", 
                UserId = "user-a", 
                OriginalFileName = $"file-{i}.png" 
            });
        }
        await _dbContext.SaveChangesAsync();

        // Act - invalid page numbers, oversized page size
        var result1 = await _handler.HandleAsync(-5, -10, null, null, null, "user-a", "admin");
        var result2 = await _handler.HandleAsync(1, 200, null, null, null, "user-a", "admin");

        // Assert
        Assert.Equal(1, result1.Page);
        Assert.Equal(10, result1.PageSize); // Minimum or default size
        
        Assert.Equal(100, result2.PageSize); // Clamped at max 100
        Assert.Equal(150, result2.TotalCount);
        Assert.Equal(2, result2.TotalPages);
    }

    [Fact]
    public async Task HandleAsync_WithStatusAndIsPublicFilters_ShouldFilterCorrectly()
    {
        // Arrange
        var file1 = new StoredFile { Id = "1", StorageKey = "k1", UserId = "user-a", OriginalFileName = "1.png", Status = "Active", IsPublic = true };
        var file2 = new StoredFile { Id = "2", StorageKey = "k2", UserId = "user-a", OriginalFileName = "2.png", Status = "Pending", IsPublic = true };
        var file3 = new StoredFile { Id = "3", StorageKey = "k3", UserId = "user-a", OriginalFileName = "3.png", Status = "Active", IsPublic = false };
        _dbContext.StoredFiles.AddRange(file1, file2, file3);
        await _dbContext.SaveChangesAsync();

        // Act
        var result = await _handler.HandleAsync(1, 10, null, "Active", true, "user-a", "employee");

        // Assert
        Assert.Single(result.Items);
        Assert.Equal("1", result.Items[0].Id);
    }
}
