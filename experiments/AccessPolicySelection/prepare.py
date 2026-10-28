"""Generate an isolated, non-packable prototype from the current public working tree."""
from pathlib import Path
import hashlib
import json
import subprocess
import xml.etree.ElementTree as ET

source = Path(__file__).resolve().parent
root = source.parents[1]
output = root / '.tmp' / 'access-policy-selection'
transport = output / 'Transport'
runner = output / 'Runner'
tests = output / 'Tests'
for folder in (transport, runner, tests, output / 'results'):
    folder.mkdir(parents=True, exist_ok=True)

def write(path, contents):
    path.write_text(contents, encoding='utf-8', newline='\n')

def replace_once(text, before, after):
    assert text.count(before) == 1, before[:120]
    return text.replace(before, after)

adapter = (root / 'InterfaceDB.Transport/SegmentCoreEncodedEnvelopeStore.cs').read_text(encoding='utf-8')
if 'IPartitionKeyIndex' in adapter:
    raise SystemExit('This historical prototype requires its pre-integration source snapshot. '
                     'Use StorageBenchmarks.AccessPolicy for current production measurements; '
                     'see experiments/AccessPolicySelection/README.md.')
adapter = replace_once(adapter, '    private readonly SemaphoreSlim _gate', '''    private readonly IPartitionKeyDirectory? _directory;
    private bool _directoryDirty;
    private readonly SemaphoreSlim _gate''')
adapter = replace_once(adapter, '''        => (_store, _limits, _maximumEnumerationKeys, _maximumPartitionKeys)
            = (store, limits, maximumEnumerationKeys, maximumPartitionKeys);''', '''    {
        (_store, _limits, _maximumEnumerationKeys, _maximumPartitionKeys)
            = (store, limits, maximumEnumerationKeys, maximumPartitionKeys);
        if (Enum.TryParse<SelectionVariant>(Environment.GetEnvironmentVariable("IDB_EXPERIMENT_INDEX"), out var variant)
            && variant != SelectionVariant.Scan)
        {
            _directory = variant == SelectionVariant.Lazy
                ? new LazyPartitionKeyDirectory(() => store.GetKeysSnapshot())
                : new PartitionKeyDirectory(variant);
            if (variant != SelectionVariant.Lazy) _directory.Rebuild(store.GetKeysSnapshot());
        }
    }''')
adapter = replace_once(adapter, '''        return new SegmentCoreEncodedEnvelopeStore(store, limits, maximumEnumerationKeys, maximumPartitionKeys.Value)
            { RetainedChangeCount = retainedChangeCount };''', '''        try
        {
            return new SegmentCoreEncodedEnvelopeStore(store, limits, maximumEnumerationKeys, maximumPartitionKeys.Value)
                { RetainedChangeCount = retainedChangeCount };
        }
        catch { await store.DisposeAsync().ConfigureAwait(false); throw; }''')
adapter = replace_once(adapter, '''        finally { _gate.Release(); }
    }

    private EncodedStoreRequest''', '''        catch { _directoryDirty = true; throw; }
        finally { _gate.Release(); }
    }

    private EncodedStoreRequest''')
start = adapter.index('        // Exclusive ownership plus the adapter gate makes Count')
end = adapter.index('        var records = new List<EncodedRecord>', start)
original = adapter[start:end]
indexed = '''        string[] keys;
        int start;
        if (_directory is not null && partition.Length == ScopedEncodedStore.PrefixBytes
            && ScopedEncodedStore.TryGetPrefix(partition + "x", out var canonical) && canonical == partition)
        {
            if (_directoryDirty)
            {
                _directory.Rebuild(_store.GetKeysSnapshot());
                _directoryDirty = false;
            }
            var selected = _directory.Select(partition, afterKey, pageSize + 1);
            if (selected.Count > _maximumPartitionKeys)
                throw LimitExceeded("This store exceeds the configured enumeration key ceiling.");
            keys = selected.Keys;
            start = 0;
        }
        else
        {
BASELINE
        }
'''.replace('BASELINE', '\n'.join('    ' + line for line in original.rstrip().replace('var keys =', 'keys =').replace('var start =', 'start =').splitlines()))
adapter = adapter[:start] + indexed + adapter[end:]
write(transport / 'SegmentCoreEncodedEnvelopeStore.cs', adapter)
changes = (root / 'InterfaceDB.Transport/SegmentCoreEncodedEnvelopeStore.Changes.cs').read_text(encoding='utf-8')
changes = replace_once(changes, '        await transaction.CommitAsync(token).ConfigureAwait(false);', '''        await transaction.CommitAsync(token).ConfigureAwait(false);
        if (!_directoryDirty) _directory?.Apply(mutations);''')
write(transport / 'SegmentCoreEncodedEnvelopeStore.Changes.cs', changes)

tree = ET.parse(root / 'InterfaceDB.Transport/InterfaceDB.Transport.csproj')
project = tree.getroot()
props = project.find('PropertyGroup')
ET.SubElement(props, 'IsPackable').text = 'false'
ET.SubElement(props, 'AssemblyName').text = 'InterfaceDB.Transport'
for group in project.findall('ItemGroup'):
    for item in list(group):
        if item.tag == 'ProjectReference':
            item.set('Include', str((root / 'InterfaceDB.Transport' / item.get('Include')).resolve()))
        if item.tag == 'None': group.remove(item)
group = ET.SubElement(project, 'ItemGroup')
ET.SubElement(group, 'InternalsVisibleTo', Include='AccessPolicySelection')
for path in (root / 'InterfaceDB.Transport').rglob('*.cs'):
    if any(x in path.parts for x in ('bin', 'obj')) or path.name in ('SegmentCoreEncodedEnvelopeStore.cs', 'SegmentCoreEncodedEnvelopeStore.Changes.cs'):
        continue
    ET.SubElement(group, 'Compile', Include=str(path))
ET.SubElement(group, 'Compile', Include=str(source / 'PartitionKeyDirectory.cs'))
ET.indent(project)
write(transport / 'Experimental.Transport.csproj', ET.tostring(project, encoding='unicode'))
write(runner / 'Runner.csproj', f'''<Project Sdk="Microsoft.NET.Sdk">
<PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><AssemblyName>AccessPolicySelection</AssemblyName><IsPackable>false</IsPackable></PropertyGroup>
<ItemGroup><ProjectReference Include="../Transport/Experimental.Transport.csproj"/><Compile Include="{source / 'Program.cs'}"/></ItemGroup>
</Project>''')
test_tree = ET.parse(root / 'InterfaceDB.Transport.Tests/InterfaceDB.Transport.Tests.csproj')
test_project = test_tree.getroot()
ET.SubElement(test_project.find('PropertyGroup'), 'AssemblyName').text = 'InterfaceDB.Transport.Tests'
for group in test_project.findall('ItemGroup'):
    for item in list(group):
        if item.tag == 'ProjectReference': group.remove(item)
group = ET.SubElement(test_project, 'ItemGroup')
ET.SubElement(group, 'ProjectReference', Include='../Transport/Experimental.Transport.csproj')
for path in (root / 'InterfaceDB.Transport.Tests').rglob('*.cs'):
    if not any(x in path.parts for x in ('bin', 'obj')):
        ET.SubElement(group, 'Compile', Include=str(path))
ET.SubElement(group, 'Compile', Include=str(source / 'DirectoryTests.cs'))
ET.indent(test_project)
write(tests / 'Tests.csproj', ET.tostring(test_project, encoding='unicode'))

inputs = [p for folder in ('InterfaceDB.Transport', 'InterfaceDB.Storage.SegmentCore', 'InterfaceDB.Authentication', 'Idb.Libraries')
          for p in (root / folder).rglob('*.cs') if not any(x in p.parts for x in ('obj', 'bin'))]
inputs.extend(source.glob('*'))
manifest = {'gitHead': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=root, text=True).strip(),
            'note': 'Includes uncommitted policy implementation; hashes, not HEAD alone, identify inputs.',
            'files': {str(p.relative_to(root)): hashlib.sha256(p.read_bytes()).hexdigest() for p in inputs if p.is_file()}}
write(output / 'inputs.json', json.dumps(manifest, indent=2))
print(output)
