# Focused BenchmarkDotNet results

Environment: AMD Ryzen 9 3950X, 16 physical/32 logical cores, .NET SDK 10.0.301,
.NET 10.0.9 x64 RyuJIT, AVX2, BenchmarkDotNet 0.15.8 ShortRun. Each query searches
100,000 deterministic records averaging 64 UTF-16 code units. All methods return the
same ordinal match digest and allocate zero managed bytes per operation.

## Contains queries

| Method | Query | Mean |
|---|---|---:|
| Adaptive managed trigram | selective natural 3-gram (106 hits) | 1.445 µs |
| Adaptive Roaring trigram | selective natural 3-gram (106 hits) | 1.484 µs |
| Packed sorted trigram | selective natural 3-gram (106 hits) | 1.778 µs |
| Native suffix array | selective natural 3-gram (106 hits) | 1.862 µs |
| 1,024-bit signature | selective natural 3-gram (106 hits) | 14.879 µs |
| 256-bit signature | selective natural 3-gram (106 hits) | 328.858 µs |
| Packed arena scan | selective natural 3-gram (106 hits) | 1,381.443 µs |
| .NET string scan | selective natural 3-gram (106 hits) | 1,396.913 µs |
| Adaptive managed trigram | rare 8-char (100 hits) | 1.713 µs |
| Native suffix array | rare 8-char (100 hits) | 1.833 µs |
| Packed sorted trigram | rare 8-char (100 hits) | 2.009 µs |
| Adaptive Roaring trigram | rare 8-char (100 hits) | 4.437 µs |
| 1,024-bit signature | rare 8-char (100 hits) | 26.584 µs |
| 256-bit signature | rare 8-char (100 hits) | 26.661 µs |
| Packed arena scan | rare 8-char (100 hits) | 1,251.089 µs |
| .NET string scan | rare 8-char (100 hits) | 1,355.368 µs |

## Packed key encoding

| Method | Mean | Ratio | Allocated | Native code |
|---|---:|---:|---:|---:|
| Allocating 3-character strings into `HashSet<string>` | 1,377.54 ns | 1.00 | 5,400 B | 4,334 B |
| Pack three UTF-16 code units into `ulong` | 47.25 ns | 0.03 | 0 B | 84 B |

The packed loop is approximately 29.2× faster in this microbenchmark. RyuJIT inlines
the key operation into three 16-bit loads and shifts by 32 and 16 bits (shown as hex
`20` and `10` in BenchmarkDotNet's assembly), followed by OR operations:

```assembly
movzx r10d, word ptr [rdx+rcx*2]
shl   r10, 20
movzx r11d, word ptr [rdx+r11*2]
shl   r11, 10
or    r10, r11
movzx ecx, word ptr [rdx+rcx*2]
or    rcx, r10
```

The full generated reports and disassembly are under
`BenchmarkDotNet.Artifacts/results/` after running `--bdn`.
