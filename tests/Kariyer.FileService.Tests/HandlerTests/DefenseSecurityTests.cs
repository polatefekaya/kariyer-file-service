using System;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;
using Kariyer.FileService.Features.ConfirmUpload;
using Kariyer.FileService.Features.DeleteFile;
using Kariyer.FileService.Features.GetDownloadUrl;
using Kariyer.FileService.Features.GetFileDetails;
using Kariyer.FileService.Features.OverwriteFileContent;
using Kariyer.FileService.Features.UpdateFileDetails;
using Kariyer.FileService.Infrastructure.Caching;
using Kariyer.FileService.Infrastructure.Persistence;
using Kariyer.FileService.Infrastructure.Storage;

namespace Kariyer.FileService.Tests.HandlerTests;

public class DefenseSecurityTests
{
    private readonly FileDbContext _dbContext;
    private readonly IR2StorageService _r2Storage;
    private readonly ICacheService _cache;

    public DefenseSecurityTests()
    {
        DbContextOptions<FileDbContext> dbOptions = new DbContextOptionsBuilder<FileDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new FileDbContext(dbOptions);
        _r2Storage = Substitute.For<IR2StorageService>();
        _cache = Substitute.For<ICacheService>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("public/images/test.png")] // Missing ULID prefix
    [InlineData("public/images/../logo.png")] // Traversal
    [InlineData("../../etc/passwd")] // Absolute traversal
    [InlineData("invalid_folder/images/01HXP123456789012345678901-test.png")] // Invalid top folder
    [InlineData("public/invalid_category/01HXP123456789012345678901-test.png")] // Invalid category
    [InlineData("public/images/01HXP123456789012345678901-test.png/extra")] // Suffix injection
    public async Task KeyValidation_ShouldRejectInvalidStorageKeys_AcrossHandlers(string invalidKey)
    {
        // 1. ConfirmUpload
        var confirmLogger = Substitute.For<ILogger<ConfirmUploadHandler>>();
        var confirmHandler = new ConfirmUploadHandler(_dbContext, _r2Storage, _cache, confirmLogger);
        var confirmResult = await confirmHandler.HandleAsync(new ConfirmUploadRequest(invalidKey));
        Assert.Null(confirmResult);

        // 2. GetDownloadUrl
        var downloadLogger = Substitute.For<ILogger<GetDownloadUrlHandler>>();
        var config = Substitute.For<Microsoft.Extensions.Configuration.IConfiguration>();
        config["R2:CdnUrl"].Returns("https://cdn.kariyerzamani.com");
        var downloadHandler = new GetDownloadUrlHandler(_dbContext, _r2Storage, _cache, config, downloadLogger);
        var downloadResult = await downloadHandler.HandleAsync(invalidKey, "user-id", "employee");
        Assert.Null(downloadResult);

        // 3. DeleteFile
        var deleteLogger = Substitute.For<ILogger<DeleteFileHandler>>();
        var deleteHandler = new DeleteFileHandler(_dbContext, _r2Storage, _cache, deleteLogger);
        var deleteResult = await deleteHandler.HandleAsync(invalidKey, "user-id", "employee");
        Assert.Null(deleteResult);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("too-short")]
    [InlineData("01HXP1234567890123456789012345")] // Too long (29 chars)
    [InlineData("01HXP12345678901234567890@")] // Special character
    [InlineData("01HXP123456789012345678901' OR '1'='1")] // SQL Injection attempt
    public async Task IdValidation_ShouldRejectMalformedULIDs_AcrossHandlers(string malformedId)
    {
        // 1. GetFileDetails
        var detailsLogger = Substitute.For<ILogger<GetFileDetailsHandler>>();
        var detailsHandler = new GetFileDetailsHandler(_dbContext, detailsLogger);
        var detailsResult = await detailsHandler.HandleAsync(malformedId, "user-id", "employee");
        Assert.Null(detailsResult);

        // 2. UpdateFileDetails
        var updateLogger = Substitute.For<ILogger<UpdateFileDetailsHandler>>();
        var updateHandler = new UpdateFileDetailsHandler(_dbContext, _r2Storage, _cache, updateLogger);
        var updateResult = await updateHandler.HandleAsync(malformedId, new UpdateFileRequest("name.png", true), "user-id", "employee");
        Assert.Null(updateResult);

        // 3. OverwriteFileContent
        var overwriteLogger = Substitute.For<ILogger<OverwriteFileContentHandler>>();
        var overwriteHandler = new OverwriteFileContentHandler(_dbContext, _r2Storage, _cache, overwriteLogger);
        var overwriteResult = await overwriteHandler.HandleAsync(malformedId, "user-id", "employee");
        Assert.Null(overwriteResult);
    }

    [Fact]
    public async Task UpdateFileDetails_WithOverlyLongFilename_ShouldReject()
    {
        // Arrange
        string longName = new string('a', 300) + ".png";
        var logger = Substitute.For<ILogger<UpdateFileDetailsHandler>>();
        var handler = new UpdateFileDetailsHandler(_dbContext, _r2Storage, _cache, logger);

        // Act
        var result = await handler.HandleAsync("01HXP123456789012345678901", new UpdateFileRequest(longName, true), "user-id", "employee");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task UpdateFileDetails_WithNullRequest_ShouldReject()
    {
        // Arrange
        var logger = Substitute.For<ILogger<UpdateFileDetailsHandler>>();
        var handler = new UpdateFileDetailsHandler(_dbContext, _r2Storage, _cache, logger);

        // Act
        var result = await handler.HandleAsync("01HXP123456789012345678901", null!, "user-id", "employee");

        // Assert
        Assert.Null(result);
    }
}
