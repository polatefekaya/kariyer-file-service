using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kariyer.FileService.Infrastructure.Telemetry;

public static class FileServiceDiagnostics
{
    public const string ServiceName = "Kariyer.FileService";

    public static readonly ActivitySource ActivitySource = new(ServiceName);
    public static readonly Meter Meter = new(ServiceName);

    // ── File Operations ───────────────────────────────────────────────────────

    public static readonly Counter<int> PresignedUploadRequestedCounter = Meter.CreateCounter<int>(
        name: "storage.upload.presigned.requested.count",
        unit: "{attempts}",
        description: "Number of presigned upload requests, tagged by outcome (success/failure) and public/private.");

    public static readonly Counter<int> FileConfirmedCounter = Meter.CreateCounter<int>(
        name: "storage.file.confirmed.count",
        unit: "{files}",
        description: "Number of files confirmed and activated, tagged by outcome.");

    public static readonly Counter<int> FileDeletedCounter = Meter.CreateCounter<int>(
        name: "storage.file.deleted.count",
        unit: "{files}",
        description: "Number of files deleted from R2 and database, tagged by outcome.");

    public static readonly Counter<int> DownloadUrlGeneratedCounter = Meter.CreateCounter<int>(
        name: "storage.download.url.generated.count",
        unit: "{urls}",
        description: "Number of presigned download URLs generated, tagged by source (cache_hit/cache_miss).");

    public static readonly Counter<int> FileDetailsRequestedCounter = Meter.CreateCounter<int>(
        name: "storage.file.details.requested.count",
        unit: "{attempts}",
        description: "Number of file details requests, tagged by outcome.");

    public static readonly Counter<int> FileDetailsUpdatedCounter = Meter.CreateCounter<int>(
        name: "storage.file.details.updated.count",
        unit: "{attempts}",
        description: "Number of file details updates, tagged by outcome.");

    public static readonly Counter<int> FileListedCounter = Meter.CreateCounter<int>(
        name: "storage.file.list.count",
        unit: "{attempts}",
        description: "Number of file list requests, tagged by outcome.");

    public static readonly Histogram<double> S3OperationDuration = Meter.CreateHistogram<double>(
        name: "storage.s3.operation.duration",
        unit: "ms",
        description: "Duration of S3/R2 operations in milliseconds, tagged by operation type (get_presigned_put/get_presigned_get/delete/exists).");

    // ── Caching ───────────────────────────────────────────────────────────────

    public static readonly Counter<int> CacheOperationsCounter = Meter.CreateCounter<int>(
        name: "storage.cache.operations.count",
        unit: "{operations}",
        description: "Garnet cache operations, tagged by operation (get/set/invalidate) and outcome (hit/miss/success/failure).");

    // ── Authentication ────────────────────────────────────────────────────────

    public static readonly Counter<int> AuthAttemptsCounter = Meter.CreateCounter<int>(
        name: "storage.auth.validation.count",
        unit: "{attempts}",
        description: "JWT authentication validation attempts, tagged by outcome (success/failure).");
}

