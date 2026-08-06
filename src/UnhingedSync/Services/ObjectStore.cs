using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using UnhingedSync.Models;

namespace UnhingedSync.Services;

public sealed record RemoteObject(string Key, long Size, DateTimeOffset? LastModified);

/// <summary>
/// The published builds live in an S3-compatible bucket, by default Cloudflare R2.
///
/// This replaced Syncthing. The record layout was already append-only with uniquely named
/// files, because that is what made it safe for several machines to publish into one
/// replicated folder, and that maps onto object keys unchanged. What it buys: no machine has
/// to be online for anyone else to fetch a build, there is no pairing, no folder IDs to keep
/// byte-identical, and an artist no longer holds every build on disk. R2 specifically because
/// egress is unmetered, and distribution is the entire workload here.
/// </summary>
public sealed class ObjectStore : IDisposable
{
    private readonly StorageConfig _config;
    private readonly AmazonS3Client _client;

    public ObjectStore(StorageConfig config)
    {
        _config = config;

        var s3Config = new AmazonS3Config
        {
            ServiceURL = config.ResolvedEndpoint,

            // R2 does not do virtual-host style buckets on its S3 endpoint.
            ForcePathStyle = true,

            // R2 ignores regions but the SDK insists on one for signing.
            AuthenticationRegion = "auto",

            // SDK v4 sends CRC32 integrity headers by default, which S3-compatible services
            // other than S3 itself reject or mishandle. Only send them where required.
            RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
            ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED
        };

        _client = new AmazonS3Client(
            new BasicAWSCredentials(config.AccessKeyId, config.SecretAccessKey), s3Config);
    }

    private string Key(string relative) =>
        string.IsNullOrWhiteSpace(_config.Prefix)
            ? relative
            : $"{_config.Prefix.Trim('/')}/{relative}";

    private string Unprefixed(string key) =>
        string.IsNullOrWhiteSpace(_config.Prefix)
            ? key
            : key[Math.Min(key.Length, _config.Prefix.Trim('/').Length + 1)..];

    /// <summary>Everything under a relative prefix, following continuation tokens.</summary>
    public async Task<List<RemoteObject>> ListAsync(string relativePrefix, CancellationToken ct = default)
    {
        var results = new List<RemoteObject>();
        var request = new ListObjectsV2Request
        {
            BucketName = _config.Bucket,
            Prefix = Key(relativePrefix)
        };

        do
        {
            var response = await _client.ListObjectsV2Async(request, ct);
            foreach (var o in response.S3Objects ?? [])
            {
                results.Add(new RemoteObject(
                    Unprefixed(o.Key),
                    o.Size ?? 0,
                    o.LastModified is { } m ? new DateTimeOffset(m.ToUniversalTime(), TimeSpan.Zero) : null));
            }
            request.ContinuationToken = response.NextContinuationToken;
        }
        while (request.ContinuationToken is not null);

        return results;
    }

    public async Task<string?> GetTextAsync(string relativeKey, CancellationToken ct = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_config.Bucket, Key(relativeKey), ct);
            using var reader = new StreamReader(response.ResponseStream);
            return await reader.ReadToEndAsync(ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task PutTextAsync(string relativeKey, string content, CancellationToken ct = default)
    {
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = Key(relativeKey),
            ContentBody = content,
            ContentType = relativeKey.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                ? "application/json"
                : "text/plain",

            // Required for R2, on every write. Signing the payload makes the SDK stream it
            // with aws-chunked framing, which R2 does not implement and rejects with a bare
            // 501 that looks like a permissions problem. Unsigned is not insecure here: the
            // request is still SigV4-signed over its headers, and the body is inside TLS.
            DisablePayloadSigning = true
        }, ct);
    }

    public async Task PutFileAsync(
        string relativeKey, string localPath, IProgress<string>? log = null, CancellationToken ct = default)
    {
        var size = new FileInfo(localPath).Length;
        log?.Report($"Uploading {Path.GetFileName(localPath)} ({size / 1024.0 / 1024.0:0.#} MB)…");

        await using var stream = File.OpenRead(localPath);
        await _client.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _config.Bucket,
            Key = Key(relativeKey),
            InputStream = stream,
            ContentType = Path.GetExtension(localPath).ToLowerInvariant() switch
            {
                ".zip" => "application/zip",
                ".json" => "application/json",
                _ => "text/plain"
            },

            // Required for R2 on every write; see PutTextAsync.
            DisablePayloadSigning = true
        }, ct);
    }

    /// <summary>
    /// Downloads to a local path, verifying SHA256 when one is known.
    ///
    /// The hash was always recorded and never checkable: over a replicated folder the only
    /// signal available was file size, which is why a half-arrived build and a deleted one
    /// were hard to tell apart. An explicit download can simply verify, and a mismatch
    /// deletes the file rather than leaving something plausible on disk.
    /// </summary>
    public async Task DownloadAsync(
        string relativeKey, string localPath, string? expectedSha256 = null,
        IProgress<string>? log = null, CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
        var partial = localPath + ".part";

        try
        {
            using (var response = await _client.GetObjectAsync(_config.Bucket, Key(relativeKey), ct))
            {
                var total = response.ContentLength;
                log?.Report($"Downloading {relativeKey} ({total / 1024.0 / 1024.0:0.#} MB)…");

                await using var source = response.ResponseStream;
                await using var destination = File.Create(partial);
                await source.CopyToAsync(destination, ct);
            }

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                var actual = await Sha256Async(partial, ct);
                if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(partial);
                    throw new InvalidOperationException(
                        $"{relativeKey} downloaded but its contents do not match the published " +
                        $"checksum (expected {expectedSha256}, got {actual}). It was discarded.");
                }
            }

            // Only ever appears at its real name once complete and verified, so an
            // interrupted download cannot be mistaken for a usable build.
            File.Move(partial, localPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(partial)) File.Delete(partial); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public async Task DeleteAsync(string relativeKey, CancellationToken ct = default)
    {
        try
        {
            await _client.DeleteObjectAsync(_config.Bucket, Key(relativeKey), ct);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            // Already gone is the desired state.
        }
    }

    public async Task<RemoteObject?> HeadAsync(string relativeKey, CancellationToken ct = default)
    {
        try
        {
            var meta = await _client.GetObjectMetadataAsync(_config.Bucket, Key(relativeKey), ct);
            return new RemoteObject(
                relativeKey,
                meta.ContentLength,
                meta.LastModified is { } m ? new DateTimeOffset(m.ToUniversalTime(), TimeSpan.Zero) : null);
        }
        catch (AmazonS3Exception e) when (e.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    /// <summary>
    /// Confirms the credentials actually work, distinguishing the ways they usually do not.
    /// Read-only by default, so it is safe to run on any machine.
    /// </summary>
    public async Task<string?> CheckAsync(bool includeWrite, CancellationToken ct = default)
    {
        try
        {
            await ListAsync("records/", ct);
        }
        catch (AmazonS3Exception e)
        {
            return e.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden =>
                    "The keys were rejected. Check accessKeyId and secretAccessKey, and that the " +
                    $"token is allowed to reach the bucket '{_config.Bucket}'.",
                System.Net.HttpStatusCode.NotFound =>
                    $"No bucket called '{_config.Bucket}' at {_config.ResolvedEndpoint}. Check the " +
                    "bucket name and the account ID.",
                _ => $"{(int)e.StatusCode} {e.StatusCode}: {e.Message}"
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return $"Could not reach {_config.ResolvedEndpoint}: {e.Message}";
        }

        if (!includeWrite) return null;

        // A round trip, because a read-only token lists happily and then fails at publish
        // time, which is a miserable moment to discover it.
        var probe = $"checks/{Environment.MachineName}-{Guid.NewGuid():N}.txt";
        try
        {
            await PutTextAsync(probe, "unhinged sync write check", ct);
            await DeleteAsync(probe, ct);
        }
        catch (AmazonS3Exception e)
        {
            // Named separately, because assuming "write failed" means "not allowed to write"
            // sent us both looking at the token when the request itself was at fault.
            return e.StatusCode switch
            {
                System.Net.HttpStatusCode.Forbidden =>
                    "Reading works but writing is refused. The token needs Object Read and " +
                    "Write, not Object Read only.",
                System.Net.HttpStatusCode.NotImplemented =>
                    "Reading works but the store rejected the upload as unsupported (501). " +
                    "That is a client bug rather than a problem with your token or bucket. " +
                    $"Detail: {e.Message}",
                _ => $"Reading works but writing failed with {(int)e.StatusCode} {e.StatusCode}: {e.Message}"
            };
        }

        return null;
    }

    public void Dispose() => _client.Dispose();
}
