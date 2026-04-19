using Minio.ApiEndpoints;

namespace BackendServer.Api;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Minio;
using Minio.DataModel.Args;

public static class Endpoints
{
    public static void AddEndpoints(this WebApplication app)
    {
        app.MapGet("/health", () => "Service healthy!");

        var picturesGroup = app.MapGroup("/api/pictures");

        picturesGroup.MapGet("/", async ([FromServices] IMinioClient minioClient, [FromQuery] string? filter, [FromQuery] string? label) =>
        {
            try
            {
                var bucketName = "pictures";
                var found = await minioClient.BucketExistsAsync(new BucketExistsArgs().WithBucket(bucketName));
                if (!found) return Results.Ok(new string[] { });

                var listArgs = new ListObjectsArgs().WithBucket(bucketName).WithRecursive(true);
                
                var pictures = new List<string>();
                using var cts = new CancellationTokenSource();

                await foreach (var item in minioClient.ListObjectsEnumAsync(listArgs, cts.Token).ConfigureAwait(false))
                {
                    bool matchesFilter = string.IsNullOrEmpty(filter) || item.Key.Contains(filter, StringComparison.OrdinalIgnoreCase);
                    bool matchesLabel = string.IsNullOrEmpty(label) || item.Key.Contains(label, StringComparison.OrdinalIgnoreCase);
                    
                    if (matchesFilter && matchesLabel && !item.IsDir)
                    {
                        pictures.Add(item.Key);
                    }
                }

                return Results.Ok(pictures);
            }
            catch (Exception ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        picturesGroup.MapGet("/{**objectName}", async ([FromServices] IMinioClient minioClient, string objectName) =>
        {
            try
            {
                var bucketName = "pictures";
                var memoryStream = new MemoryStream();
                var getObjectArgs = new GetObjectArgs()
                    .WithBucket(bucketName)
                    .WithObject(objectName)
                    .WithCallbackStream((stream) => stream.CopyTo(memoryStream));
                
                await minioClient.GetObjectAsync(getObjectArgs);
                memoryStream.Position = 0;
                
                return Results.File(memoryStream, "application/octet-stream", objectName);
            }
            catch (Exception ex)
            {
                return Results.NotFound(ex.Message);
            }
        });
    }
}