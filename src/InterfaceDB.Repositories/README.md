# InterfaceDB.Repositories

## Preview.18

Use matching `1.0.0-preview.18` versions across InterfaceDB packages. See the
[release and upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.18/documentation/preview18-release-notes.md).

Repository composition, offline synchronization, peer replication and resumable provider-to-provider storage migration for InterfaceDB.

Part of [InterfaceDB](https://github.com/ToolMaker/InterfaceDB), for .NET 10.
All implemented modules are free and MIT licensed. This is a preview release.

```sh
dotnet add package InterfaceDB.Repositories --version 1.0.0-preview.18
```

Use matching versions for all InterfaceDB packages. Configure implementations at the
composition root and consume repository/serializer abstractions in application code.

See the [integration guide](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/local-remote-repositories.md)
and [upgrade notes](https://github.com/ToolMaker/InterfaceDB/blob/v1.0.0-preview.17/documentation/preview17-release-notes.md).

For compatibility this package retains assembly `InterfaceDB.Commercial.Repositories.dll` and existing
namespaces, including `Idb.Commercial.Repositories`. A legacy `Commercial` name does not imply a paid feature
or a different license. Remove former source/DLL references when adopting the package;
do not load two implementations of the same assembly.

Object serialization is supplied through the existing DI abstractions. TCP carries the
configured object envelopes; package selection does not select a second object serializer.

## License and support

Licensed under MIT; the standard license is included. If InterfaceDB is useful to you,
please consider [sponsoring maintenance](https://github.com/ToolMaker/InterfaceDB/blob/main/SPONSORING.md).
Sponsorship is voluntary and adds no conditions to the license or feature access.
