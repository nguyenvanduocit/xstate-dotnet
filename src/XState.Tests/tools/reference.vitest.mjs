import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
export default {
  root,
  test: {
    globals: true,
    include: ['src/XState.Tests/tools/reference/*.test.mjs'],
    maxWorkers: 1, minWorkers: 1,
    reporters: ['default', 'json'],
    outputFile: resolve(root, 'tmp/xstate-parity/reference-results.json')
  }
};
