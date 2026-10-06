import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
export default {
  root: resolve(root, pin.sourceDirectory, pin.package),
  test: {
    globals: true,
    include: ['**/*.{test,spec}.?(c|m)[jt]s?(x)'],
    exclude: ['**/node_modules/**', '**/dist/**'],
    maxWorkers: 2,
    minWorkers: 1,
    reporters: ['default', 'json'],
    outputFile: resolve(root, 'tmp/xstate-parity/upstream-results.json')
  }
};
