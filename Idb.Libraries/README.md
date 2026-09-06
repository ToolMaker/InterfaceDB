# InterfaceDB

Interface-first object repositories and embedded object storage for .NET 10, licensed under MIT.

`InterfaceDB` provides typed `IRepository<T>` APIs, MessagePack envelope serialization, file-per-object storage, indexes, repository notifications, revision-based write-conflict checks and optional retained editing. It is an embedded library; a database host does not authenticate application users on its own.

`InterfaceDB.Storage.SegmentCore` is the separate MIT append-only segmented-storage provider. Use only providers appropriate to your application and qualify durability behavior on your target filesystem/device.

```shell
dotnet add package InterfaceDB --prerelease
dotnet add package InterfaceDB.Storage.SegmentCore --prerelease
```

## Preview.7 maintenance APIs

This source includes opt-in local compaction policies, activity/window hints, shared maintenance I/O budgets and bounded maintenance summaries. They are separate from logical entity Created/Changed/Deleted notifications. Constructing a policy does not start a background job, and physical relocation does not change an entity's revision timestamp.

Segment Core's optional independent cleanup output preserves retained editing baselines and uses durable checkpoints before retiring old files. Online compaction requires the mutation-WAL protocol. Independent output is an explicit durable format 1.1 opt-in; default stores remain format 1.0. Take and verify a backup before enabling it. Older runtimes reject the upgraded format, and changing the compaction setting back does not downgrade a store.

Performance is workload-dependent. Copying, metadata probes, checkpoints and reader/handle retirement have costs; no zero-overhead or lock-free publication guarantee is made. File-per-object storage has no segment-compaction operation.

These capabilities are included in preview.7. New writes use IDB6; preview.6 cannot read them. Stop old writers, rebuild custom providers/envelopes/serializers, and retain a complete pre-upgrade backup. Readers accept IDB2 through IDB6. Review the matching release notes before upgrading; APIs and storage formats may still change before stable 1.0.0.

Source and usage documentation: [InterfaceDB repository](https://github.com/ToolMaker/InterfaceDB), including `documentation/compaction-maintenance.md`, `documentation/segment-core-storage-provider.md`, `documentation/envelope-revisions.md` and `documentation/retained-editing.md`.
