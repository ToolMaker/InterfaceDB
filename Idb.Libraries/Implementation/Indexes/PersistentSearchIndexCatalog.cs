using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Idb.Libraries.Abstractions.Services.Indexes;

namespace Idb.Libraries.Implementation.Indexes;

internal sealed record SearchIndexCatalogRecord(
    string PhysicalKey,
    string RuntimeTypeName,
    object?[] ContractValues,
    byte[] RuntimeSchemaFingerprint,
    IReadOnlyList<Type> RuntimeValueTypes,
    object?[] RuntimeValues);

internal sealed record LoadedSearchIndexCatalogRecord(
    string PhysicalKey,
    string RuntimeTypeName,
    object?[] ContractValues,
    byte[] RuntimeSchemaFingerprint,
    object?[] RuntimeValues);

internal static class PersistentSearchIndexCatalog
{
    private const int FormatVersion = 1;
    private const int FingerprintBytes = 32;
    private const int FooterBytes = FingerprintBytes;
    private const int MaximumRecords = 100_000_000;
    private const int MaximumStringBytes = 1024 * 1024;
    private const int MaximumValueBytes = 16 * 1024 * 1024;
    private static ReadOnlySpan<byte> Magic => "IDBSIX01"u8;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
    };

    public static string GetPath(string directory, ReadOnlySpan<byte> contractSchemaFingerprint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        if (contractSchemaFingerprint.Length != FingerprintBytes)
        {
            throw new ArgumentException("Search-index schema fingerprints must contain 32 bytes.");
        }

        return Path.Combine(
            Path.GetFullPath(directory),
            $"catalog-{Convert.ToHexString(contractSchemaFingerprint[..8])}.idbsidx");
    }

    public static async ValueTask WriteAsync(
        string path,
        SearchIndexStorageIdentity storageIdentity,
        ReadOnlyMemory<byte> contractSchemaFingerprint,
        IReadOnlyList<Type> contractValueTypes,
        IReadOnlyList<SearchIndexCatalogRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contractValueTypes);
        ArgumentNullException.ThrowIfNull(records);
        ValidateFingerprint(storageIdentity.Fingerprint.Span, nameof(storageIdentity));
        ValidateFingerprint(contractSchemaFingerprint.Span, nameof(contractSchemaFingerprint));
        if (records.Count > MaximumRecords || storageIdentity.EntryCount != records.Count)
        {
            throw new InvalidOperationException(
                "The search-index catalog does not match the current storage entry count.");
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(path))
            ?? throw new InvalidOperationException("The search-index catalog path has no directory.");
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{path}.creating-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             new FileStreamOptions
                             {
                                 Mode = FileMode.CreateNew,
                                 Access = FileAccess.Write,
                                 Share = FileShare.Read,
                                 BufferSize = 64 * 1024,
                                 Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                             }))
            {
                using var writer = new CatalogWriter(stream);
                writer.Write(Magic);
                writer.WriteInt32(FormatVersion);
                writer.WriteInt32(records.Count);
                writer.WriteInt32(storageIdentity.EntryCount);
                writer.WriteInt32(contractValueTypes.Count);
                writer.WriteInt64(DateTimeOffset.UtcNow.UtcTicks);
                writer.Write(contractSchemaFingerprint.Span);
                writer.Write(storageIdentity.Fingerprint.Span);
                foreach (var record in records)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteString(writer, record.PhysicalKey);
                    WriteString(writer, record.RuntimeTypeName);
                    WriteValues(writer, record.ContractValues, contractValueTypes);
                    ValidateFingerprint(record.RuntimeSchemaFingerprint, nameof(records));
                    writer.Write(record.RuntimeSchemaFingerprint);
                    WriteValues(writer, record.RuntimeValues, record.RuntimeValueTypes);
                }

                writer.WriteFooter();
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    public static IReadOnlyList<LoadedSearchIndexCatalogRecord>? TryLoad(
        string path,
        SearchIndexStorageIdentity storageIdentity,
        ReadOnlyMemory<byte> contractSchemaFingerprint,
        IReadOnlyList<Type> contractValueTypes,
        Func<string, RuntimeSearchIndexCatalogSchema?> resolveRuntimeSchema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(contractValueTypes);
        ArgumentNullException.ThrowIfNull(resolveRuntimeSchema);
        if (!File.Exists(path)
            || storageIdentity.Fingerprint.Length != FingerprintBytes
            || contractSchemaFingerprint.Length != FingerprintBytes)
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                64 * 1024,
                FileOptions.SequentialScan);
            if (stream.Length < 96 + FooterBytes)
            {
                return null;
            }

            using var reader = new CatalogReader(stream, stream.Length - FooterBytes);
            Span<byte> magic = stackalloc byte[8];
            reader.ReadExactly(magic);
            if (!magic.SequenceEqual(Magic) || reader.ReadInt32() != FormatVersion)
            {
                return null;
            }

            var recordCount = reader.ReadInt32();
            var storageEntryCount = reader.ReadInt32();
            var contractValueCount = reader.ReadInt32();
            _ = reader.ReadInt64();
            var persistedContractSchema = reader.ReadBytes(FingerprintBytes);
            var persistedStorageFingerprint = reader.ReadBytes(FingerprintBytes);
            if (recordCount is < 0 or > MaximumRecords
                || recordCount != storageIdentity.EntryCount
                || storageEntryCount != storageIdentity.EntryCount
                || contractValueCount != contractValueTypes.Count
                || !persistedContractSchema.AsSpan().SequenceEqual(contractSchemaFingerprint.Span)
                || !persistedStorageFingerprint.AsSpan().SequenceEqual(storageIdentity.Fingerprint.Span))
            {
                return null;
            }

            var records = new List<LoadedSearchIndexCatalogRecord>(recordCount);
            var keys = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < recordCount; index++)
            {
                var physicalKey = ReadString(reader);
                var runtimeTypeName = ReadString(reader);
                if (!keys.Add(physicalKey))
                {
                    return null;
                }

                var contractValues = ReadValues(reader, contractValueTypes, contractValueCount);
                var runtimeFingerprint = reader.ReadBytes(FingerprintBytes);
                var runtimeSchema = resolveRuntimeSchema(runtimeTypeName);
                if (runtimeSchema is null
                    || !runtimeFingerprint.AsSpan().SequenceEqual(runtimeSchema.SchemaFingerprint.Span))
                {
                    return null;
                }

                var runtimeValues = ReadValues(
                    reader,
                    runtimeSchema.ValueTypes,
                    runtimeSchema.ValueTypes.Count);
                records.Add(new LoadedSearchIndexCatalogRecord(
                    physicalKey,
                    runtimeTypeName,
                    contractValues,
                    runtimeFingerprint,
                    runtimeValues));
            }

            reader.RequireDataEnd();
            return reader.VerifyFooter() ? records : null;
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or InvalidDataException
                                          or JsonException
                                          or NotSupportedException
                                          or CryptographicException
                                          or ArgumentException
                                          or OverflowException)
        {
            return null;
        }
    }

    private static void WriteValues(
        CatalogWriter writer,
        IReadOnlyList<object?> values,
        IReadOnlyList<Type> valueTypes)
    {
        if (values.Count != valueTypes.Count)
        {
            throw new InvalidOperationException("Search-index catalog values do not match their schema.");
        }

        writer.WriteInt32(values.Count);
        for (var index = 0; index < values.Count; index++)
        {
            var value = values[index];
            if (value is null)
            {
                writer.WriteInt32(-1);
                continue;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, valueTypes[index], JsonOptions);
            if (bytes.Length > MaximumValueBytes)
            {
                throw new InvalidOperationException("A search-index value exceeds the catalog size limit.");
            }

            writer.WriteInt32(bytes.Length);
            writer.Write(bytes);
        }
    }

    private static object?[] ReadValues(
        CatalogReader reader,
        IReadOnlyList<Type> valueTypes,
        int expectedCount)
    {
        var count = reader.ReadInt32();
        if (count != expectedCount || count != valueTypes.Count)
        {
            throw new InvalidDataException("Search-index catalog value count does not match its schema.");
        }

        var values = new object?[count];
        for (var index = 0; index < count; index++)
        {
            var length = reader.ReadInt32();
            if (length == -1)
            {
                continue;
            }

            if (length < 0 || length > MaximumValueBytes)
            {
                throw new InvalidDataException("Search-index catalog contains an invalid value length.");
            }

            values[index] = JsonSerializer.Deserialize(
                reader.ReadBytes(length),
                valueTypes[index],
                JsonOptions);
        }

        return values;
    }

    private static void WriteString(CatalogWriter writer, string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var byteCount = Encoding.UTF8.GetByteCount(value);
        if (byteCount > MaximumStringBytes)
        {
            throw new InvalidOperationException("A search-index catalog string exceeds the size limit.");
        }

        var buffer = ArrayPool<byte>.Shared.Rent(byteCount);
        try
        {
            _ = Encoding.UTF8.GetBytes(value, buffer.AsSpan(0, byteCount));
            writer.WriteInt32(byteCount);
            writer.Write(buffer.AsSpan(0, byteCount));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer, clearArray: false);
        }
    }

    private static string ReadString(CatalogReader reader)
    {
        var byteCount = reader.ReadInt32();
        if (byteCount is <= 0 or > MaximumStringBytes)
        {
            throw new InvalidDataException("Search-index catalog contains an invalid string length.");
        }

        return Encoding.UTF8.GetString(reader.ReadBytes(byteCount));
    }

    private static void ValidateFingerprint(ReadOnlySpan<byte> fingerprint, string parameterName)
    {
        if (fingerprint.Length != FingerprintBytes)
        {
            throw new ArgumentException("Search-index fingerprints must contain 32 bytes.", parameterName);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // A stale derived catalog is ignored on the next startup.
        }
    }

    private sealed class CatalogWriter : IDisposable
    {
        private readonly Stream _stream;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public CatalogWriter(Stream stream) => _stream = stream;

        public void Write(ReadOnlySpan<byte> bytes)
        {
            _hash.AppendData(bytes);
            _stream.Write(bytes);
        }

        public void WriteInt32(int value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            BinaryPrimitives.WriteInt32LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteInt64(long value)
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            BinaryPrimitives.WriteInt64LittleEndian(bytes, value);
            Write(bytes);
        }

        public void WriteFooter() => _stream.Write(_hash.GetHashAndReset());

        public void Dispose() => _hash.Dispose();
    }

    private sealed class CatalogReader : IDisposable
    {
        private readonly Stream _stream;
        private readonly long _dataLength;
        private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

        public CatalogReader(Stream stream, long dataLength)
        {
            _stream = stream;
            _dataLength = dataLength;
        }

        public byte[] ReadBytes(int count)
        {
            var bytes = new byte[count];
            ReadExactly(bytes);
            return bytes;
        }

        public void ReadExactly(Span<byte> destination)
        {
            if (destination.Length > _dataLength - _stream.Position)
            {
                throw new InvalidDataException("Search-index catalog is truncated.");
            }

            _stream.ReadExactly(destination);
            _hash.AppendData(destination);
        }

        public int ReadInt32()
        {
            Span<byte> bytes = stackalloc byte[sizeof(int)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt32LittleEndian(bytes);
        }

        public long ReadInt64()
        {
            Span<byte> bytes = stackalloc byte[sizeof(long)];
            ReadExactly(bytes);
            return BinaryPrimitives.ReadInt64LittleEndian(bytes);
        }

        public void RequireDataEnd()
        {
            if (_stream.Position != _dataLength)
            {
                throw new InvalidDataException("Search-index catalog contains trailing data.");
            }
        }

        public bool VerifyFooter()
        {
            Span<byte> expected = stackalloc byte[FooterBytes];
            _stream.ReadExactly(expected);
            return _stream.Position == _stream.Length
                && expected.SequenceEqual(_hash.GetHashAndReset());
        }

        public void Dispose() => _hash.Dispose();
    }
}

internal sealed record RuntimeSearchIndexCatalogSchema(
    ReadOnlyMemory<byte> SchemaFingerprint,
    IReadOnlyList<Type> ValueTypes);
