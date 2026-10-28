# Measured results

Timing cells are medians of four per-run medians. Memory uses three fresh processes.

## Durable SegmentCore transport adapter

| Records / tenants | Variant | Warm page ms | First page ms | Full tenant ms | Update ms | Insert + delete ms | Open ms | Page allocated bytes |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|
| 10,000 / 100 | Array | 0.1246 | 0.9748 | 0.2519 | 1.9554 | 3.9141 | 20.45 | 33,091 |
| 10,000 / 100 | Lazy | 0.1298 | 1.3201 | 0.2566 | 1.9438 | 3.9143 | 15.92 | 33,899 |
| 10,000 / 100 | Scan | 0.8558 | 1.3150 | 0.9774 | 1.9395 | 3.9114 | 21.52 | 114,542 |
| 10,000 / 100 | Tree | 0.1295 | 0.5900 | 0.2538 | 1.9669 | 3.9706 | 17.51 | 33,878 |
| 100,000 / 100 | Array | 0.2353 | 0.7681 | 4.5556 | 73.4093 | 144.6039 | 109.57 | 37,411 |
| 100,000 / 100 | Lazy | 0.2389 | 55.2632 | 4.6650 | 73.3377 | 145.5625 | 18.40 | 38,270 |
| 100,000 / 100 | Scan | 59.5894 | 62.1227 | 475.7703 | 71.4428 | 144.7762 | 21.96 | 31,692,103 |
| 100,000 / 100 | Tree | 0.2364 | 0.7768 | 4.6524 | 73.3330 | 145.8871 | 116.90 | 38,246 |

## Key directory only (no storage, network, JWT or payload reads)

| Keys / tenants | Variant | Page microseconds | Build ms | Retained MiB after first access | Insert/delete microseconds | Insert/delete allocated bytes |
|---|---|---:|---:|---:|---:|---:|
| 10,000 / 100 | Array | 0.20 | 2.90 | 0.10 | 0.70 | 2,056 |
| 10,000 / 100 | Lazy | 1.65 | 0.00 | 0.01 | 1.40 | 1,536 |
| 10,000 / 100 | Scan | 226.25 | 0.00 | 0.00 | - | - |
| 10,000 / 100 | Tree | 1.60 | 3.13 | 0.49 | 0.60 | 448 |
| 100,000 / 1 | Array | 0.30 | 68.33 | 0.76 | 295.70 | 1,600,526 |
| 100,000 / 1 | Lazy | 54280.80 | 0.00 | 0.00 | 0.20 | 400 |
| 100,000 / 1 | Scan | 52447.85 | 0.00 | 0.00 | - | - |
| 100,000 / 1 | Tree | 1.60 | 72.41 | 4.58 | 0.90 | 448 |
| 100,000 / 100 | Array | 0.20 | 52.91 | 0.79 | 5.45 | 16,456 |
| 100,000 / 100 | Lazy | 1.60 | 0.00 | 0.05 | 1.50 | 1,632 |
| 100,000 / 100 | Scan | 8349.45 | 0.00 | 0.00 | - | - |
| 100,000 / 100 | Tree | 1.55 | 57.92 | 4.61 | 0.70 | 448 |
| 1,000,000 / 1000 | Array | 0.20 | 618.71 | 7.89 | 5.20 | 16,456 |
| 1,000,000 / 1000 | Lazy | 1.60 | 0.01 | 0.05 | 1.50 | 1,632 |
| 1,000,000 / 1000 | Scan | 111558.75 | 0.00 | 0.00 | - | - |
| 1,000,000 / 1000 | Tree | 1.60 | 760.59 | 46.06 | 0.70 | 448 |

## Durable run-to-run spread (per-run page medians)

| Records | Variant | Minimum ms | Maximum ms |
|---|---|---:|---:|
| 10,000 | Array | 0.1242 | 0.1250 |
| 10,000 | Lazy | 0.1297 | 0.1302 |
| 10,000 | Scan | 0.6194 | 0.8755 |
| 10,000 | Tree | 0.1288 | 0.1298 |
| 100,000 | Array | 0.2333 | 0.2360 |
| 100,000 | Lazy | 0.2330 | 0.2490 |
| 100,000 | Scan | 59.2631 | 60.1422 |
| 100,000 | Tree | 0.2349 | 0.2406 |

## Real store managed heap (100,000 records)

| Variant | Retained on open MiB | Retained after two tenant accesses MiB |
|---|---:|---:|
| Scan | 2.52 | 2.53 |
| Array | 21.63 | 21.63 |
| Tree | 25.44 | 25.45 |
| Lazy | 2.53 | 2.99 |
