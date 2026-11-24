#!/usr/bin/env python3
"""Pick the test classes of a .NET suite that touched paths reach, for `run-local-checks.sh --quick`.

    python3 test/helpers/test-areas.py engine|agent < paths

Prints a `dotnet test --filter` expression, `all` when the whole suite should run, or nothing when no path
concerns the suite. A changed test file selects its own classes. A changed source file selects the test classes
whose file names one of its top-level types, and the classes and test folders named after a word of its file
name (`VaultEndpoints.cs` reaches `Constructd.Tests.Vault` and every `*Vault*Tests`). The whole suite runs when
a source file reaches nothing that way, when one is wiring every test depends on (Program.cs, the composition,
a project file), or when the selection grows past half the suite.
"""
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[2]
SUITES = {
    'engine': {
        'tests': 'service/tests/Constructd.Tests',
        'sources': ['service/src/', 'service/openapi/'],
        'wiring': [r'(^|/)Program\.cs$', r'/Composition/', r'\.csproj$', r'appsettings[^/]*\.json$', r'^service/openapi/',
                   r'^service/src/Constructd\.Fakes/', r'/Infrastructure/', r'^service/tests/Constructd\.Tests/Support/'],
    },
    'agent': {
        'tests': 'agent/tests/Construct.Agent.Tests',
        'sources': ['agent/src/', 'agent/openapi/', 'agent/tests/Construct.Companion.Fakes/'],
        'wiring': [r'(^|/)Program\.cs$', r'\.csproj$', r'^agent/openapi/', r'/Http/', r'^agent/tests/Construct\.Agent\.Tests/Support/',
                   r'^agent/tests/Construct\.Companion\.Fakes/'],
    },
}
# Words of a file name that name no area of their own.
GENERIC = {'Api', 'Async', 'Base', 'Client', 'Command', 'Commands', 'Composition', 'Core', 'Dto', 'Endpoint', 'Endpoints',
           'Extensions', 'Fake', 'Helper', 'Helpers', 'Host', 'In', 'Job', 'Jobs', 'Json', 'Logic', 'Memory', 'Options',
           'Primary', 'Request', 'Requests', 'Response', 'Rules', 'Service', 'Services', 'Settings', 'Sqlite', 'State',
           'Store', 'Stores', 'Tests', 'Vm', 'Vms', 'Windows', 'Linux', 'Construct', 'Constructd', 'Agent', 'Device',
           'Engine', 'Engines', 'Test'}
TOP_LEVEL_TYPE = re.compile(r'^(?:public |internal )?(?:static |sealed |abstract |partial |readonly |file )*'
                            r'(?:class|record|interface|enum|struct) (\w+)', re.M)
TEST_METHOD = re.compile(r'^\s*\[(?:Fact|Theory)\b|^\s*\[\w+Fact\b', re.M)


def words(stem):
    return [w for w in re.findall(r'[A-Z]+(?![a-z])|[A-Z][a-z0-9]*|[a-z0-9]+', stem) if len(w) > 2 and w not in GENERIC]


def test_classes(tests):
    """Every test class of the project: (namespace, name, file text, folder)."""
    found = []
    for path in sorted((ROOT / tests).rglob('*.cs')):
        if '/obj/' in path.as_posix() or '/bin/' in path.as_posix():
            continue
        text = path.read_text(encoding='utf-8', errors='replace')
        if not TEST_METHOD.search(text):
            continue
        namespace = (re.search(r'^namespace ([\w.]+)', text, re.M) or [None, ''])[1]
        folder = path.parent.relative_to(ROOT / tests).as_posix()
        for name in TOP_LEVEL_TYPE.findall(text):
            found.append((namespace, name, text, folder, path.relative_to(ROOT).as_posix()))
    return found


def select(suite, paths):
    spec = SUITES[suite]
    tests = spec['tests'] + '/'
    mine = [p for p in paths if p.startswith(tests) or any(p.startswith(s) for s in spec['sources'])]
    if not mine:
        return None
    classes = test_classes(spec['tests'])
    chosen = set()
    for path in mine:
        if any(re.search(w, path) for w in spec['wiring']):
            return 'all'
        if path.startswith(tests):
            own = {(ns, name) for ns, name, _, _, file in classes if file == path}
            if not own and path.endswith('.cs'):
                return 'all'  # a helper of the tests
            chosen |= own
            continue
        file = ROOT / path
        stem = Path(path).stem
        types = set(TOP_LEVEL_TYPE.findall(file.read_text(encoding='utf-8', errors='replace'))) if file.suffix == '.cs' and file.exists() else set()
        types.add(stem) if file.suffix != '.cs' else None
        keys = words(stem)
        reached = set()
        for ns, name, text, folder, _ in classes:
            if types and any(re.search(r'\b%s\b' % re.escape(t), text) for t in types):
                reached.add((ns, name))
            elif any(k in name or k == folder.split('/')[0] for k in keys):
                reached.add((ns, name))
        if not reached:
            return 'all'
        chosen |= reached
    if len(chosen) * 2 > len({(ns, name) for ns, name, *_ in classes}):
        return 'all'
    return '|'.join('FullyQualifiedName~%s.%s.' % (ns, name) for ns, name in sorted(chosen))


if __name__ == '__main__':
    result = select(sys.argv[1], [line.strip() for line in sys.stdin if line.strip()])
    if result:
        print(result)
