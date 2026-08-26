using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Idb.Libraries.Implementation.Serialization;
using Idb.Storage.SegmentCore.ObjectLog;

namespace Idb.Storage.SegmentCore.Tests;

[TestClass]
public sealed class SegmentCoreCompatibilityTests
{
    [TestMethod]
    public async Task StorageFormatManifest_IsCreatedAndRejectsUnsupportedMajorVersion()
    {
        using var root = new CompatibilityTemporaryDirectory();
        var options = Options();
        await using (var store = await SegmentCoreStore.OpenAsync(root.Path, options))
        {
            await store.SaveAsync("format", new byte[] { 1, 2, 3 });
            _ = await store.CheckpointAsync();
        }

        var path = Path.Combine(root.Path, "storage-format.manifest");
        Assert.IsTrue(File.Exists(path));
        var bytes = File.ReadAllBytes(path);
        BinaryPrimitives.WriteInt32LittleEndian(
            bytes.AsSpan(16),
            SegmentCoreStorageFormat.EngineMajor + 1);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(124), Crc32C.Compute(bytes.AsSpan(0, 124)));
        File.WriteAllBytes(path, bytes);

        _ = await Assert.ThrowsExactlyAsync<SegmentCoreCompatibilityException>(
            () => SegmentCoreStore.OpenAsync(root.Path, options).AsTask());
    }

    [TestMethod]
    public void StableFormatAndPublicMaintenanceContract_AreExplicitlyVersioned()
    {
        Assert.AreEqual(1, SegmentCoreStorageFormat.PublicApiMajor);
        Assert.AreEqual(0, SegmentCoreStorageFormat.PublicApiMinor);
        Assert.AreEqual(1, SegmentCoreStorageFormat.EngineMajor);
        Assert.AreEqual(0, SegmentCoreStorageFormat.EngineMinor);
        Assert.AreEqual(1, SegmentCoreStorageFormat.ObjectLogVersion);
        Assert.AreEqual(2, SegmentCoreStorageFormat.PrimaryCheckpointVersion);
        Assert.AreEqual("fnv1a-utf16le-hash-stripes-v1", SegmentCoreStorageFormat.RouterId);
        Assert.AreEqual("InterfaceDB.BinaryEnvelope.IDB4", SegmentCoreStorageFormat.EnvelopeSerializationId);
        var serializerMagic = typeof(BinaryObjectSerializer<>).GetField(
            "CurrentMagic",
            BindingFlags.NonPublic | BindingFlags.Static)?.GetRawConstantValue();
        Assert.AreEqual(
            SegmentCoreStorageFormat.EnvelopeSerializationId,
            $"InterfaceDB.BinaryEnvelope.{serializerMagic}",
            "SegmentCore's declared envelope identity must match the serializer's actual write format.");

        var store = typeof(SegmentCoreStore);
        Assert.IsNotNull(store.GetMethod(nameof(SegmentCoreStore.BackupAsync)));
        Assert.IsNotNull(store.GetMethod(nameof(SegmentCoreStore.RestoreBackupAsync)));
        Assert.IsNotNull(store.GetMethod(nameof(SegmentCoreStore.BeginTransactionAsync)));

        var maintenance = typeof(ISegmentCoreRepositoryMaintenance<>);
        Assert.IsNotNull(maintenance.GetMethod("BackupAsync"));
        Assert.IsNotNull(maintenance.GetMethod("MigrateFromAsync"));
        Assert.IsNotNull(maintenance.GetMethod("VerifyIntegrityAsync"));
    }

    [TestMethod]
    public void PublicApiSurface_MatchesTheReviewedVersionOneBaseline()
    {
        var lines = typeof(SegmentCoreStore).Assembly.ExportedTypes
            .OrderBy(static type => type.FullName, StringComparer.Ordinal)
            .SelectMany(DescribeType)
            .ToArray();
        var text = string.Join('\n', lines);
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        Assert.AreEqual(
            "77655B87A441457BA1B6457D8B32FCE4FDC775D00A776105F14C79E3309CB01F",
            hash,
            "The public API changed. Review the API diff and apply the compatibility policy before updating the approved hash.\n"
            + text);
    }

    private static IEnumerable<string> DescribeType(Type type)
    {
        var kind = type.IsInterface ? "interface"
            : type.IsEnum ? "enum"
            : type.IsValueType ? "struct"
            : type.IsClass ? "class"
            : "type";
        yield return $"type {kind} {TypeName(type)} base={TypeName(type.BaseType)}";
        foreach (var @interface in type.GetInterfaces().OrderBy(TypeName, StringComparer.Ordinal))
        {
            yield return $"  implements {TypeName(@interface)}";
        }

        const BindingFlags flags = BindingFlags.Public
            | BindingFlags.Instance
            | BindingFlags.Static
            | BindingFlags.DeclaredOnly;
        foreach (var member in type.GetMembers(flags)
                     .Where(static member => member.MemberType is MemberTypes.Constructor
                         or MemberTypes.Method
                         or MemberTypes.Property
                         or MemberTypes.Field
                         or MemberTypes.Event)
                     .Select(DescribeMember)
                     .Where(static signature => signature is not null)
                     .OrderBy(static signature => signature, StringComparer.Ordinal))
        {
            yield return "  " + member;
        }
    }

    private static string? DescribeMember(MemberInfo member) => member switch
    {
        ConstructorInfo constructor =>
            $"ctor({string.Join(',', constructor.GetParameters().Select(DescribeParameter))})",
        MethodInfo method when !method.IsSpecialName =>
            $"method {(method.IsStatic ? "static " : string.Empty)}{method.Name}`{method.GetGenericArguments().Length}"
            + $"({string.Join(',', method.GetParameters().Select(DescribeParameter))}):{TypeName(method.ReturnType)}",
        PropertyInfo property =>
            $"property {property.Name}[{string.Join(',', property.GetIndexParameters().Select(DescribeParameter))}]"
            + $":{TypeName(property.PropertyType)} get={property.GetMethod is not null} set={property.SetMethod is not null}",
        FieldInfo field =>
            $"field {(field.IsStatic ? "static " : string.Empty)}{(field.IsInitOnly ? "readonly " : string.Empty)}"
            + $"{field.Name}:{TypeName(field.FieldType)}"
            + (field.IsLiteral ? $"={field.GetRawConstantValue()}" : string.Empty),
        EventInfo @event => $"event {@event.Name}:{TypeName(@event.EventHandlerType)}",
        _ => null
    };

    private static string DescribeParameter(ParameterInfo parameter) =>
        $"{(parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty)}"
        + TypeName(parameter.ParameterType.IsByRef
            ? parameter.ParameterType.GetElementType()
            : parameter.ParameterType)
        + (parameter.HasDefaultValue ? $"={parameter.DefaultValue ?? "null"}" : string.Empty);

    private static string TypeName(Type? type)
    {
        if (type is null)
        {
            return "-";
        }

        if (type.IsArray)
        {
            return TypeName(type.GetElementType()) + $"[{new string(',', type.GetArrayRank() - 1)}]";
        }

        if (type.IsGenericParameter)
        {
            return $"!{type.GenericParameterPosition}:{type.Name}";
        }

        if (!type.IsGenericType)
        {
            return type.FullName ?? type.Name;
        }

        var definitionName = type.GetGenericTypeDefinition().FullName ?? type.Name;
        var tick = definitionName.IndexOf('`');
        if (tick >= 0)
        {
            definitionName = definitionName[..tick];
        }

        return definitionName + "<" + string.Join(',', type.GetGenericArguments().Select(TypeName)) + ">";
    }

    private static SegmentCoreOptions Options() => new()
    {
        TransactionProtocol = SegmentCoreTransactionProtocol.MutationWriteAheadLog,
        LogOptions = new StripedObjectLogProbeOptions
        {
            StripeCount = 2,
            DurabilityStrategy = ObjectLogDurabilityStrategy.ExplicitFlush
        }
    };

    private sealed class CompatibilityTemporaryDirectory : IDisposable
    {
        public CompatibilityTemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "interface-db-segment-compatibility-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
            }
        }
    }
}
