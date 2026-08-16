using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;

namespace Bizigo.Storage.Raw;

public sealed record RawObjectInfo(string Key, long ByteSize);

/// <summary>
/// Nesne deposu — <b>yalnızca S3 API</b> (F1 §7.0 koruma #1).
///
/// <para>
/// RustFS'e özel tek bir çağrı bile yok. Kaçış planının (SeaweedFS → kurumun
/// mevcut S3'ü → Garage) maliyetini bir yapılandırma satırına indiren şey bu
/// kısıt; ihlal edilirse RustFS 1.0-beta riski geri gelir.
/// </para>
/// </summary>
public interface IRawObjectStore
{
    Task EnsureBucketAsync(CancellationToken cancellationToken = default);

    Task PutAsync(string key, ReadOnlyMemory<byte> content, CancellationToken cancellationToken = default);

    /// <summary>Nesne yoksa <see langword="null"/> — çağıran bunu kayıp olarak işler.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default);

    Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default);
}

public sealed class S3RawObjectStore : IRawObjectStore, IDisposable
{
    private readonly AmazonS3Client _client;
    private readonly string _bucket;

    public S3RawObjectStore(IOptions<RawStoreOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var value = options.Value;

        _bucket = value.Bucket;
        _client = new AmazonS3Client(
            new BasicAWSCredentials(value.AccessKey, value.SecretKey),
            new AmazonS3Config
            {
                ServiceURL = value.ServiceUrl,
                ForcePathStyle = value.ForcePathStyle,
                AuthenticationRegion = value.Region,
                // AWS SDK v4 varsayılan olarak CRC32 sağlama başlıkları ekliyor;
                // AWS dışı S3 uygulamaları bunu her zaman kabul etmiyor.
                RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED,
                ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED,
            });
    }

    public async Task EnsureBucketAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.PutBucketAsync(new PutBucketRequest { BucketName = _bucket }, cancellationToken);
        }
        catch (AmazonS3Exception ex) when (
            string.Equals(ex.ErrorCode, "BucketAlreadyOwnedByYou", StringComparison.Ordinal) ||
            string.Equals(ex.ErrorCode, "BucketAlreadyExists", StringComparison.Ordinal))
        {
            // Zaten var — beklenen durum.
        }
    }

    public async Task PutAsync(
        string key,
        ReadOnlyMemory<byte> content,
        CancellationToken cancellationToken = default)
    {
        using var stream = new MemoryStream(content.ToArray(), writable: false);

        await _client.PutObjectAsync(
            new PutObjectRequest
            {
                BucketName = _bucket,
                Key = key,
                InputStream = stream,
                ContentType = "application/zstd",
            },
            cancellationToken);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await _client.GetObjectAsync(_bucket, key, cancellationToken);
            using var buffer = new MemoryStream();
            await response.ResponseStream.CopyToAsync(buffer, cancellationToken);
            return buffer.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<RawObjectInfo?> HeadAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            var response = await _client.GetObjectMetadataAsync(_bucket, key, cancellationToken);
            return new RawObjectInfo(key, response.ContentLength);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public void Dispose() => _client.Dispose();
}
