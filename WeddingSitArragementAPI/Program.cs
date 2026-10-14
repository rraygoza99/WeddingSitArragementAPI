using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

const string BucketName = "wedding-arragement";

// Configure AWS S3 client with multiple credential sources
var awsConfig = builder.Configuration.GetSection("AWS");
var region = awsConfig["Region"] ?? "us-east-2";

// Try to get credentials from multiple sources (User Secrets, Environment Variables, or AWS Profile)
var accessKey = builder.Configuration["AWS:AccessKey"];
var secretKey = builder.Configuration["AWS:SecretKey"];

if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
{
    builder.Services.AddSingleton<IAmazonS3>(sp =>
        new AmazonS3Client(accessKey, secretKey, RegionEndpoint.GetBySystemName(region)));
}
else
{
    builder.Services.AddSingleton<IAmazonS3>(sp =>
        new AmazonS3Client(RegionEndpoint.GetBySystemName(region)));
}

builder.Services.AddAWSLambdaHosting(LambdaEventSource.HttpApi);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();

// ── Create a new wedding seating file ────────────────────────────────────────
app.MapPost("/api/weddings", async (CreateWeddingRequest req, HttpContext context, IAmazonS3 s3Client, [FromQuery] string? ownerEmail) =>
{
    var email = !string.IsNullOrWhiteSpace(req.OwnerEmail)
        ? req.OwnerEmail.Trim()
        : ExtractOwnerEmail(context, ownerEmail);

    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (in body 'OwnerEmail', 'X-OwnerEmail' header, or 'ownerEmail' query parameter).");

    if (string.IsNullOrWhiteSpace(req.Partner1Name) || string.IsNullOrWhiteSpace(req.Partner2Name))
        return Results.BadRequest("Both partner names are required.");

    var weddingId = Guid.NewGuid().ToString();
    var safeName = $"{Sanitize(req.Partner1Name)}-{Sanitize(req.Partner2Name)}-{req.WeddingDate:yyyy-MM-dd}";
    var fileName = $"{safeName}.json";

    var wedding = new
    {
        WeddingId = weddingId,
        Couple = new { Partner1 = req.Partner1Name, Partner2 = req.Partner2Name },
        WeddingDate = req.WeddingDate,
        Venue = req.Venue ?? string.Empty,
        OwnerEmail = email,
        Tables = Array.Empty<object>(),
        Guests = Array.Empty<object>(),
        CreatedAt = DateTime.UtcNow
    };

    var json = JsonSerializer.Serialize(wedding, new JsonSerializerOptions { WriteIndented = true });

    try
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
        var putRequest = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = fileName,
            InputStream = stream,
            ContentType = "application/json"
        };
        putRequest.Metadata["X-OwnerEmail"] = email;

        await s3Client.PutObjectAsync(putRequest);
        return Results.Created($"/api/s3/file/{fileName}", new { FileName = fileName, WeddingId = weddingId, OwnerEmail = email });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error creating wedding file: {ex.Message}");
    }
})
.WithName("CreateWedding")
.WithSummary("Create a new wedding seating arrangement file in S3 with owner metadata");

// ── Upload an existing JSON file ──────────────────────────────────────────────
app.MapPost("/api/s3/upload", async (HttpRequest request, IAmazonS3 s3Client, [FromQuery] string? ownerEmail) =>
{
    var email = ExtractOwnerEmailFromForm(request, ownerEmail);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (via 'X-OwnerEmail' header, form field 'ownerEmail', or 'ownerEmail' query parameter).");

    if (!request.HasFormContentType || request.Form.Files.Count == 0)
        return Results.BadRequest("No file uploaded.");

    var file = request.Form.Files[0];
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");
    if (!file.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only JSON files are allowed.");

    try
    {
        // If file already exists, verify caller is in owners list
        var existingOwners = await GetExistingOwnerEmailsAsync(s3Client, BucketName, file.FileName);
        if (existingOwners != null && existingOwners.Count > 0 && !IsEmailAuthorized(existingOwners, email))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "You do not have permission to overwrite this file.");
        }

        using var stream = file.OpenReadStream();
        var putRequest = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = file.FileName,
            InputStream = stream,
            ContentType = "application/json"
        };
        putRequest.Metadata["X-OwnerEmail"] = email;

        await s3Client.PutObjectAsync(putRequest);
        return Results.Ok(new { Message = $"Uploaded {file.FileName} to S3.", FileName = file.FileName, OwnerEmail = email });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error uploading file: {ex.Message}");
    }
})
.WithName("UploadFile")
.WithSummary("Upload an existing JSON file to S3 with owner metadata");

// ── Save or update wedding JSON content ───────────────────────────────────────
app.MapPut("/api/s3/file/{fileName}", async (string fileName, HttpRequest request, HttpContext context, IAmazonS3 s3Client, [FromQuery] string? ownerEmail) =>
{
    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only JSON files are allowed.");

    var email = ExtractOwnerEmail(context, ownerEmail);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (via 'X-OwnerEmail' header or 'ownerEmail' query parameter).");

    try
    {
        // If file exists, verify ownership
        var existingOwners = await GetExistingOwnerEmailsAsync(s3Client, BucketName, fileName);
        if (existingOwners != null && existingOwners.Count > 0 && !IsEmailAuthorized(existingOwners, email))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Access denied. You do not have permission to modify this file.");
        }

        using var memoryStream = new MemoryStream();
        await request.Body.CopyToAsync(memoryStream);
        if (memoryStream.Length == 0)
            return Results.BadRequest("Request body cannot be empty.");

        memoryStream.Position = 0;
        var putRequest = new PutObjectRequest
        {
            BucketName = BucketName,
            Key = fileName,
            InputStream = memoryStream,
            ContentType = "application/json"
        };
        putRequest.Metadata["X-OwnerEmail"] = email;

        await s3Client.PutObjectAsync(putRequest);
        return Results.Ok(new { Message = $"Saved {fileName} to S3.", FileName = fileName, OwnerEmail = email });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error saving file: {ex.Message}");
    }
})
.WithName("SaveFile")
.WithSummary("Save or update a wedding JSON file in S3 with owner validation");

// ── List wedding files filtered by owner ──────────────────────────────────────
app.MapGet("/api/s3/list", async (HttpContext context, IAmazonS3 s3Client, [FromQuery] string? ownerEmail) =>
{
    var email = ExtractOwnerEmail(context, ownerEmail);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (via 'X-OwnerEmail' header or 'ownerEmail' query parameter) to list files.");

    try
    {
        var listRequest = new ListObjectsV2Request { BucketName = BucketName };
        var response = await s3Client.ListObjectsV2Async(listRequest);

        var s3Objects = (response.S3Objects ?? new List<S3Object>())
            .Where(o => o.Key.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            .ToList();

        // Retrieve metadata & tags for objects to check X-OwnerEmail
        var metadataTasks = s3Objects.Select(async o =>
        {
            try
            {
                var owners = await GetExistingOwnerEmailsAsync(s3Client, BucketName, o.Key) ?? new List<string>();
                return new
                {
                    Object = o,
                    OwnerEmails = owners,
                    Matches = IsEmailAuthorized(owners, email)
                };
            }
            catch
            {
                return new { Object = o, OwnerEmails = new List<string>(), Matches = false };
            }
        });

        var results = await Task.WhenAll(metadataTasks);

        var jsonFiles = results
            .Where(r => r.Matches)
            .Select(r => new
            {
                FileName = r.Object.Key,
                Size = r.Object.Size,
                LastModified = r.Object.LastModified,
                OwnerEmails = r.OwnerEmails
            })
            .ToList();

        return Results.Ok(jsonFiles);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error listing files: {ex.Message}");
    }
})
.WithName("ListFiles")
.WithSummary("List wedding JSON files in S3 filtered by owner email");

// ── Get / Download a wedding file ────────────────────────────────────────────
async Task<IResult> GetWeddingFile(string fileName, HttpContext context, IAmazonS3 s3Client, [FromQuery] string? ownerEmail)
{
    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only JSON files are allowed.");

    var email = ExtractOwnerEmail(context, ownerEmail);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (via 'X-OwnerEmail' header or 'ownerEmail' query parameter).");

    try
    {
        var getRequest = new GetObjectRequest { BucketName = BucketName, Key = fileName };
        var response = await s3Client.GetObjectAsync(getRequest);

        var owners = GetOwnerEmailsFromMetadata(response.Metadata);
        if (owners.Count == 0)
        {
            try
            {
                var tagResponse = await s3Client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = BucketName,
                    Key = fileName
                });
                owners = GetOwnerEmailsFromTags(tagResponse.Tagging);
            }
            catch { }
        }

        if (owners.Count > 0 && !IsEmailAuthorized(owners, email))
        {
            response.Dispose();
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Access denied. You are not authorized to access this file.");
        }

        return Results.File(response.ResponseStream, "application/json", fileName);
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound($"File '{fileName}' was not found.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error downloading file: {ex.Message}");
    }
}

app.MapGet("/api/s3/file/{fileName}", GetWeddingFile)
    .WithName("GetFile")
    .WithSummary("Get a wedding JSON file from S3 (owner restricted)");

app.MapGet("/api/s3/download/{fileName}", GetWeddingFile)
    .WithName("DownloadFile")
    .WithSummary("Download a wedding JSON file from S3 (owner restricted)");

// ── Delete a wedding file ─────────────────────────────────────────────────────
app.MapDelete("/api/s3/delete/{fileName}", async (string fileName, HttpContext context, IAmazonS3 s3Client, [FromQuery] string? ownerEmail) =>
{
    if (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        return Results.BadRequest("Only JSON files are allowed.");

    var email = ExtractOwnerEmail(context, ownerEmail);
    if (string.IsNullOrWhiteSpace(email))
        return Results.BadRequest("Owner email is required (via 'X-OwnerEmail' header or 'ownerEmail' query parameter).");

    try
    {
        // Check ownership before deleting
        var owners = await GetExistingOwnerEmailsAsync(s3Client, BucketName, fileName);
        if (owners != null && owners.Count > 0 && !IsEmailAuthorized(owners, email))
        {
            return Results.Problem(
                statusCode: StatusCodes.Status403Forbidden,
                title: "Forbidden",
                detail: "Access denied. You are not authorized to delete this file.");
        }

        var deleteRequest = new DeleteObjectRequest { BucketName = BucketName, Key = fileName };
        await s3Client.DeleteObjectAsync(deleteRequest);
        return Results.Ok($"Deleted {fileName} from S3.");
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return Results.NotFound($"File '{fileName}' was not found.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error deleting file: {ex.Message}");
    }
})
.WithName("DeleteFile")
.WithSummary("Delete a wedding JSON file from S3 (owner restricted)");

app.Run();

// ── Helpers ───────────────────────────────────────────────────────────────────
static string Sanitize(string name) =>
    string.Concat(name.Where(c => char.IsLetterOrDigit(c) || c == '-')).Trim('-');

static async Task<List<string>?> GetExistingOwnerEmailsAsync(IAmazonS3 s3Client, string bucket, string key)
{
    try
    {
        var meta = await s3Client.GetObjectMetadataAsync(bucket, key);
        var owners = GetOwnerEmailsFromMetadata(meta.Metadata);

        if (owners.Count == 0)
        {
            try
            {
                var tagResponse = await s3Client.GetObjectTaggingAsync(new GetObjectTaggingRequest
                {
                    BucketName = bucket,
                    Key = key
                });
                owners = GetOwnerEmailsFromTags(tagResponse.Tagging);
            }
            catch { }
        }

        return owners;
    }
    catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
    {
        return null; // Object does not exist
    }
}

static List<string> ParseEmailList(string? rawValue)
{
    if (string.IsNullOrWhiteSpace(rawValue))
        return new List<string>();

    return rawValue
        .Split(new[] { ',', ';', '|', ' ' }, StringSplitOptions.RemoveEmptyEntries)
        .Select(e => e.Trim())
        .Where(e => !string.IsNullOrEmpty(e))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToList();
}

static List<string> GetOwnerEmailsFromMetadata(MetadataCollection metadata)
{
    if (metadata == null) return new List<string>();

    var candidates = new[] { "x-owneremail", "X-OwnerEmail", "x-owner-email", "owneremail", "owner-email" };
    foreach (var key in candidates)
    {
        if (metadata[key] is { Length: > 0 } val)
            return ParseEmailList(val);
    }

    foreach (var key in metadata.Keys)
    {
        if (key.Equals("x-owneremail", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("x-owner-email", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("owneremail", StringComparison.OrdinalIgnoreCase) ||
            key.Equals("owner-email", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEmailList(metadata[key]);
        }
    }
    return new List<string>();
}

static List<string> GetOwnerEmailsFromTags(List<Tag>? tags)
{
    if (tags == null) return new List<string>();

    foreach (var tag in tags)
    {
        if (tag.Key.Equals("X-OwnerEmail", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Equals("OwnerEmail", StringComparison.OrdinalIgnoreCase) ||
            tag.Key.Equals("owner-email", StringComparison.OrdinalIgnoreCase))
        {
            return ParseEmailList(tag.Value);
        }
    }
    return new List<string>();
}

static bool IsEmailAuthorized(IEnumerable<string> authorizedEmails, string requesterEmail)
{
    if (string.IsNullOrWhiteSpace(requesterEmail)) return false;
    return authorizedEmails.Any(e => string.Equals(e, requesterEmail.Trim(), StringComparison.OrdinalIgnoreCase));
}

static string? ExtractOwnerEmail(HttpContext context, string? queryOwnerEmail = null)
{
    if (!string.IsNullOrWhiteSpace(queryOwnerEmail))
        return queryOwnerEmail.Trim();

    if (context.Request.Headers.TryGetValue("X-OwnerEmail", out var headerVal) && !string.IsNullOrWhiteSpace(headerVal))
        return headerVal.ToString().Trim();

    if (context.Request.Query.TryGetValue("ownerEmail", out var qVal) && !string.IsNullOrWhiteSpace(qVal))
        return qVal.ToString().Trim();

    return null;
}

static string? ExtractOwnerEmailFromForm(HttpRequest request, string? queryOwnerEmail = null)
{
    var email = ExtractOwnerEmail(request.HttpContext, queryOwnerEmail);
    if (!string.IsNullOrWhiteSpace(email))
        return email;

    if (request.HasFormContentType && request.Form.TryGetValue("ownerEmail", out var formVal) && !string.IsNullOrWhiteSpace(formVal))
        return formVal.ToString().Trim();

    if (request.HasFormContentType && request.Form.TryGetValue("X-OwnerEmail", out var formHeaderVal) && !string.IsNullOrWhiteSpace(formHeaderVal))
        return formHeaderVal.ToString().Trim();

    return null;
}

// ── Request / Response models ─────────────────────────────────────────────────
record CreateWeddingRequest(
    string Partner1Name,
    string Partner2Name,
    DateOnly WeddingDate,
    string? Venue,
    string? OwnerEmail
);
