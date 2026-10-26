"""Build offline resources from cached, unexecuted upstream data. No network at app runtime.

Usage: python _internal/import_databases.py
Raw files live in .build/upstream. SHA-256 fingerprints are written into the manifest.
No code from the downloaded repositories is executed.
"""
import gzip
import hashlib
import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
CACHE = ROOT / '.build' / 'upstream'
DATA = ROOT / 'Data'
NOTICES = ROOT / 'ThirdPartyNotices'
DATA.mkdir(exist_ok=True)
NOTICES.mkdir(exist_ok=True)

SOURCES = {
    'recog_intel.c': 'https://raw.githubusercontent.com/anrieff/libcpuid/master/libcpuid/recog_intel.c',
    'recog_amd.c': 'https://raw.githubusercontent.com/anrieff/libcpuid/master/libcpuid/recog_amd.c',
    'gpu-catalog.json': 'https://raw.githubusercontent.com/onepunk/open-gpu-db/main/dist/catalog.json',
    'gpu-all.json': 'https://raw.githubusercontent.com/RightNow-AI/RightNow-GPU-Database/main/data/all-gpus.json',
    'pci.ids': 'https://raw.githubusercontent.com/pciutils/pciids/master/pci.ids',
}


def write_json(name, obj):
    raw = json.dumps(obj, ensure_ascii=False, separators=(',', ':'), allow_nan=False).encode('utf-8')
    (DATA / name).write_bytes(gzip.compress(raw, mtime=0))


cpu_rows = []
number = r'(-?(?:0x[0-9a-fA-F]+|\d+))'
entry = re.compile(r'\{\s*' + r'\s*,\s*'.join([number] * 8) +
                   r'\s*,\s*\{\s*"([^"]*)"\s*,\s*(\d+)\s*\}\s*,\s*"([^"]*)"\s*,\s*(?:"([^"]*)"|(UNKN_STR))\s*\}')
for filename, vendor in [('recog_intel.c', 'GenuineIntel'), ('recog_amd.c', 'AuthenticAMD')]:
    source = (CACHE / filename).read_text(encoding='utf-8')
    source = re.sub(r'/\*.*?\*/|//[^\n]*', '', source, flags=re.S)
    table = source[source.index('const struct match_entry_t'):]
    table = table[:table.index('\n};')]
    matches = list(entry.finditer(table))
    assert len(matches) == len(re.findall(r'\{\s*' + number, table)), f'Incomplete C table import: {filename}'
    for match in matches:
        groups = match.groups()
        values = [int(n, 16) if '0x' in n.lower() else int(n) for n in groups[:8]]
        cpu_rows.append(dict(zip(['Family', 'Model', 'Stepping', 'DisplayFamily', 'DisplayModel', 'Cores', 'L2', 'L3'], values)) |
                        {'Vendor': vendor, 'Pattern': groups[8], 'PatternWeight': int(groups[9]), 'Codename': groups[10], 'Technology': groups[11] or ''})
assert len(cpu_rows) > 800
write_json('cpu-identities.json.gz', cpu_rows)

catalog = json.loads((CACHE / 'gpu-catalog.json').read_text(encoding='utf-8'))
extra = json.loads((CACHE / 'gpu-all.json').read_text(encoding='utf-8'))
extra_by_id = {r['id']: r for r in extra}
assert len(extra_by_id) == len(extra)
mapping = {
    'architecture': 'architecture', 'generation': 'generation', 'release_date': 'releaseDate',
    'memory.capacity_gb': 'memorySize', 'memory.type': 'memoryType', 'memory.bandwidth_gbps': 'memoryBandwidth',
    'specs.chip': 'gpuName', 'specs.foundry': 'foundry', 'specs.process_nm': 'processSize', 'specs.transistors_b': 'transistors',
    'specs.die_size_mm2': 'dieSize', 'specs.base_clock_mhz': 'baseClock', 'specs.boost_clock_mhz': 'boostClock',
    'specs.memory_clock_mhz': 'memoryClock', 'specs.memory_bus_bits': 'memoryBus', 'specs.shading_units': 'shaders',
    'specs.tensor_cores': 'tensorCores', 'specs.rt_cores': 'rtCores', 'specs.tdp_w': 'tdp', 'specs.bus_interface': 'busInterface',
}
gpu_rows = []
for g in catalog['gpus']:
    supplemental = extra_by_id.get(g['id'], {})
    # Only merge the exact canonical record; never join merely on a partial product name.
    if supplemental.get('name') != g['name'] or supplemental.get('vendor') != g['vendor']:
        supplemental = {}
    values = {k: v for k, v in supplemental.items() if k not in {'id', 'name', 'vendor', 'url'} and v is not None}
    provenance = {k: 'RightNow GPU Database / dbgpu' for k in values}
    for path, key in mapping.items():
        value = g
        for part in path.split('.'):
            value = value.get(part) if isinstance(value, dict) else None
        if value is not None:
            values[key] = value
            provenance[key] = 'Open GPU DB / dbgpu'
    for key, value in g.get('specs', {}).get('apis', {}).items():
        canonical = {'directx': 'directX', 'opengl': 'openGL', 'opencl': 'openCL', 'shader_model': 'shaderModel'}.get(key, key)
        values[canonical] = value
        provenance[canonical] = 'Open GPU DB / dbgpu'
    for p in g.get('provenance', []):
        for path, key in mapping.items():
            if any(path == f or path.startswith(f + '.') for f in p.get('fields', [])) and key in values:
                provenance[key] = p.get('source_url') or p['source_id']
    # Preserve upstream zero values; the consumer decides what is meaningful for each field.
    urls = list(dict.fromkeys(p['source_url'] for p in g.get('provenance', []) if p.get('source_url')))
    gpu_rows.append({'Id': g['id'], 'Name': g['name'], 'Vendor': g['vendor'], 'Aliases': g.get('aliases', []),
                     'DeviceType': g.get('device_type', ''), 'Values': values, 'Sources': provenance, 'Urls': urls})
assert len(gpu_rows) >= 2900
assert len({g['Id'] for g in gpu_rows}) == len(gpu_rows)
write_json('gpu-specifications.json.gz', gpu_rows)

# Vendor/device/subsystem identities only; no driver or executable component.
(DATA / 'pci.ids.gz').write_bytes(gzip.compress((CACHE / 'pci.ids').read_bytes(), mtime=0))
pci_version = re.search(r'^#\s*Version:\s*(\S+)', (CACHE / 'pci.ids').read_text(encoding='utf-8'), re.M).group(1)
manifest = {'SchemaVersion': 1, 'ImportedAt': '2026-10-26', 'CpuRules': len(cpu_rows), 'GpuRecords': len(gpu_rows),
            'GpuVersion': catalog['catalog_version'], 'PciVersion': pci_version,
            'Sources': [{'File': name, 'Url': url, 'Sha256': hashlib.sha256((CACHE / name).read_bytes()).hexdigest()} for name, url in SOURCES.items()]}
(DATA / 'manifest.json').write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
for original, target in [('libcpuid-LICENSE.txt', 'libcpuid-BSD-2-Clause.txt'), ('gpu-LICENSE.txt', 'OpenGPUDB-Apache-2.0.txt'),
                         ('gpu-NOTICE.txt', 'OpenGPUDB-NOTICE.txt'), ('rightnow-LICENSE.txt', 'RightNow-Apache-2.0.txt'),
                         ('dbgpu-LICENSE.txt', 'dbgpu-MIT.txt')]:
    (NOTICES / target).write_bytes((CACHE / original).read_bytes())
print(json.dumps({k: v for k, v in manifest.items() if k != 'Sources'}, indent=2))
