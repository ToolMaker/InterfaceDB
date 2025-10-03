# 📦 Storage + Versioning Specification (v 2.0)

**Audience**: Developers & architects implementing the on-disk store and its upgrade pipeline  
**Goal**: Persist objects in simple, per-type folders while allowing _any_ binary/text format (MessagePack, JSON, ProtoBuf, …) and painless, lazy migrations.

---

## 1. Folder & File Rules

| Rule | Description |
|------|-------------|
| **R1** | Only **concrete classes / structs** marked with `[StorageScope]` get folders. |
| **R2** | Folder name = concrete type name (e.g., `/Person`, `/Alien`). |
| **R3** | Each file holds **one object** wrapped in an **Envelope**; file name = `<Id>.v<SchemaVersion>.<ext>` (`01HA…​.v4.bin`). |
| **R4** | No interface or base-class folders, no nesting, no duplicates. |

> *The file‐name suffix lets batch tools spot legacy versions without opening every file.*

---

## 2. Core Annotations

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, 
                Inherited = false, AllowMultiple = false)]
public sealed class StorageScopeAttribute : Attribute { }
