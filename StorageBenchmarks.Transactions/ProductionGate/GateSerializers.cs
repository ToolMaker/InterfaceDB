using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Implementation.Serialization;
using Idb.Serialization.Protobuf;
using MessagePack;
using MessagePack.Resolvers;
using ProtoBuf.Meta;

internal static class GateSerializers
{
    public static IRevisionObjectSerializer<IProductionGateRecord> CreateProtobuf()
    {
        var model = RuntimeTypeModel.Create();
        model.Add(typeof(ProductionGateRecord), false)
            .Add(1, nameof(ProductionGateRecord.Id))
            .Add(2, nameof(ProductionGateRecord.Name))
            .Add(3, nameof(ProductionGateRecord.Age))
            .Add(4, nameof(ProductionGateRecord.Role))
            .Add(5, nameof(ProductionGateRecord.Content))
            .Add(6, nameof(ProductionGateRecord.Revision));
        return new ProtobufObjectSerializer<IProductionGateRecord>(model,
            new Dictionary<string, Type> { ["production-gate-record-v1"] = typeof(ProductionGateRecord) });
    }

    public static IRevisionObjectSerializer<IProductionGateRecord> CreateMessagePack() =>
        new BinaryObjectSerializer<IProductionGateRecord>(
            MessagePackSerializerOptions.Standard.WithResolver(TypelessContractlessStandardResolver.Instance),
            runtimeTypeAllowlist: new(typeof(ProductionGateRecord)));
}
