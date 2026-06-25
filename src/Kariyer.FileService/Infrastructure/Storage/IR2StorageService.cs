using System;
using System.Threading.Tasks;

namespace Kariyer.FileService.Infrastructure.Storage;

public interface IR2StorageService
{
    string GeneratePresignedUploadUrl(string key, string contentType, long fileSize, TimeSpan expiration);
    string GeneratePresignedDownloadUrl(string key, TimeSpan expiration);
    Task<bool> DeleteFileAsync(string key);
    Task<bool> FileExistsAsync(string key);
    Task<long?> GetObjectSizeAsync(string key);
    Task<bool> MoveFileAsync(string sourceKey, string destinationKey);
    Task<string> InitiateMultipartUploadAsync(string key, string contentType);
    string GeneratePresignedPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan expiration);
    Task<bool> CompleteMultipartUploadAsync(string key, string uploadId, System.Collections.Generic.IEnumerable<Amazon.S3.Model.PartETag> parts);
    Task<bool> AbortMultipartUploadAsync(string key, string uploadId);
}
