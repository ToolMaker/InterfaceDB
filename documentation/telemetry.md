# Logs, metrics and traces

Implemented in source on 19 October 2026 and included in preview.12, published on 21 October.
Telemetry is opt-in and uses standard .NET `Meter`, `ActivitySource` and `ILogger` APIs. No
external exporter, collector, account or paid service is enabled by the library.

## Register and own one dispatcher

```csharp
using Idb.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

services.AddInterfaceDbTelemetry(new DatabaseTelemetryOptions
{
    QueueCapacity = 1024,
    Metrics = true,
    Traces = true,
    TraceSampleRatio = 0.01,
    TcpTraces = false,
    StorageTraces = false
});
```

At the application's composition root, resolve `IDatabaseTelemetry` and pass that same borrowed
service through the options for each participating component:

| Component | Configuration |
| --- | --- |
| Native repository, including File storage | `StoragePolicy.Telemetry` |
| Direct SegmentCore storage | `SegmentCoreOptions.Telemetry` |
| TCP client and typed repository | `TcpStoreClientOptions.Telemetry`, used by `TcpRepositoryOptions.Connection` |
| TCP host | `TcpStoreHostOptions.Telemetry` |
| Encoded SegmentCore adapter / versioned host storage | Optional `telemetry:` argument to `OpenAsync` |

For native SegmentCore repositories, `StoragePolicy.Telemetry` takes precedence over the underlying
store option. Typed TCP repositories forward the connection service to their native repository
boundary unless the storage policy explicitly supplies another service. Prefer one service per
process. Sharing it does not transfer ownership to a repository or host. Dispose repositories and
hosts first, then asynchronously dispose the DI provider so the dispatcher can drain.

Leaving the options null is the default disabled path. `Enabled = false` also disables a registered
service. Custom `IDatabaseTelemetry` implementations must provide bounded, nonblocking methods;
library exception guards cannot prevent an application-supplied implementation from blocking.

The runnable TCP host accepts `"TelemetryEnabled": true` in its settings. This enables the bounded
dispatcher, host and SegmentCore metrics, and sanitized JSON warning/error logs on stderr. Enable
`"TelemetryTcpTraces": true` for TCP spans; `"TelemetryTraceSampleRatio"` defaults to `0.01`.
`TelemetryEnabled` defaults to false. The same settings work in Docker; collecting metrics/traces
still requires a listener or diagnostics collector chosen by the operator. It does not open a
metrics HTTP port. Existing console/startup messages are outside this structured telemetry stream.

## Metrics and coverage

Subscribe to meter **`InterfaceDB.Operations`**, version `1.0.0`.

| Instrument | Unit / meaning |
| --- | --- |
| `interfacedb.operation.completed` | Completed observations, including failures, cancellations and early enumeration disposal |
| `interfacedb.operation.duration` | Seconds at the observed boundary |
| `interfacedb.operation.bytes` | Successful client request frames, host reply frames, or direct storage-save payload bytes |
| `interfacedb.connections.active` | Accepted TCP host connections, including handshakes |
| `interfacedb.subscriptions.active` | Registered TCP host subscriptions |
| `interfacedb.telemetry.dropped` | Observations discarded when the dispatcher queue is full |
| `interfacedb.telemetry.export_failures` | Listener/logger exceptions isolated by the dispatcher |
| `interfacedb.process.managed_memory` | Runtime managed heap bytes; no forced GC |
| `interfacedb.process.available_memory` | Runtime-reported memory budget, including applicable container limits; **not free RAM** |

Operation labels are the closed enums `component`, `operation`, `outcome` and `error.code`.
No aliases, types, endpoints, paths, keys, user IDs, tokens or exception text become labels.
Unknown enum values map to `Unknown`. Use component and operation filters when calculating rates:
one application operation can produce observations at several layers. Summing all components
does not measure distinct application requests. Byte counts are not disk allocation, volume free
space, retained data size, or a count of all physical writes. A TCP reply can be measured as sent
even when the client disconnects before receiving it.

Coverage includes native repository initialization/save/batch/delete/read/query/search; File
physical envelope reads and writes; SegmentCore open/read/bulk-read/write/delete/transactions,
checkpoint, compaction, backup and verification; TCP connect, requests/replies and subscription
lifecycle. Typed TCP `Execute` measures the full facade operation, including connection admission;
the native repository inside it reports the specific CRUD/query operation. Enumeration is measured
once per enumeration and reports `Incomplete` when disposed before exhaustion. It does not emit a
repository metric for every returned row. Lower storage operations have their own observations.

Storage health is visible through maintenance outcomes, latency, safe failure categories and byte
throughput. Runtime memory instruments are collected when a listener requests them. The library
does not scan directories, query the container engine, or gather host CPU/disk capacity on database
calls. Use platform/runtime monitoring for those additional process and volume measurements.

The existing `InterfaceDB.Storage.SegmentCore` meter is retained, including startup stages and
maintenance/storage-byte measurements. Its legacy callbacks are synchronous. The new dispatcher
does not subscribe to that meter: attach only nonblocking listeners if collecting it directly.

## Trace boundaries and correlation

Subscribe to activity source **`InterfaceDB.Operations`**. Start the application's operation activity
before calling the repository. InterfaceDB captures its W3C trace/span IDs at operation entry and
creates a completed child span on the dispatcher with the original start/end timestamps. There is
no live InterfaceDB activity on the calling thread. Repository and TCP spans share the application
parent; they are siblings, rather than nested live scopes. Their trace IDs also appear in structured
logs when an application parent exists. With no parent, sampled spans are independent roots.

The default trace sampling ratio is **1%**, at repository boundaries only. Listeners can further
decline a span. `TcpTraces = true` adds TCP client/host spans; `StorageTraces = true` adds storage
spans, both at the configured ratio. Metrics still cover TCP and storage when their spans are off.
Sampling affects traces, not operation metrics or failure logs.
This library sampler is per observation, so it does not promise complete traces across all layers.

There is **no client-to-server trace propagation**. The TCP wire format, authentication and account
isolation are unchanged. Server activities use server-process context; they do not inherit a remote
client's trace ID. Application `Activity` baggage and vendor tracestate are never propagated to the
dispatcher. Cross-process trace context requires a separately versioned protocol extension.

## Logs and fault isolation

Provide `ILogger<DatabaseTelemetry>` through DI to collect sanitized failures across repository
reads/writes/queries, storage operations, TCP transport, notification callbacks and background
maintenance. Successful operations and normal cancellation produce no log entries. Information
and Debug logs are not emitted. `MinimumLogLevel` defaults to `Warning`; it can be raised to
`Error`, `Critical` or `None`. Recoverable failures and denied/invalid/conflicting requests are
warnings; other failed operations are errors. This telemetry currently emits no Critical events.
Each log contains fixed component,
operation, outcome/error category, duration and optional application trace ID. Exception objects,
messages, stacks, record bodies, credentials and personal data are not passed to the logger.
The same exception instance is logged once per dispatcher as it crosses observed boundaries;
each boundary still emits its metrics. Weak exception keys are used for deduplication, with no
exception object in the queue. The first observed failure determines its logged component and
severity. Wrapped exceptions and separate processes can produce separate logs. Existing
application callbacks such as `TcpStoreHostOptions.OnError` retain their existing
semantics and responsibility for sensitive data; avoid logging the same event from both sinks.

Producers enqueue small observations with `TryWrite`; listeners and logger callbacks run on one
background dispatcher with execution-context flow suppressed. Queue capacity is bounded (default
1,024, allowed 1–65,536). A full queue drops telemetry, never database work. Gauges update separately
and do not depend on queued observations. Metrics and logs are best effort, not an audit ledger.
A full queue can drop a failure log too; deduplication does not retry that discarded log.

Throwing consumers are isolated. A consumer that blocks stalls the dispatcher, eventually causing
drops, but does not execute on the database call. Shutdown waits up to `ShutdownTimeout` (default
two seconds). .NET cannot forcibly stop an arbitrary blocked callback; it may outlive that timeout.
Check `DroppedRecords`, `ExportFailures` and `Completion` when diagnosing shutdown/export health.

## Verification and performance

See [measurement and validation evidence](telemetry-performance-20261019.md). The tests exercise
throwing and blocked consumers, queue saturation, bounded disposal, original exception identity,
lazy queries, DI disposal, safe labels, trace parents/baggage exclusion, TLS CRUD and provider
composition. Performance depends on enabled observers and exporters; it is not zero-cost.

The design follows the standard [metrics instrumentation APIs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation)
and [ActivitySource APIs](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).
