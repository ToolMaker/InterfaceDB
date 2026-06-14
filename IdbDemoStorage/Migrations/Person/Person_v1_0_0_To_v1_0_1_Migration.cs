using System;
using System.IO;
using MessagePack;
using Idb.Libraries.Abstraction.Services.Migrations;
using Idb.Libraries.Abstractions.Services.Serialization;
using Idb.Libraries.Abstractions.Dtos;
using MigrationTool.Contracts.Person.v1_0_0;
using MigrationTool.Contracts.Person.v1_0_1;

namespace MigrationTool.Migrations;

[MessagePackObject(keyAsPropertyName: true, AllowPrivate = true)]
public sealed partial class Person_v1_0_0_To_v1_0_1_Migration : MigrationTool.Contracts.Person.v1_0_0.IPerson, MigrationTool.Contracts.Person.v1_0_1.IPerson, IMigration
{
    [IgnoreMember]
    public IObjectEnvelope<MigrationTool.Contracts.Person.v1_0_0.IPerson>? LegacyEnvelope { get; set; }

    [IgnoreMember]
    public IObjectEnvelope<MigrationTool.Contracts.Person.v1_0_1.IPerson>? CurrentEnvelope { get; set; }

    public string SourceVersion => "1.0.0";
    public string TargetVersion => "1.0.1";

    [IgnoreMember]
    IObjectEnvelope<MigrationTool.Contracts.Person.v1_0_0.IPerson>? IStoredContract<MigrationTool.Contracts.Person.v1_0_0.IPerson>.Envelope
        => LegacyEnvelope;

    [IgnoreMember]
    IObjectEnvelope<MigrationTool.Contracts.Person.v1_0_1.IPerson>? IStoredContract<MigrationTool.Contracts.Person.v1_0_1.IPerson>.Envelope
        => CurrentEnvelope;

    public Guid Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int Age { get; set; }

    public string Role { get; set; } = string.Empty;

    public string Surname { get; set; } = string.Empty;

    public bool Up()
    {
        // TODO: Map legacy state to the new schema. Return false when migration cannot be completed.
        return true;
    }

    public bool Down()
    {
        // TODO: Map current state back to the legacy schema. Return false when rollback cannot be completed.
        return true;
    }
}
