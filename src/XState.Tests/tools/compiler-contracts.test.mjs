import assert from 'node:assert/strict';
import { test } from 'node:test';
import { readFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { loadContracts, validateEvidence, outcomePasses, compileSource, resultsPath, root } from './compiler-contracts.mjs';
const context = loadContracts();
const evidence = JSON.parse(readFileSync(resultsPath, 'utf8'));
const contract = context.cases.find(test => test.negative);
const outcome = evidence.cases.find(test => test.id === contract.id);
const directory = resolve(root, 'tmp/xstate-parity/compiler/controls');
test('accepts freshly compiled evidence for exactly the mapped declarations', () => {
  assert.equal(validateEvidence(context, evidence).size, context.cases.length);
});
test('rejects stale library and fixture hashes', () => {
  for (const mutate of [report => report.fingerprints.librarySha256 = 'stale', report => report.fingerprints.fixtures[contract.positive] = 'stale']) {
    const report = structuredClone(evidence); mutate(report);
    assert.throws(() => validateEvidence(context, report), /stale/);
  }
});
test('rejects bare coverage claims and duplicated case evidence', () => {
  assert.throws(() => validateEvidence(context, { coveredDeclarations: evidence.coveredDeclarations }), /stale/);
  const report = structuredClone(evidence); report.cases[1] = report.cases[0];
  assert.throws(() => validateEvidence(context, report), /assertion failed/);
});
test('rejects invalid positive fixture even when the negative diagnostic matches', () => {
  assert.equal(outcomePasses(contract, { ...outcome, positive: outcome.negative }), false);
});
test('rejects a negative fixture mutated to valid code', () => {
  const source = readFileSync(resolve(root, 'src/XState.Tests/compiler-cases', contract.negative), 'utf8').replace('"FOO"', 'SnapshotStatus.Active');
  const negative = compileSource(context, source, directory, 'unexpected-success');
  assert.equal(negative.exitCode, 0);
  assert.equal(outcomePasses(contract, { ...outcome, negative }), false);
});
test('rejects an unrelated compiler error instead of accepting any failure', () => {
  const source = readFileSync(resolve(root, 'src/XState.Tests/compiler-cases', contract.negative), 'utf8').replace('actor.GetSnapshot().Status == "FOO"', 'missingSymbol');
  const negative = compileSource(context, source, directory, 'unrelated-error');
  assert.equal(negative.exitCode, 1); assert.equal(negative.diagnostics[0].id, 'CS0103');
  assert.equal(outcomePasses(contract, { ...outcome, negative }), false);
});
test('rejects the correct diagnostic at the wrong source location', () => {
  const negative = structuredClone(outcome.negative); negative.diagnostics[0].line++;
  assert.equal(outcomePasses(contract, { ...outcome, negative }), false);
});
