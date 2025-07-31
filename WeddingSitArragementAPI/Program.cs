using Amazon;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Linq;

var builder = WebApplication.CreateBuilder(args);

// Configure AWS S3 client with multiple credential sources
var awsConfig = builder.Configuration.GetSection("AWS");
var region = awsConfig["Region"] ?? "us-east-2";

// Try to get credentials from multiple sources (User Secrets, Environment Variables, or AWS Profile)
var accessKey = builder.Configuration["AWS:AccessKey"];
var secretKey = builder.Configuration["AWS:SecretKey"];

if (!string.IsNullOrEmpty(accessKey) && !string.IsNullOrEmpty(secretKey))
{
    // Use explicit credentials from User Secrets or Environment Variables
    builder.Services.AddSingleton<IAmazonS3>(sp =>
        new AmazonS3Client(accessKey, secretKey, RegionEndpoint.GetBySystemName(region)));
}
else
{
    // Fallback to default AWS credential chain (AWS Profile, IAM Role, etc.)
    builder.Services.AddSingleton<IAmazonS3>(sp =>
        new AmazonS3Client(RegionEndpoint.GetBySystemName(region)));
}

var app = builder.Build();

// Endpoint to upload a JSON file to S3
app.MapPost("/api/s3/upload", async (HttpRequest request, IAmazonS3 s3Client) =>
{
    var bucketName = "wedding-arragement";
    
    if (request.Form.Files.Count == 0)
        return Results.BadRequest("No file uploaded.");
        
    var file = request.Form.Files[0];
    if (file == null || file.Length == 0)
        return Results.BadRequest("No file uploaded.");
    if (!file.FileName.EndsWith(".json"))
        return Results.BadRequest("Only JSON files are allowed.");

    try
    {
        using var stream = file.OpenReadStream();
        var putRequest = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = file.FileName,
            InputStream = stream,
            ContentType = "application/json"
        };
        await s3Client.PutObjectAsync(putRequest);
        return Results.Ok($"Uploaded {file.FileName} to S3.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error uploading file: {ex.Message}");
    }
});

// Endpoint to list JSON files in S3
app.MapGet("/api/s3/list", async (IAmazonS3 s3Client) =>
{
    var bucketName = "wedding-arragement";
    
    try
    {
        var listRequest = new ListObjectsV2Request
        {
            BucketName = bucketName
        };
        var response = await s3Client.ListObjectsV2Async(listRequest);
        
        // Handle case when S3Objects is null (empty bucket)
        var s3Objects = response.S3Objects ?? new List<S3Object>();
        
        var jsonFiles = s3Objects
            .Where(o => o.Key.EndsWith(".json"))
            .Select(o => new { 
                FileName = o.Key, 
                Size = o.Size, 
                LastModified = o.LastModified 
            })
            .ToList();
        return Results.Ok(jsonFiles);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error listing files: {ex.Message}");
    }
});

// Endpoint to download a JSON file from S3
app.MapGet("/api/s3/download/{fileName}", async (string fileName, IAmazonS3 s3Client) =>
{
    var bucketName = "wedding-arragement";
    
    if (!fileName.EndsWith(".json"))
        return Results.BadRequest("Only JSON files are allowed.");
    
    try
    {
        var getRequest = new GetObjectRequest
        {
            BucketName = bucketName,
            Key = fileName
        };
        
        var response = await s3Client.GetObjectAsync(getRequest);
        return Results.File(response.ResponseStream, "application/json", fileName);
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error downloading file: {ex.Message}");
    }
});

// Endpoint to delete a JSON file from S3
app.MapDelete("/api/s3/delete/{fileName}", async (string fileName, IAmazonS3 s3Client) =>
{
    var bucketName = "wedding-arragement";
    
    if (!fileName.EndsWith(".json"))
        return Results.BadRequest("Only JSON files are allowed.");
    
    try
    {
        var deleteRequest = new DeleteObjectRequest
        {
            BucketName = bucketName,
            Key = fileName
        };
        
        await s3Client.DeleteObjectAsync(deleteRequest);
        return Results.Ok($"Deleted {fileName} from S3.");
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error deleting file: {ex.Message}");
    }
});

app.Run();
