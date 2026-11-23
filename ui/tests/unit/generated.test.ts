import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import openapiTS, { astToString } from 'openapi-typescript';
import { describe, expect, it } from 'vitest';

/* The TypeScript clients are generated from the committed OpenAPI documents (`npm run gen:api`) and checked
   in; this fails when either document changed without regenerating. */
const root = (p: string) => fileURLToPath(new URL(p, import.meta.url));

describe('generated clients', () => {
  for (const [doc, out] of [['../../../agent/openapi/agent.v1.json', '../../src/backend/generated/agent.ts'], ['../../../service/openapi/constructd.v1.json', '../../src/backend/generated/engine.ts']]) {
    it(`${out.split('/').at(-1)} matches ${doc.split('/').at(-1)}`, async () => {
      const fresh = astToString(await openapiTS(new URL(doc, import.meta.url)));
      const committed = readFileSync(root(out), 'utf8');
      // The CLI adds a header comment; compare the generated body.
      expect(committed.endsWith(fresh), `${out} is stale: run npm run gen:api`).toBe(true);
    }, 30000);
  }
});
