using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using MessagePack;

namespace Idb.Transport;

/// <summary>A deliberately closed MessagePack schema. Domain payloads are never deserialized here.</summary>
internal static class MessagePackFrameCodec
{
    internal const int HeaderBytes = 44;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    internal static byte[] Encode(WireMessage message, TransportLimits limits)
    {
        ArgumentNullException.ThrowIfNull(message);
        limits.Validate();
        Validate(message, limits);
        var body = new BoundedBufferWriter(limits.MaximumFrameBytes - HeaderBytes);
        body.Array(14);
        body.Integer((int)message.Kind);
        body.Integer(message.CorrelationId);
        body.String(message.StoreAlias, 128);
        WriteManifest(body, message.Manifest);
        WriteLimits(body, message.Limits);
        body.Integer((int)message.Capabilities);
        WriteRequest(body, message.Request, limits);
        WriteResponse(body, message.Response, limits);
        body.Integer((int)message.Error);
        body.Integer(message.TimeoutMilliseconds);
        body.String(message.AccessToken, TransportValidation.MaximumAccessTokenBytes);
        body.Integer((int)message.AuthenticationMode);
        body.String(message.RequestedScope, TransportValidation.MaximumControlStringBytes);
        body.String(message.ScopeIdentity, 128);
        var frame = new byte[HeaderBytes + body.WrittenCount];
        "IDBT"u8.CopyTo(frame);
        frame[4] = 3;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(8, 4), (uint)body.WrittenCount);
        body.WrittenSpan.CopyTo(frame.AsSpan(HeaderBytes));
        SHA256.HashData(body.WrittenSpan, frame.AsSpan(12, 32));
        return frame;
    }

    internal static async ValueTask WriteAsync(Stream stream, WireMessage message, TransportLimits limits,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var frame = Encode(message, limits);
        await stream.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
    }

    internal static ValueTask<WireMessage?> ReadAsync(Stream stream, TransportLimits limits,
        CancellationToken cancellationToken = default)
        => ReadAsync(stream, limits, limits.MaximumFrameBytes, cancellationToken);

    internal static async ValueTask<WireMessage?> ReadAsync(Stream stream, TransportLimits limits,
        int maximumTotalFrameBytes, CancellationToken cancellationToken = default)
    {
        limits.Validate();
        if (maximumTotalFrameBytes < HeaderBytes + 1 || maximumTotalFrameBytes > limits.MaximumFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(maximumTotalFrameBytes));
        var header = new byte[HeaderBytes];
        if (!await ReadExactlyAsync(stream, header, true, cancellationToken).ConfigureAwait(false)) return null;
        if (!header.AsSpan(0, 4).SequenceEqual("IDBT"u8) || header[4] != 3
            || header[5] != 0 || header[6] != 0 || header[7] != 0)
            throw Protocol("Unsupported transport frame header.");
        var length = BinaryPrimitives.ReadUInt32BigEndian(header.AsSpan(8, 4));
        if (length > maximumTotalFrameBytes - HeaderBytes) throw Limit();
        if (length == 0) throw Protocol("The transport frame body is empty.");
        var body = new byte[(int)length];
        await ReadExactlyAsync(stream, body, false, cancellationToken).ConfigureAwait(false);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), header.AsSpan(12, 32)))
            throw Protocol("Transport frame checksum mismatch.");
        return Decode(body, limits);
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, Memory<byte> buffer, bool cleanEof,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                if (cleanEof && offset == 0) return false;
                throw Protocol("Truncated transport frame.");
            }
            offset += read;
        }
        return true;
    }

    private static WireMessage Decode(byte[] body, TransportLimits limits)
    {
        try
        {
            var reader = new MessagePackReader(body);
            Shape(ref reader, 14);
            var message = new WireMessage
            {
                Kind = (WireMessageKind)reader.ReadInt32(),
                CorrelationId = reader.ReadInt64(),
                StoreAlias = String(ref reader, 128),
                Manifest = ReadManifest(ref reader),
                Limits = ReadLimits(ref reader),
                Capabilities = (EncodedStoreCapabilities)reader.ReadInt32(),
                Request = ReadRequest(ref reader, limits),
                Response = ReadResponse(ref reader, limits),
                Error = (RemoteErrorCode)reader.ReadInt32(),
                TimeoutMilliseconds = reader.ReadInt32(),
                AccessToken = String(ref reader, TransportValidation.MaximumAccessTokenBytes),
                AuthenticationMode = (TcpStoreAuthenticationMode)reader.ReadInt32(),
                RequestedScope = String(ref reader, TransportValidation.MaximumControlStringBytes),
                ScopeIdentity = String(ref reader, 128)
            };
            if (!reader.End) throw Protocol("Trailing transport message data.");
            Validate(message, limits);
            return message;
        }
        catch (Exception exception) when (exception is MessagePackSerializationException or EndOfStreamException
            or InvalidOperationException or OverflowException or ArgumentException)
        {
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Malformed transport message.", exception);
        }
    }

    private static void Validate(WireMessage message, TransportLimits limits)
    {
        try
        {
            if (!Enum.IsDefined(message.Kind) || !Enum.IsDefined(message.Error) || !Enum.IsDefined(message.AuthenticationMode)
                || (message.Capabilities & ~EncodedStoreCapabilities.All) != 0
                || message.CorrelationId < 0 || message.TimeoutMilliseconds < 0)
                throw Protocol("Invalid transport message fields.");
            CheckString(message.StoreAlias, 128);
            CheckString(message.AccessToken, TransportValidation.MaximumAccessTokenBytes);
            CheckString(message.RequestedScope, TransportValidation.MaximumControlStringBytes);
            CheckString(message.ScopeIdentity, 128);
            if (message.RequestedScope is not null && message.Kind is not (WireMessageKind.Hello or WireMessageKind.Subscribe or WireMessageKind.Discover)
                || message.ScopeIdentity is not null && message.Kind is not (WireMessageKind.Hello or WireMessageKind.Subscribe or WireMessageKind.Discover or WireMessageKind.HelloResult))
                throw Protocol("Unexpected data scope fields.");
            if (message.RequestedScope is not null) TransportValidation.ControlString(message.RequestedScope, false);
            if (message.ScopeIdentity is not null) TransportValidation.ControlString(message.ScopeIdentity, false);
            // Slot 10 remains a JWT in existing modes; Provider mode uses bounded provider credentials
            // on requests and a non-secret authenticated identity receipt on successful HelloResult.
            if (message.AccessToken is not null && message.Kind is not (WireMessageKind.Hello or WireMessageKind.Subscribe or WireMessageKind.Discover)
                    && !(message.AuthenticationMode == TcpStoreAuthenticationMode.Provider && message.Kind is WireMessageKind.CreateAccount or WireMessageKind.HelloResult)
                || message.AuthenticationMode != TcpStoreAuthenticationMode.MutualTls
                    && message.Kind is not (WireMessageKind.Hello or WireMessageKind.Subscribe or WireMessageKind.Discover or WireMessageKind.HelloResult or WireMessageKind.CreateAccount or WireMessageKind.AccountResult))
                throw Protocol("Unexpected authentication fields.");
            if (message.StoreAlias is not null) TransportValidation.Alias(message.StoreAlias);
            if (message.Manifest is { } manifest)
            {
                foreach (var field in new[] { manifest.ApiVersion, manifest.StorageEngine, manifest.StorageFormat,
                    manifest.EnvelopeFormat, manifest.ContractIdentity, manifest.ContractVersion,
                    manifest.ContractSchemaHash, manifest.ImplementationManifestHash, manifest.IndexDefinitionHash,
                    manifest.ApprovedBuildIdentity })
                    CheckString(field, TransportValidation.MaximumControlStringBytes);
            }
            message.Manifest?.Validate();
            message.Limits?.Validate();
            if (message.Request is { } request)
            {
                CheckString(request.Key, limits.MaximumKeyBytes);
                CheckString(request.AfterKey, limits.MaximumKeyBytes);
                CheckBinary(request.Payload, limits);
                if (request.Keys is { } keys)
                {
                    CheckCount(keys.Length, limits.MaximumBatchItems);
                    foreach (var key in keys) CheckString(key, limits.MaximumKeyBytes);
                }
                if (request.Mutations is { } mutations)
                {
                    CheckCount(mutations.Length, limits.MaximumBatchItems);
                    foreach (var mutation in mutations)
                    {
                        if (mutation is null) throw Protocol("A mutation is missing.");
                        CheckString(mutation.Key, limits.MaximumKeyBytes);
                        CheckBinary(mutation.Payload, limits);
                    }
                }
                if (request.PageSize > limits.MaximumPageItems) throw Limit();
                TransportValidation.Request(request, limits);
            }
            if (message.Response is { } response)
            {
                if (response.AffectedCount < 0) throw Protocol("Invalid affected count.");
                CheckBinary(response.Payload, limits);
                CheckString(response.NextAfterKey, limits.MaximumKeyBytes);
                if (response.NextAfterKey is not null && string.IsNullOrWhiteSpace(response.NextAfterKey))
                    throw Protocol("An enumeration cursor is empty.");
                if (response.Records is { } records)
                {
                    CheckCount(records.Length, Math.Max(limits.MaximumBatchItems, limits.MaximumPageItems));
                    foreach (var record in records)
                    {
                        if (record is null || string.IsNullOrWhiteSpace(record.Key) || record.Payload is null)
                            throw Protocol("An encoded record is incomplete.");
                        CheckString(record.Key, limits.MaximumKeyBytes);
                        CheckBinary(record.Payload, limits);
                    }
                }
            }
        }
        catch (ArgumentException exception)
        {
            throw new RemoteStoreException(RemoteErrorCode.ProtocolError, "Invalid transport message fields.", exception);
        }
    }

    private static void CheckString(string? value, int maximum)
    {
        if (value is not null && Utf8.GetByteCount(value) > maximum) throw Limit();
    }

    private static void CheckBinary(byte[]? value, TransportLimits limits)
    {
        if (value is not null && value.Length > limits.MaximumPayloadBytes) throw Limit();
    }

    private static void CheckCount(int count, int maximum)
    {
        if (count < 0) throw Protocol("Invalid collection count.");
        if (count > maximum) throw Limit();
    }

    private static void Shape(ref MessagePackReader reader, int count)
    {
        if (!reader.TryReadArrayHeader(out var actual) || actual != count)
            throw Protocol("Invalid transport message shape.");
    }

    private static int CollectionCount(ref MessagePackReader reader, int maximum)
    {
        if (!reader.TryReadArrayHeader(out var count)) throw Protocol("Truncated collection header.");
        CheckCount(count, maximum);
        return count;
    }

    private static string? String(ref MessagePackReader reader, int maximum)
    {
        var bytes = reader.ReadStringSequence();
        if (bytes is null) return null;
        if (bytes.Value.Length > maximum) throw Limit();
        return bytes.Value.IsSingleSegment
            ? Utf8.GetString(bytes.Value.FirstSpan)
            : Utf8.GetString(bytes.Value.ToArray());
    }

    private static byte[]? Binary(ref MessagePackReader reader, TransportLimits limits)
    {
        if (reader.NextMessagePackType is not (MessagePackType.Nil or MessagePackType.Binary))
            throw Protocol("Expected a binary payload.");
        var bytes = reader.ReadBytes();
        if (bytes is null) return null;
        if (bytes.Value.Length > limits.MaximumPayloadBytes) throw Limit();
        return bytes.Value.ToArray();
    }

    private static CompatibilityManifest? ReadManifest(ref MessagePackReader reader)
    {
        if (reader.TryReadNil()) return null;
        Shape(ref reader, 10);
        return new()
        {
            ApiVersion = String(ref reader, 1024)!, StorageEngine = String(ref reader, 1024)!,
            StorageFormat = String(ref reader, 1024)!, EnvelopeFormat = String(ref reader, 1024)!,
            ContractIdentity = String(ref reader, 1024)!, ContractVersion = String(ref reader, 1024)!,
            ContractSchemaHash = String(ref reader, 1024)!, ImplementationManifestHash = String(ref reader, 1024)!,
            IndexDefinitionHash = String(ref reader, 1024)!, ApprovedBuildIdentity = String(ref reader, 1024)
        };
    }

    private static TransportLimits? ReadLimits(ref MessagePackReader reader)
    {
        if (reader.TryReadNil()) return null;
        Shape(ref reader, 5);
        return new()
        {
            MaximumFrameBytes = reader.ReadInt32(), MaximumPayloadBytes = reader.ReadInt32(),
            MaximumKeyBytes = reader.ReadInt32(), MaximumBatchItems = reader.ReadInt32(),
            MaximumPageItems = reader.ReadInt32()
        };
    }

    private static EncodedStoreRequest? ReadRequest(ref MessagePackReader reader, TransportLimits limits)
    {
        if (reader.TryReadNil()) return null;
        Shape(ref reader, 7);
        var command = (EncodedStoreCommand)reader.ReadInt32();
        var key = String(ref reader, limits.MaximumKeyBytes);
        var payload = Binary(ref reader, limits);
        string[]? keys = null;
        if (!reader.TryReadNil())
        {
            var count = CollectionCount(ref reader, limits.MaximumBatchItems);
            keys = new string[count];
            for (var index = 0; index < count; index++) keys[index] = String(ref reader, limits.MaximumKeyBytes)!;
        }
        EncodedMutation[]? mutations = null;
        if (!reader.TryReadNil())
        {
            var count = CollectionCount(ref reader, limits.MaximumBatchItems);
            mutations = new EncodedMutation[count];
            for (var index = 0; index < count; index++)
            {
                Shape(ref reader, 2);
                mutations[index] = new(String(ref reader, limits.MaximumKeyBytes)!, Binary(ref reader, limits));
            }
        }
        return new()
        {
            Command = command, Key = key, Payload = payload, Keys = keys, Mutations = mutations,
            AfterKey = String(ref reader, limits.MaximumKeyBytes), PageSize = reader.ReadInt32()
        };
    }

    private static EncodedStoreResponse? ReadResponse(ref MessagePackReader reader, TransportLimits limits)
    {
        if (reader.TryReadNil()) return null;
        Shape(ref reader, 5);
        var found = reader.ReadBoolean();
        var payload = Binary(ref reader, limits);
        EncodedRecord[]? records = null;
        if (!reader.TryReadNil())
        {
            var count = CollectionCount(ref reader, Math.Max(limits.MaximumBatchItems, limits.MaximumPageItems));
            records = new EncodedRecord[count];
            for (var index = 0; index < count; index++)
            {
                Shape(ref reader, 2);
                records[index] = new(String(ref reader, limits.MaximumKeyBytes)!, Binary(ref reader, limits)!);
            }
        }
        return new()
        {
            Found = found, Payload = payload, Records = records,
            NextAfterKey = String(ref reader, limits.MaximumKeyBytes), AffectedCount = reader.ReadInt32()
        };
    }

    private static void WriteManifest(BoundedBufferWriter writer, CompatibilityManifest? manifest)
    {
        if (manifest is null) { writer.Nil(); return; }
        writer.Array(10);
        writer.String(manifest.ApiVersion, 1024); writer.String(manifest.StorageEngine, 1024);
        writer.String(manifest.StorageFormat, 1024); writer.String(manifest.EnvelopeFormat, 1024);
        writer.String(manifest.ContractIdentity, 1024); writer.String(manifest.ContractVersion, 1024);
        writer.String(manifest.ContractSchemaHash, 1024); writer.String(manifest.ImplementationManifestHash, 1024);
        writer.String(manifest.IndexDefinitionHash, 1024); writer.String(manifest.ApprovedBuildIdentity, 1024);
    }

    private static void WriteLimits(BoundedBufferWriter writer, TransportLimits? limits)
    {
        if (limits is null) { writer.Nil(); return; }
        writer.Array(5);
        writer.Integer(limits.MaximumFrameBytes); writer.Integer(limits.MaximumPayloadBytes);
        writer.Integer(limits.MaximumKeyBytes); writer.Integer(limits.MaximumBatchItems);
        writer.Integer(limits.MaximumPageItems);
    }

    private static void WriteRequest(BoundedBufferWriter writer, EncodedStoreRequest? request, TransportLimits limits)
    {
        if (request is null) { writer.Nil(); return; }
        writer.Array(7);
        writer.Integer((int)request.Command); writer.String(request.Key, limits.MaximumKeyBytes);
        writer.Binary(request.Payload, limits.MaximumPayloadBytes);
        if (request.Keys is { } keys)
        {
            CheckCount(keys.Length, limits.MaximumBatchItems);
            writer.Array(keys.Length);
            foreach (var key in keys) writer.String(key, limits.MaximumKeyBytes);
        }
        else writer.Nil();
        if (request.Mutations is { } mutations)
        {
            CheckCount(mutations.Length, limits.MaximumBatchItems);
            writer.Array(mutations.Length);
            foreach (var mutation in mutations)
            {
                writer.Array(2); writer.String(mutation.Key, limits.MaximumKeyBytes);
                writer.Binary(mutation.Payload, limits.MaximumPayloadBytes);
            }
        }
        else writer.Nil();
        writer.String(request.AfterKey, limits.MaximumKeyBytes); writer.Integer(request.PageSize);
    }

    private static void WriteResponse(BoundedBufferWriter writer, EncodedStoreResponse? response, TransportLimits limits)
    {
        if (response is null) { writer.Nil(); return; }
        writer.Array(5); writer.Boolean(response.Found); writer.Binary(response.Payload, limits.MaximumPayloadBytes);
        if (response.Records is { } records)
        {
            CheckCount(records.Length, Math.Max(limits.MaximumBatchItems, limits.MaximumPageItems));
            writer.Array(records.Length);
            foreach (var record in records)
            {
                writer.Array(2); writer.String(record.Key, limits.MaximumKeyBytes);
                writer.Binary(record.Payload, limits.MaximumPayloadBytes);
            }
        }
        else writer.Nil();
        writer.String(response.NextAfterKey, limits.MaximumKeyBytes); writer.Integer(response.AffectedCount);
    }

    private static RemoteStoreException Protocol(string message) => new(RemoteErrorCode.ProtocolError, message);
    private static RemoteStoreException Limit() => new(RemoteErrorCode.LimitExceeded, "Transport resource limit exceeded.");

    // This writer only exposes the schema's allowed primitives. It checks the budget before growing
    // or copying, including aggregate batches whose individually valid payloads exceed a frame.
    private sealed class BoundedBufferWriter(int maximum) : IBufferWriter<byte>
    {
        private byte[] _buffer = new byte[Math.Min(maximum, 256)];
        public int WrittenCount { get; private set; }
        public ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, WrittenCount);

        public void Advance(int count)
        {
            if (count < 0 || count > _buffer.Length - WrittenCount) throw new ArgumentOutOfRangeException(nameof(count));
            WrittenCount += count;
        }

        public Memory<byte> GetMemory(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsMemory(WrittenCount); }
        public Span<byte> GetSpan(int sizeHint = 0) { Ensure(sizeHint); return _buffer.AsSpan(WrittenCount); }

        private void Ensure(int count)
        {
            if (count < 0) throw new ArgumentOutOfRangeException(nameof(count));
            count = Math.Max(1, count);
            if (count > maximum - WrittenCount) throw Limit();
            if (count <= _buffer.Length - WrittenCount) return;
            System.Array.Resize(ref _buffer, Math.Min(maximum, Math.Max(WrittenCount + count, _buffer.Length * 2)));
        }

        private void Byte(byte value) { GetSpan(1)[0] = value; Advance(1); }
        public void Nil() => Byte(0xc0);
        public void Boolean(bool value) => Byte(value ? (byte)0xc3 : (byte)0xc2);

        public void Integer(long value)
        {
            if (value < 0) throw Protocol("Negative transport integer.");
            if (value <= 127) { Byte((byte)value); return; }
            if (value <= byte.MaxValue) { Byte(0xcc); Byte((byte)value); return; }
            if (value <= ushort.MaxValue)
            {
                var span = GetSpan(3); span[0] = 0xcd;
                BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)value); Advance(3); return;
            }
            if (value <= uint.MaxValue)
            {
                var span = GetSpan(5); span[0] = 0xce;
                BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)value); Advance(5); return;
            }
            var wide = GetSpan(9); wide[0] = 0xcf;
            BinaryPrimitives.WriteUInt64BigEndian(wide[1..], (ulong)value); Advance(9);
        }

        public void Array(int count)
        {
            if (count < 16) { Byte((byte)(0x90 | count)); return; }
            var span = GetSpan(3); span[0] = 0xdc;
            BinaryPrimitives.WriteUInt16BigEndian(span[1..], checked((ushort)count)); Advance(3);
        }

        public void String(string? value, int maxBytes)
        {
            if (value is null) { Nil(); return; }
            var count = Utf8.GetByteCount(value);
            if (count > maxBytes) throw Limit();
            if (count < 32) Byte((byte)(0xa0 | count));
            else if (count <= byte.MaxValue) { Byte(0xd9); Byte((byte)count); }
            else
            {
                var span = GetSpan(3); span[0] = 0xda;
                BinaryPrimitives.WriteUInt16BigEndian(span[1..], checked((ushort)count)); Advance(3);
            }
            if (count == 0) return;
            Utf8.GetBytes(value, GetSpan(count)); Advance(count);
        }

        public void Binary(byte[]? value, int maxBytes)
        {
            if (value is null) { Nil(); return; }
            var count = value.Length;
            if (count > maxBytes) throw Limit();
            if (count <= byte.MaxValue) { Byte(0xc4); Byte((byte)count); }
            else if (count <= ushort.MaxValue)
            {
                var span = GetSpan(3); span[0] = 0xc5;
                BinaryPrimitives.WriteUInt16BigEndian(span[1..], (ushort)count); Advance(3);
            }
            else
            {
                var span = GetSpan(5); span[0] = 0xc6;
                BinaryPrimitives.WriteUInt32BigEndian(span[1..], (uint)count); Advance(5);
            }
            if (count == 0) return;
            value.CopyTo(GetSpan(count)); Advance(count);
        }
    }
}
