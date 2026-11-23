#!/usr/bin/env python3
"""Map touched paths to check groups by the path table in docs/local-checks.md.

Reads paths on stdin, one per line, and prints the groups to run, one per line. A path that no
row matches selects `all`. In a pattern `*` stays within one directory and `**` crosses them.
"""
from pathlib import Path
import re
import sys

TABLE_HEADER = '| Path | Groups |'


def pattern(glob):
    out = ''
    for part in re.split(r'(\*\*|\*|\?)', glob):
        out += {'**': '.*', '*': '[^/]*', '?': '[^/]'}.get(part, re.escape(part))
    return re.compile(out + r'\Z')


def table(text):
    lines = text.splitlines()
    start = lines.index(TABLE_HEADER) + 2
    rows = []
    for line in lines[start:]:
        if not line.startswith('|'):
            break
        paths, groups = [cell.strip() for cell in line.strip('|').split('|')]
        rows.append(([pattern(p) for p in re.findall(r'`([^`]+)`', paths)], re.findall(r'`([^`]+)`', groups)))
    return rows


def groups(paths, rows):
    selected = set()
    for path in paths:
        matched = [names for patterns, names in rows if any(p.match(path) for p in patterns)]
        if not matched:
            return ['all']
        selected.update(name for names in matched for name in names)
    return sorted(selected)


if __name__ == '__main__':
    doc = Path(__file__).resolve().parents[2] / 'docs/local-checks.md'
    print('\n'.join(groups([l.strip() for l in sys.stdin if l.strip()], table(doc.read_text()))))
