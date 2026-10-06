import { test } from 'node:test';
import assert from 'node:assert/strict';
import { inventoryExamples, evaluateExamples } from './examples.mjs';
const files = [
  { path: 'examples/console/package.json', sha256: 'manifest' },
  { path: 'examples/console/main.ts', sha256: 'source' },
  { path: 'examples/ui/package.json', sha256: 'ui-manifest' },
  { path: 'examples/ui/src/App.tsx', sha256: 'ui-source' }
];
const pin = { commit: 'pin', exampleDirectory: 'examples', excludedUiExamples: [{ name: 'ui', reason: 'User excludes UI', evidence: 'examples/ui/src/App.tsx' }] };
const inventory = () => inventoryExamples(pin, { files });
const mappings = [{ name: 'console', checks: ['completes', 'cancels'] }];
function evidence(runtime) {
  return { commit: 'pin', runtime, runId: 'paired-run', examples: [{ name: 'console', sourceSha256: inventory().examples.find(example => example.name === 'console').sourceSha256,
    checks: mappings[0].checks.map(name => ({ name, status: 'passed', executed: true, assertions: 2 })) }] };
}
const evaluate = (native = evidence('csharp'), upstream = evidence('upstream'), contracts = mappings) => evaluateExamples(inventory(), contracts, native, upstream);
test('counts UI exclusions separately from executable examples', () => {
  assert.equal(inventory().total, 2); assert.equal(inventory().required, 1); assert.equal(inventory().excludedUi, 1);
  const result = evaluate(); assert.equal(result.complete, true); assert.equal(result.passed, 1); assert.equal(result.examples[1].status, 'excluded-ui');
});
test('absent contracts remain pending', () => assert.equal(evaluate(null, null, []).pending, 1));
test('requires both native and upstream execution', () => {
  assert.equal(evaluate(null).complete, false); assert.equal(evaluate(undefined, null).complete, false);
});
for (const variant of ['build-only', 'skipped', 'missing-check', 'no-assertions', 'failed']) {
  test('does not pass ' + variant, () => {
    const data = evidence('csharp'); const check = data.examples[0].checks[0];
    if (variant === 'build-only') check.executed = false;
    if (variant === 'skipped') check.status = 'skipped';
    if (variant === 'missing-check') data.examples[0].checks.pop();
    if (variant === 'no-assertions') check.assertions = 0;
    if (variant === 'failed') check.status = 'failed';
    assert.equal(evaluate(data).complete, false);
    assert.equal(evaluate(data).failed, variant === 'failed' ? 1 : 0);
  });
}
for (const variant of ['stale', 'duplicate', 'excluded', 'unknown-check', 'wrong-commit']) {
  test('rejects ' + variant + ' result evidence', () => {
    const data = evidence('csharp');
    if (variant === 'stale') data.examples[0].sourceSha256 = 'old';
    if (variant === 'duplicate') data.examples.push(data.examples[0]);
    if (variant === 'excluded') data.examples[0].name = 'ui';
    if (variant === 'unknown-check') data.examples[0].checks[0].name = 'unreviewed';
    if (variant === 'wrong-commit') data.commit = 'old';
    assert.throws(() => evaluate(data));
  });
}
test('rejects duplicate sources and nonexistent exclusions', () => {
  assert.throws(() => inventoryExamples(pin, { files: [...files, files[0]] }));
  assert.throws(() => inventoryExamples({ ...pin, excludedUiExamples: [{ name: 'absent', reason: 'UI', evidence: 'examples/ui/src/App.tsx' }] }, { files }));
});
test('source changes invalidate previous execution evidence', () => {
  const changed = inventoryExamples(pin, { files: files.map(file => file.path.endsWith('main.ts') ? { ...file, sha256: 'changed' } : file) });
  assert.throws(() => evaluateExamples(changed, mappings, evidence('csharp'), evidence('upstream')));
});
test('rejects empty, duplicate and UI contracts', () => {
  for (const contracts of [[{ name: 'console', checks: [] }], [...mappings, ...mappings], [{ name: 'ui', checks: ['runs'] }]])
    assert.throws(() => evaluate(null, null, contracts));
});

test('top-level example documentation is verified without becoming an example', () => {
  const result = inventoryExamples(pin, { files: [...files, { path: 'examples/readme.md', sha256: 'readme' }] });
  assert.equal(result.total, 2); assert.equal(result.sharedFiles.length, 1);
});

test('rejects stale paired execution even with identical upstream source', () => {
  const original = evidence('upstream'); original.runId = 'old-run';
  assert.throws(() => evaluate(undefined, original), /different runs/);
});
test('entrypoint also requires successful native CLI execution', () => {
  const mappings = [{ name: 'console', checks: ['entrypoint'] }];
  const local = evidence('csharp'), original = evidence('upstream');
  for (const entry of [local, original]) entry.examples[0].checks = [{ name: 'entrypoint', status: 'passed', executed: true, assertions: 1 }];
  assert.equal(evaluateExamples(inventory(), mappings, local, original).complete, false);
  local.examples[0].checks[0].cli = { status: 'passed', executed: true, exitCode: 0 };
  assert.equal(evaluateExamples(inventory(), mappings, local, original).complete, true);
  local.examples[0].checks[0].cli.exitCode = 1;
  assert.equal(evaluateExamples(inventory(), mappings, local, original).complete, false);
  local.examples[0].checks[0].cli.status = 'failed';
  assert.equal(evaluateExamples(inventory(), mappings, local, original).failed, 1);
});

test('interactive entrypoints require real upstream CLI evidence too', () => {
  const mappings = [{ name: 'console', checks: ['entrypoint'], dialogue: [{ prompt: 'Name?', reply: 'Ada' }] }];
  const local = evidence('csharp'), original = evidence('upstream');
  for (const entry of [local, original]) entry.examples[0].checks = [{ name: 'entrypoint', status: 'passed', executed: true, assertions: 1 }];
  local.examples[0].checks[0].cli = { status: 'passed', executed: true, exitCode: 0 };
  assert.equal(evaluateExamples(inventory(), mappings, local, original).complete, false);
  local.examples[0].checks[0].cli.upstream = { status: 'passed', executed: true, exitCode: 0 };
  assert.equal(evaluateExamples(inventory(), mappings, local, original).complete, true);
});
