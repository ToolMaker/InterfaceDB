# Contains-index benchmark results
Generated 2026-08-23 08:07:52 +02:00 on .NET 10.0.9.

All query results are verified against ordinal, case-sensitive `string.Contains`. Query latency includes candidate verification and ID digesting, but excludes InterfaceDB physical-key materialization. P50/P95 are warm measurements; cold latency follows a 64 MiB cache-thrashing pass. Memory deltas are process observations and should be treated as approximate, especially for native libraries.

## Fastest warm query by dataset and query

| Dataset | Query | Matches | Winner | P50 µs | Runner-up | P50 µs |
|---|---:|---:|---|---:|---|---:|
| random-10000x64 | empty | 10 000 | LIFTI whole-token wildcard | 12,39 | WaveletMatrix FM-index (full SA) | 18,17 |
| random-10000x64 | one-char | 7 794 | Packed trigram / adaptive Roaring | 138,39 | Packed trigram / adaptive sparse+bitmap | 139,54 |
| random-10000x64 | two-char | 359 | NLibSais native suffix array | 16,39 | Segmented NLibSais SA + delta (4096) | 27,20 |
| random-10000x64 | absent-3 | 0 | WaveletMatrix FM-index (sample 32) | 0,02 | Packed 48-bit trigram / sorted postings | 0,07 |
| random-10000x64 | rare-8 | 10 | Packed 48-bit trigram / sorted postings | 0,85 | Packed trigram / adaptive Roaring | 1,38 |
| random-10000x64 | one-percent-16 | 100 | NLibSais native suffix array | 5,75 | Bit-sliced signature 256-bit/3h | 6,04 |
| random-10000x64 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 14,08 | Bit-sliced signature 256-bit/3h | 16,19 |
| random-10000x64 | natural-broad | 8 | Naive string/HashSet trigram | 0,16 | Packed trigram / adaptive Roaring | 0,17 |
| namesandemails-10000x72 | empty | 10 000 | Packed trigram / adaptive sparse+bitmap | 46,45 | Naive string/HashSet trigram | 46,56 |
| namesandemails-10000x72 | one-char | 9 629 | Packed 48-bit trigram / sorted postings | 88,26 | .NET string.Contains scan | 88,59 |
| namesandemails-10000x72 | two-char | 3 395 | Packed 48-bit trigram / sorted postings | 104,77 | Packed trigram / adaptive Roaring | 104,86 |
| namesandemails-10000x72 | absent-3 | 0 | Packed trigram / adaptive Roaring | 0,03 | Packed trigram / adaptive sparse+bitmap | 0,03 |
| namesandemails-10000x72 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 0,30 | Packed 48-bit trigram / sorted postings | 0,38 |
| namesandemails-10000x72 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 2,83 | Packed 48-bit trigram / sorted postings | 3,20 |
| namesandemails-10000x72 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 13,98 | Bit-sliced signature 1024-bit/3h | 15,78 |
| namesandemails-10000x72 | natural-broad | 2 529 | Packed trigram / adaptive sparse+bitmap | 44,66 | Bit-sliced signature 1024-bit/3h | 46,44 |
| urls-10000x128 | empty | 10 000 | Packed trigram / adaptive sparse+bitmap | 46,41 | Packed trigram / adaptive Roaring | 46,57 |
| urls-10000x128 | one-char | 10 000 | .NET string.Contains scan | 90,27 | Packed 48-bit trigram / sorted postings | 91,04 |
| urls-10000x128 | two-char | 10 000 | Naive string/HashSet trigram | 89,33 | .NET string.Contains scan | 89,36 |
| urls-10000x128 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed trigram / adaptive Roaring | 0,03 |
| urls-10000x128 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 0,33 | Packed 48-bit trigram / sorted postings | 0,43 |
| urls-10000x128 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 3,03 | Packed 48-bit trigram / sorted postings | 3,49 |
| urls-10000x128 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 16,14 | Bit-sliced signature 1024-bit/3h | 17,57 |
| urls-10000x128 | natural-broad | 10 000 | .NET string.Contains scan | 90,99 | Packed UTF-16 arena scan | 93,27 |
| logs-10000x256 | empty | 10 000 | Naive string/HashSet trigram | 47,83 | Packed 48-bit trigram / sorted postings | 47,88 |
| logs-10000x256 | one-char | 2 000 | Packed UTF-16 arena scan | 98,13 | Packed trigram / adaptive Roaring | 98,99 |
| logs-10000x256 | two-char | 2 000 | Packed UTF-16 arena scan | 135,89 | Packed trigram / adaptive Roaring | 138,68 |
| logs-10000x256 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed trigram / adaptive Roaring | 0,03 |
| logs-10000x256 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 0,37 | Packed 48-bit trigram / sorted postings | 0,44 |
| logs-10000x256 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 3,42 | Packed 48-bit trigram / sorted postings | 3,68 |
| logs-10000x256 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 21,47 | Bit-sliced signature 1024-bit/3h | 23,39 |
| logs-10000x256 | natural-broad | 2 000 | Packed trigram / adaptive sparse+bitmap | 29,10 | Bit-sliced signature 1024-bit/3h | 30,97 |
| repetitive-10000x128 | empty | 10 000 | Packed trigram / adaptive sparse+bitmap | 46,20 | Naive string/HashSet trigram | 46,41 |
| repetitive-10000x128 | one-char | 10 000 | .NET string.Contains scan | 70,97 | Packed 48-bit trigram / sorted postings | 71,80 |
| repetitive-10000x128 | two-char | 9 712 | Packed trigram / adaptive sparse+bitmap | 176,80 | Packed 48-bit trigram / sorted postings | 177,69 |
| repetitive-10000x128 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed trigram / adaptive Roaring | 0,03 |
| repetitive-10000x128 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 0,32 | Packed 48-bit trigram / sorted postings | 0,40 |
| repetitive-10000x128 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 2,98 | Packed 48-bit trigram / sorted postings | 3,39 |
| repetitive-10000x128 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 16,16 | Bit-sliced signature 1024-bit/3h | 18,33 |
| repetitive-10000x128 | natural-broad | 9 341 | Packed trigram / adaptive sparse+bitmap | 242,28 | Bit-sliced signature 1024-bit/3h | 243,88 |
| unicode-10000x72 | empty | 10 000 | Packed trigram / adaptive Roaring | 46,60 | Packed trigram / adaptive sparse+bitmap | 46,62 |
| unicode-10000x72 | one-char | 5 730 | Packed trigram / adaptive sparse+bitmap | 125,17 | Packed trigram / adaptive Roaring | 125,98 |
| unicode-10000x72 | two-char | 5 730 | Packed trigram / adaptive Roaring | 124,99 | Naive string/HashSet trigram | 125,97 |
| unicode-10000x72 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed trigram / adaptive Roaring | 0,03 |
| unicode-10000x72 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 0,29 | Packed 48-bit trigram / sorted postings | 0,37 |
| unicode-10000x72 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 2,80 | Packed 48-bit trigram / sorted postings | 3,15 |
| unicode-10000x72 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 14,05 | Bit-sliced signature 256-bit/3h | 15,68 |
| unicode-10000x72 | natural-broad | 5 683 | Packed 48-bit trigram / sorted postings | 129,48 | Packed trigram / adaptive Roaring | 129,95 |
| random-100000x64 | empty | 100 000 | LIFTI whole-token wildcard | 133,34 | NLibSais native suffix array | 144,10 |
| random-100000x64 | one-char | 77 539 | Lucene.Net exact trigram terms | 1 556,38 | Packed UTF-16 arena scan | 1 569,27 |
| random-100000x64 | two-char | 3 774 | Segmented NLibSais SA + delta (4096) | 174,83 | NLibSais native suffix array | 201,48 |
| random-100000x64 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed trigram / adaptive Roaring | 0,03 |
| random-100000x64 | rare-8 | 100 | Packed trigram / adaptive sparse+bitmap | 1,85 | NLibSais native suffix array | 2,11 |
| random-100000x64 | one-percent-16 | 1 000 | Packed trigram / adaptive Roaring | 25,37 | Packed trigram / adaptive sparse+bitmap | 26,94 |
| random-100000x64 | ten-percent-8 | 10 000 | Packed trigram / adaptive sparse+bitmap | 194,04 | Bit-sliced signature 256-bit/3h | 211,44 |
| random-100000x64 | natural-broad | 106 | Packed trigram / adaptive sparse+bitmap | 1,56 | Packed trigram / adaptive Roaring | 1,68 |
| repetitive-100000x128 | empty | 100 000 | Packed UTF-16 arena scan | 503,15 | Packed 48-bit trigram / sorted postings | 836,84 |
| repetitive-100000x128 | one-char | 100 000 | Bit-sliced signature 256-bit/3h | 2 483,03 | Packed 48-bit trigram / sorted postings | 2 527,40 |
| repetitive-100000x128 | two-char | 96 968 | Packed 48-bit trigram / sorted postings | 2 628,28 | Packed trigram / adaptive Roaring | 2 635,60 |
| repetitive-100000x128 | absent-3 | 0 | Packed 48-bit trigram / sorted postings | 0,03 | Packed trigram / adaptive sparse+bitmap | 0,03 |
| repetitive-100000x128 | rare-8 | 100 | Packed trigram / adaptive sparse+bitmap | 2,04 | Packed 48-bit trigram / sorted postings | 2,49 |
| repetitive-100000x128 | one-percent-16 | 1 000 | Packed trigram / adaptive Roaring | 22,61 | Packed trigram / adaptive sparse+bitmap | 27,88 |
| repetitive-100000x128 | ten-percent-8 | 10 000 | Packed trigram / adaptive sparse+bitmap | 163,64 | Bit-sliced signature 256-bit/3h | 181,73 |
| repetitive-100000x128 | natural-broad | 93 176 | Packed UTF-16 arena scan | 2 931,37 | .NET string.Contains scan | 3 548,17 |
| random-10000x2048 | empty | 10 000 | Packed UTF-16 arena scan | 50,12 | Packed 48-bit trigram / sorted postings | 88,68 |
| random-10000x2048 | one-char | 10 000 | Packed trigram / adaptive sparse+bitmap | 206,22 | Packed 48-bit trigram / sorted postings | 208,28 |
| random-10000x2048 | two-char | 7 042 | Bit-sliced signature 1024-bit/3h | 2 179,28 | Packed 48-bit trigram / sorted postings | 2 194,35 |
| random-10000x2048 | absent-3 | 0 | Packed 48-bit trigram / sorted postings | 0,03 | Packed trigram / adaptive sparse+bitmap | 0,03 |
| random-10000x2048 | rare-8 | 10 | Packed trigram / adaptive sparse+bitmap | 1,19 | Packed 48-bit trigram / sorted postings | 1,26 |
| random-10000x2048 | one-percent-16 | 100 | Packed trigram / adaptive sparse+bitmap | 10,83 | Packed 48-bit trigram / sorted postings | 11,41 |
| random-10000x2048 | ten-percent-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 94,77 | Packed 48-bit trigram / sorted postings | 100,79 |
| random-10000x2048 | natural-broad | 351 | Packed trigram / adaptive sparse+bitmap | 31,03 | Packed 48-bit trigram / sorted postings | 32,05 |
| random-1000000x64 | empty | 1 000 000 | Packed UTF-16 arena scan | 4 964,75 | Bit-sliced signature 256-bit/3h | 15 341,00 |
| random-1000000x64 | one-char | 778 665 | Packed UTF-16 arena scan | 15 994,80 | .NET string.Contains scan | 16 491,20 |
| random-1000000x64 | two-char | 38 622 | Packed UTF-16 arena scan | 13 371,80 | Packed trigram / adaptive sparse+bitmap | 13 915,40 |
| random-1000000x64 | absent-3 | 0 | Packed trigram / adaptive sparse+bitmap | 0,03 | Packed 48-bit trigram / sorted postings | 0,04 |
| random-1000000x64 | rare-8 | 1 000 | Packed trigram / adaptive sparse+bitmap | 18,19 | Packed 48-bit trigram / sorted postings | 22,59 |
| random-1000000x64 | one-percent-16 | 10 000 | Packed trigram / adaptive sparse+bitmap | 301,81 | Packed 48-bit trigram / sorted postings | 324,21 |
| random-1000000x64 | ten-percent-8 | 100 000 | Packed trigram / adaptive sparse+bitmap | 2 390,03 | Bit-sliced signature 256-bit/3h | 2 425,93 |
| random-1000000x64 | natural-broad | 982 | Packed trigram / adaptive sparse+bitmap | 22,99 | Packed 48-bit trigram / sorted postings | 29,56 |

## Build and retained-memory snapshot

| Dataset | Index | Build ms | Managed Δ MiB | Private Δ MiB |
|---|---|---:|---:|---:|
| logs-10000x256 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| logs-10000x256 | Packed UTF-16 arena scan | 1,1 | 5,0 | 0,0 |
| logs-10000x256 | Bit-sliced signature 256-bit/3h | 65,1 | 0,4 | 0,0 |
| logs-10000x256 | Bit-sliced signature 1024-bit/3h | 67,5 | 1,3 | 1,0 |
| logs-10000x256 | Naive string/HashSet trigram | 492,3 | 15,5 | 29,2 |
| logs-10000x256 | Packed 48-bit trigram / sorted postings | 606,1 | 13,2 | 9,6 |
| logs-10000x256 | Packed trigram / adaptive sparse+bitmap | 625,4 | 13,6 | 4,4 |
| logs-10000x256 | Packed trigram / adaptive Roaring | 634,1 | 13,2 | 24,9 |
| namesandemails-10000x72 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| namesandemails-10000x72 | Packed UTF-16 arena scan | 0,4 | 1,5 | 0,1 |
| namesandemails-10000x72 | Bit-sliced signature 1024-bit/3h | 18,9 | 1,3 | 1,1 |
| namesandemails-10000x72 | Bit-sliced signature 256-bit/3h | 20,1 | 0,4 | 4,2 |
| namesandemails-10000x72 | Naive string/HashSet trigram | 99,4 | 8,2 | 13,6 |
| namesandemails-10000x72 | Packed 48-bit trigram / sorted postings | 120,7 | 6,2 | 0,4 |
| namesandemails-10000x72 | Packed trigram / adaptive sparse+bitmap | 137,9 | 8,0 | 2,8 |
| namesandemails-10000x72 | Packed trigram / adaptive Roaring | 145,0 | 7,6 | 2,3 |
| random-1000000x64 | .NET string.Contains scan | 1,8 | 7,6 | 7,6 |
| random-1000000x64 | Packed UTF-16 arena scan | 73,2 | 131,5 | 125,1 |
| random-1000000x64 | Bit-sliced signature 256-bit/3h | 1 866,0 | 38,3 | 0,0 |
| random-1000000x64 | Bit-sliced signature 1024-bit/3h | 2 368,6 | 129,9 | 0,0 |
| random-1000000x64 | Packed 48-bit trigram / sorted postings | 15 637,7 | 251,3 | 607,5 |
| random-1000000x64 | Packed trigram / adaptive sparse+bitmap | 16 032,5 | 252,4 | 243,9 |
| random-100000x64 | .NET string.Contains scan | 0,3 | 0,8 | 0,8 |
| random-100000x64 | Packed UTF-16 arena scan | 6,1 | 13,1 | 12,3 |
| random-100000x64 | Bit-sliced signature 256-bit/3h | 179,6 | 3,8 | 0,0 |
| random-100000x64 | Bit-sliced signature 1024-bit/3h | 200,9 | 13,0 | 9,2 |
| random-100000x64 | Segmented NLibSais SA + delta (4096) | 234,7 | 65,5 | 38,1 |
| random-100000x64 | NLibSais native suffix array | 296,1 | 64,8 | 75,9 |
| random-100000x64 | LIFTI whole-token wildcard | 856,3 | 88,4 | 10,0 |
| random-100000x64 | Packed trigram / adaptive Roaring | 1 422,1 | 30,7 | 0,0 |
| random-100000x64 | Packed 48-bit trigram / sorted postings | 1 433,1 | 28,4 | 91,3 |
| random-100000x64 | Packed trigram / adaptive sparse+bitmap | 1 516,9 | 30,8 | 54,2 |
| random-100000x64 | Lucene.Net exact trigram terms | 2 832,3 | 20,9 | 0,0 |
| random-10000x2048 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| random-10000x2048 | Packed UTF-16 arena scan | 13,3 | 39,2 | 39,7 |
| random-10000x2048 | Bit-sliced signature 256-bit/3h | 601,0 | 0,4 | 0,0 |
| random-10000x2048 | Bit-sliced signature 1024-bit/3h | 644,8 | 1,3 | 0,0 |
| random-10000x2048 | Packed trigram / adaptive sparse+bitmap | 4 590,6 | 80,6 | 114,9 |
| random-10000x2048 | Packed 48-bit trigram / sorted postings | 4 878,7 | 80,5 | 222,8 |
| random-10000x64 | .NET string.Contains scan | 0,1 | 0,1 | 0,0 |
| random-10000x64 | Packed UTF-16 arena scan | 3,5 | 1,3 | 0,7 |
| random-10000x64 | Bit-sliced signature 1024-bit/3h | 23,3 | 1,3 | 2,4 |
| random-10000x64 | Segmented NLibSais SA + delta (4096) | 24,5 | 6,6 | 6,5 |
| random-10000x64 | NLibSais native suffix array | 34,2 | 6,5 | 8,6 |
| random-10000x64 | Bit-sliced signature 256-bit/3h | 52,7 | 0,4 | 0,6 |
| random-10000x64 | LIFTI whole-token wildcard | 91,8 | 9,4 | 13,9 |
| random-10000x64 | Packed trigram / adaptive sparse+bitmap | 165,6 | 8,6 | 13,0 |
| random-10000x64 | Packed 48-bit trigram / sorted postings | 166,2 | 6,1 | 0,0 |
| random-10000x64 | Packed trigram / adaptive Roaring | 172,0 | 8,6 | 10,9 |
| random-10000x64 | Naive string/HashSet trigram | 178,6 | 8,1 | 29,7 |
| random-10000x64 | WaveletMatrix FM-index (sample 32) | 190,7 | 11,8 | 24,0 |
| random-10000x64 | WaveletMatrix FM-index (full SA) | 220,1 | 22,6 | 66,3 |
| random-10000x64 | CaseON managed suffix array | 364,7 | 9,0 | 15,3 |
| random-10000x64 | Lucene.Net exact trigram terms | 1 290,7 | 2,6 | 0,0 |
| repetitive-100000x128 | .NET string.Contains scan | 0,1 | 0,8 | 0,0 |
| repetitive-100000x128 | Packed UTF-16 arena scan | 11,1 | 25,4 | 0,0 |
| repetitive-100000x128 | Packed trigram / adaptive sparse+bitmap | 127,6 | 1,7 | 0,0 |
| repetitive-100000x128 | Packed 48-bit trigram / sorted postings | 135,0 | 13,6 | 26,7 |
| repetitive-100000x128 | Packed trigram / adaptive Roaring | 147,0 | 0,8 | 2,3 |
| repetitive-100000x128 | Bit-sliced signature 256-bit/3h | 172,4 | 3,8 | 2,6 |
| repetitive-100000x128 | Bit-sliced signature 1024-bit/3h | 181,6 | 13,0 | 12,5 |
| repetitive-10000x128 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| repetitive-10000x128 | Packed UTF-16 arena scan | 1,0 | 2,5 | 2,3 |
| repetitive-10000x128 | Packed trigram / adaptive sparse+bitmap | 13,1 | 0,2 | 0,1 |
| repetitive-10000x128 | Packed 48-bit trigram / sorted postings | 13,4 | 1,4 | 1,2 |
| repetitive-10000x128 | Packed trigram / adaptive Roaring | 15,8 | 0,1 | 0,7 |
| repetitive-10000x128 | Bit-sliced signature 256-bit/3h | 18,0 | 0,4 | 0,1 |
| repetitive-10000x128 | Bit-sliced signature 1024-bit/3h | 18,7 | 1,3 | 1,1 |
| repetitive-10000x128 | Naive string/HashSet trigram | 33,7 | 1,4 | 0,0 |
| unicode-10000x72 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| unicode-10000x72 | Packed UTF-16 arena scan | 0,6 | 1,5 | 1,2 |
| unicode-10000x72 | Packed 48-bit trigram / sorted postings | 13,6 | 2,2 | 0,0 |
| unicode-10000x72 | Packed trigram / adaptive sparse+bitmap | 13,9 | 0,5 | 0,0 |
| unicode-10000x72 | Bit-sliced signature 256-bit/3h | 17,2 | 0,4 | 0,2 |
| unicode-10000x72 | Packed trigram / adaptive Roaring | 18,4 | 0,2 | 6,6 |
| unicode-10000x72 | Bit-sliced signature 1024-bit/3h | 18,7 | 1,3 | 1,0 |
| unicode-10000x72 | Naive string/HashSet trigram | 27,5 | 2,2 | 2,2 |
| urls-10000x128 | .NET string.Contains scan | 0,0 | 0,1 | 0,0 |
| urls-10000x128 | Packed UTF-16 arena scan | 0,5 | 2,5 | 0,1 |
| urls-10000x128 | Bit-sliced signature 256-bit/3h | 31,0 | 0,4 | 0,4 |
| urls-10000x128 | Bit-sliced signature 1024-bit/3h | 33,6 | 1,3 | 2,1 |
| urls-10000x128 | Naive string/HashSet trigram | 180,1 | 10,4 | 8,5 |
| urls-10000x128 | Packed trigram / adaptive sparse+bitmap | 254,6 | 8,6 | 0,4 |
| urls-10000x128 | Packed trigram / adaptive Roaring | 259,2 | 8,3 | 6,3 |
| urls-10000x128 | Packed 48-bit trigram / sorted postings | 260,4 | 8,5 | 11,4 |

## Mutation and rebuild cost

| Index | Operation | Items | Total ms | µs/item | Post-query µs | Status |
|---|---|---:|---:|---:|---:|---|
| .NET string.Contains scan | incremental-upsert | 1 000 | 0,10 | 0,10 | 1 472,42 | OK |
| Packed 48-bit trigram / sorted postings | full-rebuild-after-upserts | 1 000 | 1 481,72 | 1 481,72 | 40,62 | OK |
| Packed trigram / adaptive sparse+bitmap | full-rebuild-after-upserts | 1 000 | 1 501,94 | 1 501,94 | 38,33 | OK |
| Bit-sliced signature 256-bit/3h | incremental-upsert | 1 000 | 6,04 | 6,04 | 83,54 | OK |
| Bit-sliced signature 1024-bit/3h | incremental-upsert | 1 000 | 11,85 | 11,85 | 83,80 | OK |
| NLibSais native suffix array | full-rebuild-after-upserts | 1 000 | 294,29 | 294,29 | 50,32 | OK |
| Segmented NLibSais SA + delta (4096) | incremental-upsert | 1 000 | 0,17 | 0,17 | 27,27 | OK |
| Segmented NLibSais SA + delta (4096) | segment-compaction | 100 000 | 240,76 | 2,41 | 60,13 | OK |
| LIFTI whole-token wildcard | incremental-upsert | 1 000 | 12 796,49 | 12 796,49 | 386 527,20 | OK |
| Lucene.Net exact trigram terms | full-rebuild-after-upserts | 1 000 | 2 865,82 | 2 865,82 | 4 405,00 | OK |

## Read concurrency

| Index | Workers | Single QPS | Parallel QPS | Scaling |
|---|---:|---:|---:|---:|
| Packed trigram / adaptive sparse+bitmap | 8 | 537 251 | 1 119 370 | 2,08× |
| Packed 48-bit trigram / sorted postings | 8 | 451 340 | 1 052 632 | 2,33× |
| Packed trigram / adaptive Roaring | 8 | 177 310 | 553 993 | 3,12× |
| Bit-sliced signature 256-bit/3h | 8 | 49 065 | 308 100 | 6,28× |
| Bit-sliced signature 1024-bit/3h | 8 | 48 791 | 198 311 | 4,06× |
| Packed UTF-16 arena scan | 8 | 772 | 6 188 | 8,02× |
| .NET string.Contains scan | 8 | 718 | 4 056 | 5,65× |
| Lucene.Net exact trigram terms | 8 | 299 | 212 | 0,71× |
| LIFTI whole-token wildcard | 8 | 3 | 15 | 4,83× |

## Suffix-array construction backends

| Builder | Input units | Build ms | Managed Δ MiB | Status |
|---|---:|---:|---:|---|
| NLibSais UTF-16 native | 1 319 282 | 51,2 | 5,0 | OK |
| DeltaQ SAIS UTF-8 | 1 319 282 | 100,9 | 5,0 | OK |
| DeltaQ GoSAIS UTF-8 | 1 319 282 | 103,3 | 5,0 | OK |
| CaseON prefix-doubling UTF-16 | 1 319 282 | 774,4 | 25,2 | OK |

## Multi-pattern batch search

| Method | Patterns | Build ms | Search ms | Status |
|---|---:|---:|---:|---|
| .NET repeated scans | 8 | 0,00 | 37,94 | OK |
| AhoCorasick NuGet | 8 | 1,53 | 191,14 | OK |
| .NET repeated scans | 64 | 0,00 | 112,72 | OK |
| AhoCorasick NuGet | 64 | 0,20 | 142,46 | OK |
| .NET repeated scans | 512 | 0,00 | 782,85 | OK |
| AhoCorasick NuGet | 512 | 1,47 | 191,43 | OK |
| .NET repeated scans | 1024 | 0,00 | 1 567,92 | OK |
| AhoCorasick NuGet | 1024 | 1,11 | 217,49 | OK |
