using Idb.Libraries.Abstractions.Services.Migrations;
using MessagePack;

namespace IdbConsole.Tests.Migrations;

public class MessagePackMigrationTests
{
    [Fact]
    public void UpgradesLegacyContractIntoNewSchema()
    {
        var legacy = new LegacyPerson
        {
            FirstName = "Ada",
            LastName = "Lovelace",
            Age = 36
        };

        var payload = MessagePackSerializer.Serialize(legacy, MessagePackSerializerOptions.Standard);

        var migration = MessagePackSerializer.Deserialize<PersonMigration>(payload, MessagePackSerializerOptions.Standard);
        Assert.NotNull(migration);

        var upResult = migration.Up();
        Assert.True(upResult);

        var upgraded = (IPersonV2)migration;
        Assert.Equal("Ada", upgraded.FirstName);
        Assert.Equal("Lovelace", upgraded.LastName);
        Assert.Equal(36, upgraded.Age);
        Assert.Equal("Ada Lovelace", upgraded.DisplayName);
    }

    [Fact]
    public void CanDowngradeAfterUpgrade()
    {
        var legacy = new LegacyPerson
        {
            FirstName = "Grace",
            LastName = "Hopper",
            Age = 85
        };

        var payload = MessagePackSerializer.Serialize(legacy, MessagePackSerializerOptions.Standard);
        var migration = MessagePackSerializer.Deserialize<PersonMigration>(payload, MessagePackSerializerOptions.Standard);

        Assert.True(migration.Up());
        Assert.True(migration.Down());

        var downgraded = (IPersonV1)migration;
        Assert.Equal("Grace", downgraded.FirstName);
        Assert.Equal("Hopper", downgraded.LastName);
        Assert.Equal(85, downgraded.Age);
    }

    [Fact]
    public void SerializesToNewSchemaAfterSuccessfulUpgrade()
    {
        var legacy = new LegacyPerson
        {
            FirstName = "Alan",
            LastName = "Turing",
            Age = 41
        };

        var payload = MessagePackSerializer.Serialize(legacy, MessagePackSerializerOptions.Standard);
        var migration = MessagePackSerializer.Deserialize<PersonMigration>(payload, MessagePackSerializerOptions.Standard);

        Assert.True(migration.Up());

        var upgradedPayload = MessagePackSerializer.Serialize(migration, MessagePackSerializerOptions.Standard);
        var roundTripped = MessagePackSerializer.Deserialize<PersonV2Record>(upgradedPayload, MessagePackSerializerOptions.Standard);

        Assert.Equal("Alan", roundTripped.FirstName);
        Assert.Equal("Turing", roundTripped.LastName);
        Assert.Equal(41, roundTripped.Age);
        Assert.Equal("Alan Turing", roundTripped.DisplayName);
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class LegacyPerson : IPersonV1
    {
        [Key(0)]
        public string FirstName { get; set; } = string.Empty;

        [Key(1)]
        public string LastName { get; set; } = string.Empty;

        [Key(2)]
        public int Age { get; set; }
    }

    internal interface IPersonV1
    {
        string FirstName { get; }
        string LastName { get; }
        int Age { get; }
    }

    internal interface IPersonV2 : IPersonV1
    {
        string DisplayName { get; }
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class PersonV2Record : IPersonV2
    {
        [Key(0)]
        public string FirstName { get; set; } = string.Empty;

        [Key(1)]
        public string LastName { get; set; } = string.Empty;

        [Key(2)]
        public int Age { get; set; }

        [Key(3)]
        public string DisplayName { get; set; } = string.Empty;
    }

    [MessagePackObject(AllowPrivate = true)]
    internal sealed class PersonMigration : IPersonV2, IMigration, IPersonV1
    {
        [Key(0)]
        public string FirstName { get; set; } = string.Empty;

        [Key(1)]
        public string LastName { get; set; } = string.Empty;

        [Key(2)]
        public int Age { get; set; }

        [Key(3)]
        public string DisplayName { get; set; } = string.Empty;

        public bool Up()
        {
            if (string.IsNullOrWhiteSpace(DisplayName))
            {
                DisplayName = $"{FirstName} {LastName}".Trim();
            }

            return true;
        }

        public bool Down()
        {
            DisplayName = string.Empty;
            return true;
        }
    }
}
