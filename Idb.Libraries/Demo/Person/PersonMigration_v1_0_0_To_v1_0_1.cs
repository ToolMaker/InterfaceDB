using System;
using Idb.Libraries.Abstraction.Services.Migrations;
using Current = Idb.Libraries.Demo.Person.v1_0_1;
using Legacy = Idb.Libraries.Demo.Person.v1_0_0;

namespace Idb.Libraries.Demo.Person;

/// <summary>
/// Sample migration that upgrades <see cref="Legacy.IPerson"/> payloads to <see cref="Current.IPerson"/>.
/// </summary>
public sealed class PersonMigration_v1_0_0_To_v1_0_1 :
    Legacy.IPerson,
    Current.IPerson,
    IMigration
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Age { get; set; }
    public string Department { get; set; } = "Unassigned";

    public string SourceVersion => "1.0.0";

    public string TargetVersion => "1.0.1";

    public bool Up()
    {
        if (string.IsNullOrWhiteSpace(Department))
        {
            Department = "General";
        }

        return true;
    }

    public bool Down()
    {
        Department = "Unassigned";
        return true;
    }
}

