using System;
using System.IO;
using MessagePack;
using Idb.Libraries.Abstractions.Services.Migrations;
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
        // v1.0.1 adds Surname. Preserve all shared members and initialize the new value.
        Surname = string.IsNullOrWhiteSpace(Surname) ? "Unknown" : Surname;
        return true;
    }

    public bool Down()
    {
        // v1.0.0 has no Surname member; the shared members already represent the legacy shape.
        return true;
    }
}
