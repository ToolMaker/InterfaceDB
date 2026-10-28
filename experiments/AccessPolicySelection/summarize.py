"""Summarize raw repeated measurements; keep every sample in the source JSON files."""
from collections import defaultdict
from pathlib import Path
from statistics import median
import json
import sys

folder = Path(sys.argv[1]).resolve()
groups = defaultdict(list)
for name in ('micro.json', 'storage-10000.json', 'storage-100000.json'):
    report = json.loads((folder / name).read_text(encoding='utf-8'))
    for row in report['rows']:
        groups[(row['kind'], row['total'], row['tenants'], row['variant'])].append(row)

def med(rows, *fields):
    values = []
    for row in rows:
        for field in fields: row = row[field]
        values.append(row)
    return median(values)

lines = ['# Measured results', '', 'Timing cells are medians of four per-run medians. Memory uses three fresh processes.', '',
         '## Durable SegmentCore transport adapter', '',
         '| Records / tenants | Variant | Warm page ms | First page ms | Full tenant ms | Update ms | Insert + delete ms | Open ms | Page allocated bytes |',
         '|---|---|---:|---:|---:|---:|---:|---:|---:|---:|']
for (kind, total, tenants, variant), rows in sorted(groups.items()):
    if kind != 'durable-adapter': continue
    lines.append(f'| {total:,} / {tenants} | {variant} | {med(rows,"page","P50Ms"):.4f} | {med(rows,"firstPageMs"):.4f} | {med(rows,"fullTenant","P50Ms"):.4f} | '
                 f'{med(rows,"update","P50Ms"):.4f} | {med(rows,"insertDeletePair","P50Ms"):.4f} | {med(rows,"openMs"):.2f} | {med(rows,"page","BytesPerOperation"):,.0f} |')
lines += ['', '## Key directory only (no storage, network, JWT or payload reads)', '',
          '| Keys / tenants | Variant | Page microseconds | Build ms | Retained MiB after first access | Insert/delete microseconds | Insert/delete allocated bytes |',
          '|---|---|---:|---:|---:|---:|---:|']
for (kind, total, tenants, variant), rows in sorted(groups.items()):
    if kind != 'micro': continue
    edit = '-' if variant == 'Scan' else f'{med(rows,"insertDeleteIndexOnly","P50Ms") * 1000:.2f}'
    allocation = '-' if variant == 'Scan' else f'{med(rows,"insertDeleteIndexOnly","BytesPerOperation"):,.0f}'
    lines.append(f'| {total:,} / {tenants} | {variant} | {med(rows,"page","P50Ms") * 1000:.2f} | {med(rows,"buildMs"):.2f} | '
                 f'{med(rows,"retainedAfterFirstSelect") / 1048576:.2f} | {edit} | {allocation} |')
lines += ['', '## Durable run-to-run spread (per-run page medians)', '', '| Records | Variant | Minimum ms | Maximum ms |', '|---|---|---:|---:|']
for (kind, total, tenants, variant), rows in sorted(groups.items()):
    if kind == 'durable-adapter':
        values = [row['page']['P50Ms'] for row in rows]
        lines.append(f'| {total:,} | {variant} | {min(values):.4f} | {max(values):.4f} |')
lines += ['', '## Real store managed heap (100,000 records)', '', '| Variant | Retained on open MiB | Retained after two tenant accesses MiB |', '|---|---:|---:|']
for variant in ('Scan', 'Array', 'Tree', 'Lazy'):
    memory = [json.loads(path.read_text(encoding='utf-8'))['rows'][0] for path in folder.glob(f'memory-{variant}-*.json')]
    lines.append(f'| {variant} | {med(memory,"retainedOpenBytes") / 1048576:.2f} | {med(memory,"retainedAfterTwoPartitionsBytes") / 1048576:.2f} |')
text = '\n'.join(lines) + '\n'
(folder / 'summary.md').write_text(text, encoding='utf-8', newline='\n')
print(text)
