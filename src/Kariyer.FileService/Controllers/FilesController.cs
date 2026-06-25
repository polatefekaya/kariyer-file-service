using System;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Kariyer.FileService.Domain;
using Kariyer.FileService.Features.ConfirmUpload;
using Kariyer.FileService.Features.DeleteFile;
using Kariyer.FileService.Features.GetDownloadUrl;
using Kariyer.FileService.Features.GetFileDetails;
using Kariyer.FileService.Features.ListFiles;
using Kariyer.FileService.Features.OverwriteFileContent;
using Kariyer.FileService.Features.PresignedUpload;
using Kariyer.FileService.Features.UpdateFileDetails;
using Kariyer.FileService.Features.MultipartUpload;

namespace Kariyer.FileService.Controllers;

[Authorize]
[ApiController]
[Route("api/files")]
[Microsoft.AspNetCore.RateLimiting.EnableRateLimiting("UploadPolicy")]
public class FilesController : ControllerBase
{
    private readonly PresignedUploadHandler _presignedUploadHandler;
    private readonly ConfirmUploadHandler _confirmUploadHandler;
    private readonly GetDownloadUrlHandler _getDownloadUrlHandler;
    private readonly DeleteFileHandler _deleteFileHandler;
    private readonly ListFilesHandler _listFilesHandler;
    private readonly GetFileDetailsHandler _getFileDetailsHandler;
    private readonly UpdateFileDetailsHandler _updateFileDetailsHandler;
    private readonly OverwriteFileContentHandler _overwriteFileContentHandler;
    private readonly MultipartInitiateHandler _multipartInitiateHandler;
    private readonly MultipartPresignPartsHandler _multipartPresignPartsHandler;
    private readonly MultipartCompleteHandler _multipartCompleteHandler;
    private readonly MultipartAbortHandler _multipartAbortHandler;

    public FilesController(
        PresignedUploadHandler presignedUploadHandler,
        ConfirmUploadHandler confirmUploadHandler,
        GetDownloadUrlHandler getDownloadUrlHandler,
        DeleteFileHandler deleteFileHandler,
        ListFilesHandler listFilesHandler,
        GetFileDetailsHandler getFileDetailsHandler,
        UpdateFileDetailsHandler updateFileDetailsHandler,
        OverwriteFileContentHandler overwriteFileContentHandler,
        MultipartInitiateHandler multipartInitiateHandler,
        MultipartPresignPartsHandler multipartPresignPartsHandler,
        MultipartCompleteHandler multipartCompleteHandler,
        MultipartAbortHandler multipartAbortHandler)
    {
        _presignedUploadHandler = presignedUploadHandler;
        _confirmUploadHandler = confirmUploadHandler;
        _getDownloadUrlHandler = getDownloadUrlHandler;
        _deleteFileHandler = deleteFileHandler;
        _listFilesHandler = listFilesHandler;
        _getFileDetailsHandler = getFileDetailsHandler;
        _updateFileDetailsHandler = updateFileDetailsHandler;
        _overwriteFileContentHandler = overwriteFileContentHandler;
        _multipartInitiateHandler = multipartInitiateHandler;
        _multipartPresignPartsHandler = multipartPresignPartsHandler;
        _multipartCompleteHandler = multipartCompleteHandler;
        _multipartAbortHandler = multipartAbortHandler;
    }

    [HttpPost("presigned-upload")]
    [ProducesResponseType(typeof(PresignedUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> GetPresignedUploadUrl([FromBody] PresignedUploadRequest request)
    {
        string? userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized("User identity not found in token.");
        }

        PresignedUploadResponse? result = await _presignedUploadHandler.HandleAsync(request, userId);
        if (result == null)
        {
            return BadRequest("Invalid upload request parameters (name, format, or size).");
        }

        return Ok(result);
    }

    [HttpPost("confirm-upload")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ConfirmUpload([FromBody] ConfirmUploadRequest request)
    {
        bool? result = await _confirmUploadHandler.HandleAsync(request);
        if (result == null)
        {
            return NotFound("File record not found.");
        }
        if (result == false)
        {
            return BadRequest("File has not been uploaded to storage yet.");
        }

        return Ok();
    }

    [HttpGet("download-url")]
    [ProducesResponseType(typeof(FileUrlResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDownloadUrl([FromQuery] string key)
    {
        string? userId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            FileUrlResponse? result = await _getDownloadUrlHandler.HandleAsync(key, userId, userRole);
            if (result == null)
            {
                return NotFound("File not found.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpDelete]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteFile([FromQuery] string key)
    {
        string? userId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            bool? result = await _deleteFileHandler.HandleAsync(key, userId, userRole);
            if (result == null)
            {
                return NotFound("File not found.");
            }
            if (result == false)
            {
                return StatusCode(500, "Could not delete resource from storage.");
            }
            return Ok("File deleted successfully.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpGet]
    [ProducesResponseType(typeof(PagedResult<StoredFile>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListFiles(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? userId = null,
        [FromQuery] string? status = null,
        [FromQuery] bool? isPublic = null)
    {
        string? currentUserId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        PagedResult<StoredFile> result = await _listFilesHandler.HandleAsync(page, pageSize, userId, status, isPublic, currentUserId, userRole);
        return Ok(result);
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(StoredFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFileDetails(string id)
    {
        string? currentUserId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        try
        {
            StoredFile? result = await _getFileDetailsHandler.HandleAsync(id, currentUserId, userRole);
            if (result == null)
            {
                return NotFound("File details not found.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(StoredFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateFileDetails(string id, [FromBody] UpdateFileRequest request)
    {
        string? currentUserId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        try
        {
            StoredFile? result = await _updateFileDetailsHandler.HandleAsync(id, request, currentUserId, userRole);
            if (result == null)
            {
                return NotFound("File not found.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPost("{id}/presigned-upload")]
    [ProducesResponseType(typeof(PresignedUploadResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> OverwriteFileContent(string id)
    {
        string? currentUserId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(currentUserId))
        {
            return Unauthorized();
        }

        try
        {
            PresignedUploadResponse? result = await _overwriteFileContentHandler.HandleAsync(id, currentUserId, userRole);
            if (result == null)
            {
                return NotFound("File not found.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPost("multipart/initiate")]
    [ProducesResponseType(typeof(MultipartInitiateResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> InitiateMultipartUpload([FromBody] MultipartInitiateRequest request)
    {
        string? userId = User.FindFirst("sub")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized("User identity not found in token.");
        }

        MultipartInitiateResponse? result = await _multipartInitiateHandler.HandleAsync(request, userId);
        if (result == null)
        {
            return BadRequest("Invalid request parameters (name, format, or size).");
        }

        return Ok(result);
    }

    [HttpPost("multipart/presign-parts")]
    [ProducesResponseType(typeof(MultipartPresignPartsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PresignParts([FromBody] MultipartPresignPartsRequest request)
    {
        string? userId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            MultipartPresignPartsResponse? result = await _multipartPresignPartsHandler.HandleAsync(request, userId, userRole);
            if (result == null)
            {
                return BadRequest("Invalid storage key, upload ID, or part numbers.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPost("multipart/complete")]
    [ProducesResponseType(typeof(StoredFile), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> CompleteMultipart([FromBody] MultipartCompleteRequest request)
    {
        string? userId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            StoredFile? result = await _multipartCompleteHandler.HandleAsync(request, userId, userRole);
            if (result == null)
            {
                return BadRequest("Could not complete upload session on storage server.");
            }
            return Ok(result);
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }

    [HttpPost("multipart/abort")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AbortMultipart([FromBody] MultipartAbortRequest request)
    {
        string? userId = User.FindFirst("sub")?.Value;
        string? userRole = User.FindFirst("role")?.Value;
        if (string.IsNullOrWhiteSpace(userId))
        {
            return Unauthorized();
        }

        try
        {
            bool? result = await _multipartAbortHandler.HandleAsync(request, userId, userRole);
            if (result == null)
            {
                return BadRequest("Invalid key, upload ID, or not found.");
            }
            if (result == false)
            {
                return StatusCode(500, "Could not abort upload session on storage server.");
            }
            return Ok("Upload aborted successfully.");
        }
        catch (UnauthorizedAccessException ex)
        {
            return Forbid(ex.Message);
        }
    }
}
