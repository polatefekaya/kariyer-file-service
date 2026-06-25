using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Kariyer.FileService.Infrastructure.Telemetry;

namespace Kariyer.FileService.Infrastructure.Storage;

public class R2StorageService : IR2StorageService
{
    private readonly IAmazonS3 _s3Client;
    private readonly string _bucketName;
    private readonly ILogger<R2StorageService> _logger;

    public R2StorageService(IAmazonS3 s3Client, IConfiguration configuration, ILogger<R2StorageService> logger)
    {
        _s3Client = s3Client;
        _bucketName = configuration["R2:BucketName"] 
            ?? throw new ArgumentNullException(nameof(configuration), "R2:BucketName configuration is missing.");
        _logger = logger;
    }

    public string GeneratePresignedUploadUrl(string key, string contentType, long fileSize, TimeSpan expiration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            GetPreSignedUrlRequest request = new()
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.Add(expiration),
                ContentType = contentType
            };

            // Restrict upload content length dynamically if required
            request.Headers["Content-Length"] = fileSize.ToString();

            string url = _s3Client.GetPreSignedURL(request);
            stopwatch.Stop();

            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_presigned_put"));

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating presigned upload URL for key '{Key}'", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_presigned_put_error"));
            throw;
        }
    }

    public string GeneratePresignedDownloadUrl(string key, TimeSpan expiration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            GetPreSignedUrlRequest request = new()
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.GET,
                Expires = DateTime.UtcNow.Add(expiration)
            };

            string url = _s3Client.GetPreSignedURL(request);
            stopwatch.Stop();

            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_presigned_get"));

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating presigned download URL for key '{Key}'", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_presigned_get_error"));
            throw;
        }
    }

    public async Task<bool> DeleteFileAsync(string key)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            DeleteObjectRequest request = new()
            {
                BucketName = _bucketName,
                Key = key
            };

            await _s3Client.DeleteObjectAsync(request);
            stopwatch.Stop();

            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "delete"));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file with key '{Key}' from R2", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "delete_error"));
            return false;
        }
    }

    public async Task<bool> FileExistsAsync(string key)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            GetObjectMetadataRequest request = new()
            {
                BucketName = _bucketName,
                Key = key
            };

            await _s3Client.GetObjectMetadataAsync(request);
            stopwatch.Stop();

            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "exists"));

            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "exists_not_found"));
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error verifying if file with key '{Key}' exists in R2", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "exists_error"));
            return false;
        }
    }

    public async Task<long?> GetObjectSizeAsync(string key)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            GetObjectMetadataRequest request = new()
            {
                BucketName = _bucketName,
                Key = key
            };

            var response = await _s3Client.GetObjectMetadataAsync(request);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_size"));
            return response.ContentLength;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_size_not_found"));
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting size for key '{Key}' from R2", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "get_size_error"));
            return null;
        }
    }

    public async Task<bool> MoveFileAsync(string sourceKey, string destinationKey)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            var copyRequest = new CopyObjectRequest
            {
                SourceBucket = _bucketName,
                SourceKey = sourceKey,
                DestinationBucket = _bucketName,
                DestinationKey = destinationKey
            };

            await _s3Client.CopyObjectAsync(copyRequest);

            var deleteRequest = new DeleteObjectRequest
            {
                BucketName = _bucketName,
                Key = sourceKey
            };
            await _s3Client.DeleteObjectAsync(deleteRequest);

            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "move"));
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move file from '{SourceKey}' to '{DestinationKey}' in R2", sourceKey, destinationKey);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "move_error"));
            return false;
        }
    }

    public async Task<string> InitiateMultipartUploadAsync(string key, string contentType)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new InitiateMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                ContentType = contentType
            };

            var response = await _s3Client.InitiateMultipartUploadAsync(request);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "initiate_multipart"));

            return response.UploadId;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error initiating multipart upload for key '{Key}'", key);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "initiate_multipart_error"));
            throw;
        }
    }

    public string GeneratePresignedPartUploadUrl(string key, string uploadId, int partNumber, TimeSpan expiration)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _bucketName,
                Key = key,
                Verb = HttpVerb.PUT,
                Expires = DateTime.UtcNow.Add(expiration),
                UploadId = uploadId,
                PartNumber = partNumber
            };

            string url = _s3Client.GetPreSignedURL(request);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "presign_part"));

            return url;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error generating presigned part upload URL for key '{Key}', part {PartNumber}", key, partNumber);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "presign_part_error"));
            throw;
        }
    }

    public async Task<bool> CompleteMultipartUploadAsync(string key, string uploadId, IEnumerable<PartETag> parts)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new CompleteMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                UploadId = uploadId,
                PartETags = System.Linq.Enumerable.ToList(parts)
            };

            await _s3Client.CompleteMultipartUploadAsync(request);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "complete_multipart"));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error completing multipart upload for key '{Key}', upload ID '{UploadId}'", key, uploadId);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "complete_multipart_error"));
            return false;
        }
    }

    public async Task<List<PartETag>> ListPartsAsync(string key, string uploadId)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            // Server-authoritative part list: R2 tells us exactly which parts it
            // received (and their ETags), so the browser never needs to read the
            // ETag header — no R2 CORS ExposeHeaders requirement, and clients
            // cannot spoof part ETags.
            var request = new ListPartsRequest
            {
                BucketName = _bucketName,
                Key = key,
                UploadId = uploadId,
                MaxParts = 1000
            };

            var response = await _s3Client.ListPartsAsync(request);

            if (response.IsTruncated == true)
            {
                // Our parts are ≥5MB and objects ≤500MB → ≤100 parts, so the single
                // 1000-part page always covers it. Warn loudly if that ever changes.
                _logger.LogWarning("ListParts truncated for key '{Key}' (>1000 parts); completion may be incomplete.", key);
            }

            var parts = response.Parts
                .Select(p => new PartETag(p.PartNumber, p.ETag))
                .OrderBy(p => p.PartNumber)
                .ToList();

            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "list_parts"));

            return parts;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error listing parts for key '{Key}', upload ID '{UploadId}'", key, uploadId);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("operation", "list_parts_error"));
            throw;
        }
    }

    public async Task<bool> AbortMultipartUploadAsync(string key, string uploadId)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        try
        {
            var request = new AbortMultipartUploadRequest
            {
                BucketName = _bucketName,
                Key = key,
                UploadId = uploadId
            };

            await _s3Client.AbortMultipartUploadAsync(request);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "abort_multipart"));

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error aborting multipart upload for key '{Key}', upload ID '{UploadId}'", key, uploadId);
            stopwatch.Stop();
            FileServiceDiagnostics.S3OperationDuration.Record(stopwatch.Elapsed.TotalMilliseconds, 
                new KeyValuePair<string, object?>("operation", "abort_multipart_error"));
            return false;
        }
    }
}
