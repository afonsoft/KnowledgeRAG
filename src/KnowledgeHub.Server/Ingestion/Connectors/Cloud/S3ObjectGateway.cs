using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace KnowledgeHub.Server.Ingestion.Connectors.Cloud;

/// <summary>S3-compatible gateway over the AWS SDK — used for real AWS S3
/// (<see cref="AwsS3Connector"/>) and OCI Object Storage via its S3-compatible
/// endpoint (<see cref="OciStorageConnector"/>).</summary>
internal sealed class S3ObjectGateway(IAmazonS3 client, string bucket) : IRemoteObjectGateway
{
    public async IAsyncEnumerable<RemoteObject> ListAsync(
        string? prefix, [EnumeratorCancellation] CancellationToken ct)
    {
        string? continuationToken = null;
        do
        {
            var response = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = string.IsNullOrEmpty(prefix) ? null : prefix,
                ContinuationToken = continuationToken
            }, ct);
            // skip '/'-suffixed keys — folder placeholders, not documents
            foreach (var obj in (response.S3Objects ?? []).Where(o => !o.Key.EndsWith('/')))
            {
                yield return new RemoteObject(
                    obj.Key, obj.ETag ?? "", new DateTimeOffset(obj.LastModified ?? DateTime.UnixEpoch), obj.Size ?? 0);
            }
            continuationToken = response.NextContinuationToken;
        }
        while (continuationToken is not null);
    }

    public async Task<Stream> OpenReadAsync(string key, CancellationToken ct)
    {
        var response = await client.GetObjectAsync(new GetObjectRequest
        {
            BucketName = bucket,
            Key = key
        }, ct);
        return response.ResponseStream;
    }

    public ValueTask DisposeAsync()
    {
        client.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Real AWS S3 client bound to a region endpoint.</summary>
    public static S3ObjectGateway ForAws(string bucket, string region, string accessKeyId, string secretAccessKey)
    {
        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        return new S3ObjectGateway(
            new AmazonS3Client(credentials, Amazon.RegionEndpoint.GetBySystemName(region)), bucket);
    }

    /// <summary>OCI Object Storage via the S3-compatible endpoint
    /// (<c>{namespace}.compat.objectstorage.{region}.oraclecloud.com</c>).</summary>
    public static S3ObjectGateway ForOci(string bucket, string ns, string region, string accessKeyId, string secretAccessKey)
    {
        var credentials = new BasicAWSCredentials(accessKeyId, secretAccessKey);
        var config = new AmazonS3Config
        {
            ServiceURL = $"https://{ns}.compat.objectstorage.{region}.oraclecloud.com",
            ForcePathStyle = true
        };
        return new S3ObjectGateway(new AmazonS3Client(credentials, config), bucket);
    }
}
