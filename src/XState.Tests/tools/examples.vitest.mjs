import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
export default {
  root,
  resolve: { alias: { xstate: resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts') } },
  test: {
    globals: true, include: ['src/XState.Tests/tools/examples/*.test.mjs'],
    maxWorkers: 1, minWorkers: 1, testTimeout: 15000,
    reporters: ['default', 'json'], outputFile: resolve(root, 'tmp/xstate-parity/example-runner-results.json')
  }
};
