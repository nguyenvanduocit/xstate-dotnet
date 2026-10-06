import { verifyRun } from './run-evidence.mjs';
import { inventoryExamples, evaluateExamples, contracts as exampleContracts } from './examples.mjs';
import { loadContracts, validateEvidence } from './compiler-contracts.mjs';
import { readFileSync, writeFileSync, existsSync, mkdirSync } from 'node:fs';
import { resolve, dirname, relative } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const execution = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/run-evidence.json'), 'utf8'));
verifyRun(execution, pin.commit, name => readFileSync(resolve(root, 'tmp/xstate-parity', name)));
const inventory = JSON.parse(readFileSync(resolve(root, 'src/XState.Tests/upstream-inventory.json'), 'utf8'));
const sourceRoot = resolve(root, pin.sourceDirectory);
const verification = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/upstream-source-verification.json'), 'utf8'));
if (verification.commit !== pin.commit || verification.sourceDirectory !== pin.sourceDirectory || verification.archiveSha256 !== pin.archiveSha256 ||
    createHash('sha256').update(readFileSync(resolve(root, verification.archive))).digest('hex') !== pin.archiveSha256)
  throw Error('Pinned source verification evidence is stale or mismatched.');
if (!verification.files?.length || new Set(verification.files.map(file => file.path)).size !== verification.files.length)
  throw Error('Pinned source verification evidence is empty or duplicated.');
for (const file of verification.files) {
  if ((!file.path.startsWith(pin.package + '/') && !file.path.startsWith(pin.exampleDirectory + '/')) || createHash('sha256').update(readFileSync(resolve(sourceRoot, file.path))).digest('hex') !== file.sha256)
    throw Error('Pinned source differs from verified archive evidence: ' + file.path);
}

for (const source of inventory.sourceFiles) {
  const hash = createHash('sha256').update(readFileSync(resolve(sourceRoot, source.path))).digest('hex');
  if (hash !== source.sha256) throw Error('Upstream source changed: ' + source.path);
}
const baseline = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/upstream-results.json'), 'utf8'));
const native = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-results.json'), 'utf8'));
const nativeById = new Map();
for (const test of native.tests) {
  if (nativeById.has(test.id)) throw Error('Duplicate C# test mapping: ' + test.id);
  nativeById.set(test.id, test);
}
const tests = [];
for (const file of baseline.testResults) {
  const path = relative(sourceRoot, file.name).replaceAll('\\', '/');
  const occurrences = new Map();
  for (const test of file.assertionResults) {
    const baseId = path + '::' + [...test.ancestorTitles, test.title].join(' > ');
    const occurrence = (occurrences.get(baseId) ?? 0) + 1;
    occurrences.set(baseId, occurrence);
    const id = baseId + (occurrence > 1 ? ' [occurrence ' + occurrence + ']' : '');
    const port = nativeById.get(id);
    tests.push({ id, upstreamStatus: test.status, csharpStatus: port?.status ?? 'pending', error: port?.error ?? null });
    nativeById.delete(id);
  }
}
if (nativeById.size) throw Error('C# mappings missing from upstream baseline: ' + [...nativeById.keys()].join('\n'));
const compileResultsPath = resolve(root, 'tmp/xstate-parity/compile-results.json');
const coveredTypes = existsSync(compileResultsPath)
  ? validateEvidence(loadContracts(), JSON.parse(readFileSync(compileResultsPath, 'utf8'))) : new Set();
const missingTypeAssertions = inventory.tests.filter(t => t.typeAssertions && !coveredTypes.has(t.id)).map(t => t.id);
const passed = tests.filter(t => t.csharpStatus === 'passed').length;
const failures = tests.filter(t => t.csharpStatus === 'failed').length;
const pending = tests.filter(t => t.upstreamStatus === 'passed' && t.csharpStatus === 'pending').length;
const resourcesPath = resolve(root, 'tmp/xstate-parity/csharp-results.resources.json');
const resources = existsSync(resourcesPath) ? JSON.parse(readFileSync(resourcesPath, 'utf8')) : null;
const resourceChecksPass = resources !== null && resources.total > 0 && resources.failed === 0 && resources.tests.length === resources.total && resources.tests.every(t => t.status === 'passed');
const readEvidence = name => { const path = resolve(root, 'tmp/xstate-parity/' + name); return existsSync(path) ? JSON.parse(readFileSync(path, 'utf8')) : null; };
const examples = evaluateExamples(inventoryExamples(pin, verification), exampleContracts, readEvidence('csharp-examples.json'), readEvidence('upstream-examples.json'));
const reference = readEvidence('reference-results.json');
const differentialPassed = reference?.numPassedTests > 0 && reference.numFailedTests === 0 && reference.numPendingTests === 0 && reference.numPassedTests === reference.numTotalTests;
const complete = differentialPassed && examples.complete && baseline.numFailedTests === 0 && failures === 0 && pending === 0 && missingTypeAssertions.length === 0 && resourceChecksPass;
const report = { runId: execution.runId, execution: { status: execution.status, startedAt: execution.startedAt, finishedAt: execution.finishedAt }, differential: { total: reference?.numTotalTests, passed: reference?.numPassedTests, failed: reference?.numFailedTests, skipped: reference?.numPendingTests }, version: pin.version, commit: pin.commit, sourceHashesVerified: true, verifiedSourceFiles: verification.files.length, complete, examples, resourceChecks: resources ? { total: resources.total, passed: resources.passed, failed: resources.failed } : { status: "missing" }, baseline: { total: baseline.numTotalTests, passed: baseline.numPassedTests, skipped: baseline.numPendingTests, todo: baseline.numTodoTests }, csharp: { passed, failed: failures, pendingRuntime: pending }, typeAssertions: { declarations: inventory.counts.declarationsWithTypeAssertions, pending: missingTypeAssertions.length }, tests, pendingTypeAssertionIds: missingTypeAssertions };
mkdirSync(resolve(root, 'tmp/xstate-parity'), { recursive: true });
writeFileSync(resolve(root, 'tmp/xstate-parity/parity-report.json'), JSON.stringify(report, null, 2) + '\n');
console.log(JSON.stringify({ runId: report.runId, complete, differential: report.differential, baseline: report.baseline, csharp: report.csharp, typeAssertions: report.typeAssertions, resourceChecks: report.resourceChecks, examples: { total: examples.total, required: examples.required, excludedUi: examples.excludedUi, passed: examples.passed, failed: examples.failed, pending: examples.pending } }, null, 2));
if (process.argv.includes('--require-complete') && !complete) process.exitCode = 1;

