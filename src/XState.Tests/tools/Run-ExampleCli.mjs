import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { runExampleProcess } from './run-example-process.mjs';
import assert from 'node:assert/strict';
import { contracts } from './examples.mjs';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const path = resolve(root, 'tmp/xstate-parity/csharp-examples.json');
const evidence = JSON.parse(readFileSync(path, 'utf8'));
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
let failures = 0;
for (const contract of contracts) {
  const entry = evidence.examples.find(example => example.name === contract.name)?.checks.find(check => check.name === 'entrypoint');
  if (!entry || entry.status !== 'passed') throw Error('Native behavior checks must pass before CLI validation: ' + contract.name);
  const child = await runExampleProcess('dotnet', [resolve(root, 'tmp/csharp-lint/XState.Examples.Tests/XState.Examples.Tests/XState.Examples.dll'), contract.name],
    { cwd: root, dialogue: contract.dialogue });
  entry.cli = { ...child, status: 'failed' };
  try {
    assert.equal(child.error, null); assert.equal(child.exitCode, 0);
    assert.equal(child.answered, contract.dialogue?.length ?? 0);
    assert.deepEqual(child.stdout.trimEnd().split(/\r?\n/), entry.observation.logs);
    assert.equal(child.stderr, '');
    if (contract.dialogue) {
      const original = await runExampleProcess(process.execPath,
        [resolve(root, 'data/library-source/xstate-test-tools/node_modules/vite-node/vite-node.mjs'),
          '--config', resolve(root, 'src/XState.Tests/tools/examples.vitest.mjs'),
          resolve(root, pin.sourceDirectory, 'examples', contract.name, 'main.ts')], { cwd: root, dialogue: contract.dialogue });
      entry.cli.upstream = { ...original, status: 'failed' };
      assert.equal(original.error, null); assert.equal(original.exitCode, 0);
      assert.equal(original.answered, contract.dialogue.length);
      const expected = contract.dialogue.map(step => step.prompt).join('') + 'workflow completed undefined\n';
      assert.equal(original.stdout.replaceAll('\r\n', '\n'), expected);
      assert.equal(original.stderr, '');
      entry.cli.upstream.status = 'passed';
    }
    entry.cli.status = 'passed';
  } catch (error) { failures++; entry.cli.error = String(error); console.error(contract.name, error); }
  writeFileSync(path, JSON.stringify(evidence, null, 2) + '\n');
}
console.log(`Native CLI: ${contracts.length - failures}/${contracts.length} passed.`);
if (failures) process.exitCode = 1;
