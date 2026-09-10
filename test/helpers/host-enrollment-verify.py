#!/usr/bin/env python3
"""Local integration fixture: run the production HTTPS verifier, without changing this VM."""
import importlib.util
import json
from pathlib import Path
import sys

spec = importlib.util.spec_from_file_location('adopt', Path(__file__).resolve().parents[2] / 'bin/adopt-host.py')
adopt = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adopt)
try:
    adopt.verify_host(json.loads(sys.stdin.buffer.read().decode('utf-8')))
    print('Host identity verified')
except Exception as error:
    print(adopt.failure_message(error), file=sys.stderr)
    sys.exit(1)
