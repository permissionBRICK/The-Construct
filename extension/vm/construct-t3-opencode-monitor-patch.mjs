#!/usr/bin/env node
// The build repository owns runtime patching; use the installed build's tools.
import {execFileSync, spawnSync} from 'node:child_process';
import {resolve} from 'node:path';
const root = execFileSync('python3', [resolve(import.meta.dirname, '../../bin/t3code-build-source.py'), '--installed'], {encoding:'utf8'}).trim();
const result = spawnSync(process.execPath, [resolve(root, 'extension/vm/construct-t3-opencode-monitor-patch.mjs), ...process.argv.slice(2)], {stdio:'inherit'});
if (result.error) throw result.error;
process.exit(result.status ?? 1);
