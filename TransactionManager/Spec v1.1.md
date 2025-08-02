# 📦 Storage Scope Specification (v 1.1)

**Author**        : Senior Architect / BA  
**Audience**      : Developer building the storage-path-resolution system  
**Phase**        : MVP (Phase 1) with Phase 2 outlook  

---

## 🔍 Purpose

Define a predictable, attribute-driven directory layout that

* keeps interface and concrete folders separate and clear  
* **adds roots for any `[StorageScope]` type used as a property**  
* stays forward-compatible with link files, transactions, and lazy loading (Phase 2)  

---

## 🧱 Core Concepts

```csharp
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Interface,
                Inherited = false, AllowMultiple = false)]
public sealed class StorageScopeAttribute : Attribute { }
