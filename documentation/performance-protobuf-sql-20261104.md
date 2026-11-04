# Protobuf, MessagePack and SQL performance — 4 November 2026

**Protobuf shrank these InterfaceDB payloads by 43%, but did not improve overall
repository performance.** Durable write latencies were effectively unchanged.
The apparent point and range read gains flipped with engine order. The controlled
TCP comparison found a small point-read difference and slower Protobuf pages.

## Same repository workload against SQL

Two Release runs on Windows, .NET 10.0.14, with reversed engine order on the
second run. Each engine seeded 10,000 complete records, ran 50 measured
queries per shape, and updated and deleted 1,000 records. Both SegmentCore
variants used MetadataOnly, four stripes, mutation WAL and ExplicitFlush.
SQLite used the harness's durable configuration; SQL Server delayed durability
was disabled. SQL Server was local, Developer Edition 16.0.1000.6; SQLite
was 3.53.4. Both rounds passed full-record and reopen checks.

Times below are the **range of the two run medians**, in milliseconds. Ordinal
prefix/suffix controls were measured separately on SegmentCore. The other string
rows use the standard SQL harness query shapes and matching data.

| Operation | SegmentCore MessagePack | SegmentCore Protobuf | SQLite direct | SQLite Dapper | SQL Server |
| --- | ---: | ---: | ---: | ---: | ---: |
| Durable insert | 3.09–3.13 | 3.08 | 3.17 | 3.15–3.17 | 0.23–0.24 |
| Exact query | 0.012–0.040 | 0.012–0.036 | 0.021–0.022 | 0.025–0.027 | 0.12–0.13 |
| Range (948 records) | 3.76–9.02 | 4.82–7.70 | 1.19–1.51 | 1.57–2.15 | 3.03–3.74 |
| Broad conjunction (3,251 records) | 11.35–12.38 | 12.43–12.76 | 3.89–3.90 | 4.95–5.02 | 5.05–5.09 |
| Prefix | 21.59–23.61 | 25.63–26.23 | 1.15–1.44 | 1.47–2.07 | 3.84–3.88 |
| Suffix | 20.06–20.51 | 22.81–24.95 | 1.02–1.03 | 1.03–1.05 | 15.37–15.49 |
| Contains (39 records) | 0.23–0.26 | 0.23–0.26 | 0.89–0.91 | 0.91–0.94 | 23.60–23.65 |
| Ordinal prefix | 3.00–3.10 | 3.33–3.36 | — | — | — |
| Ordinal suffix | 0.16–0.17 | 0.18 | — | — | — |
| Update indexed fields | 3.16 | 3.08–3.21 | 3.07–3.21 | 3.13–3.18 | 0.23–0.24 |
| Delete by ID | 3.15–3.17 | 3.07–3.14 | 3.09–3.20 | 3.09–3.12 | 0.21–0.22 |

- Changing serializers did not close the durable write gap to SQL Server.
  This measures the configured storage and commit paths, including different
  flush methods. InterfaceDB update and delete also include the entity lookup;
  the SQL adapters operate by primary key.
- Exact and range queries favored the second SegmentCore variant in both rounds.
  For range, Protobuf went from 4.82 ms against MessagePack's 9.02 ms to 7.70 ms
  against MessagePack's 3.76 ms. SQLite and SQL Server were faster on range and
  broad conjunction in both rounds.
- The indexed contains workload was faster on both SegmentCore formats. Protobuf
  did not create that advantage. Prefix, suffix and broad conjunction were slower
  with Protobuf in both runs; the difference on the ordinal prefix was small
  relative to the default prefix scan.

## Codec and TCP reads

The same six-field benchmark contract was mapped in both codecs. The runner
verified exact payload bytes on direct and TCP reads, and checked every entity
field plus envelope key, version and revision. Average envelope size was **269.8
bytes with MessagePack and 153.0 bytes with Protobuf** (512 records, excluding
transport framing). Encode/decode timings come from batches of 128, divided by
128 to report per-record time. TCP measurements use an established loopback
mutual TLS session and opaque SegmentCore storage. They include client decoding
where indicated; they exclude connection setup and actual HTTP/2 gRPC traffic.

Four processes with runtime-default tiering (two orders, twice each), 500 samples
per operation. Ranges are the lowest and highest run medians, in microseconds:

| Operation | MessagePack | Protobuf |
| --- | ---: | ---: |
| Encode one envelope | 1.67–1.85 | 10.17–10.67 |
| Decode one envelope | 2.54–2.91 | 1.28–4.02 |
| Local point read, bytes | 2.40–5.20 | 2.30–4.30 |
| Local point read, object | 4.60–9.20 | 3.40–7.70 |
| Local 128-record page, objects | 533–1051 | 398–834 |
| TCP point read, bytes | 52–150 | 53–119 |
| TCP point read, object | 58–155 | 57–144 |
| TCP 128-record page, objects | 737–1125 | 570–1110 |

The format running second generally had lower read latency, even for raw reads
that invoke no decoder. Decode and TCP speed rankings from those runs therefore
cannot be attributed to the format. Encoding was slower for Protobuf in every
order, with about 2,621 bytes allocated per envelope versus 328 for MessagePack.

A sensitivity check ran four more processes in forward/reverse/reverse/forward
order with `DOTNET_TieredCompilation=0` for those processes. These are controlled
measurements, not a recommendation to change production runtime settings:

| Operation (µs) | MessagePack | Protobuf |
| --- | ---: | ---: |
| Encode one envelope | 0.63–0.65 | 7.33–7.59 |
| Decode one envelope | 0.81–0.86 | 2.02–2.05 |
| Local point read, bytes | 2.60–2.70 | 2.60–2.70 |
| Local point read, object | 3.70–3.90 | 5.00–5.20 |
| Local 128-record page, objects | 438–447 | 591–597 |
| TCP point read, bytes | 71–78 | 71–72 |
| TCP point read, object | 77–85 | 79–82 |
| TCP 128-record page, objects | 680–717 | 797–819 |

With tiering disabled, Protobuf encoded and decoded more slowly. It took about
0.80–0.82 ms to read and decode 128 records over TCP versus 0.68–0.72 ms with
MessagePack. Process-wide TCP page allocation was about 2,610 bytes per record
for Protobuf and 3,140 bytes for MessagePack, roughly 17% lower
for Protobuf despite higher decoder allocation. The allocation figure includes
both endpoints in this same-process harness.

Keep MessagePack as the performance baseline for this workload. Protobuf offers
a smaller representation and schema interoperability; smaller envelopes alone
did not make these small local requests faster. Larger payloads, constrained
networks, or a future gRPC endpoint need their own measurements.

## Reproduction and checks

See the [benchmark commands](../StorageBenchmarks.Transactions/ProductionGate/SQL_COMPARISON.md#comparing-envelope-serializers).
The final Release solution build, public source boundary check and materialization
self-test passed. Full SQL reports, the runtime-default and tiering-disabled samples,
and the SQL smoke check are retained locally under
`C:\Temp\InterfaceDBSerializerPerf_20261104`.
