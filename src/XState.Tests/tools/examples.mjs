import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

// Each port must register the same named behavior checks in the native runner
// and in the upstream runner. Build success is never execution evidence.
export const contracts = [
  { name: 'workflow-hello', checks: ['entrypoint'] },
  { name: 'workflow-filling-water', checks: ['entrypoint', 'already-full', 'over-capacity', 'fractional'] },
  ...['workflow-greeting', 'workflow-event-greeting', 'workflow-math-problem', 'workflow-async-function']
    .map(name => ({ name, checks: ['entrypoint', 'alternate-input'] })),
  { name: 'workflow-parallel', checks: ['entrypoint', 'long-first'] },
  { name: 'workflow-async-subflow', checks: ['entrypoint', 'alternate-input'], dialogue: [
    { prompt: 'What is your name?', reply: 'Jenny' },
    { prompt: 'Welcome Jenny, press enter to finish the onboarding process', reply: '' }
  ] }
];

export function inventoryExamples(pin, verification) {
  const prefix = pin.exampleDirectory + '/';
  const groups = new Map();
  const sharedFiles = [];
  const seenPaths = new Set();
  for (const file of verification.files) {
    if (!file.path.startsWith(prefix)) continue;
    if (seenPaths.has(file.path)) throw Error('Duplicate example source: ' + file.path);
    seenPaths.add(file.path);
    const parts = file.path.slice(prefix.length).split('/');
    if (parts.some(part => !part || part === '..' || part === '.'))
      throw Error('Invalid example source path: ' + file.path);
    if (parts.length === 1) { sharedFiles.push(file); continue; }
    const files = groups.get(parts[0]) ?? [];
    files.push(file);
    groups.set(parts[0], files);
  }
  if (!groups.size) throw Error('No verified upstream examples.');
  const exclusions = new Map();
  for (const entry of pin.excludedUiExamples ?? []) {
    if (exclusions.has(entry.name)) throw Error('Duplicate UI exclusion: ' + entry.name);
    if (!groups.has(entry.name) || !entry.reason ||
        !entry.evidence.startsWith(prefix + entry.name + '/') || !seenPaths.has(entry.evidence))
      throw Error('UI exclusion lacks verified source evidence: ' + entry.name);
    exclusions.set(entry.name, entry);
  }
  const examples = [...groups].sort(([a], [b]) => a.localeCompare(b)).map(([name, files]) => {
    files.sort((a, b) => a.path.localeCompare(b.path));
    if (!files.some(file => file.path === prefix + name + '/package.json'))
      throw Error('Example package manifest missing: ' + name);
    const sourceSha256 = createHash('sha256').update(JSON.stringify(files)).digest('hex');
    return { name, scope: exclusions.has(name) ? 'excluded-ui' : 'required',
      exclusion: exclusions.get(name) ?? null, sourceSha256, files };
  });
  return { commit: pin.commit, sharedFiles, total: examples.length,
    required: examples.filter(example => example.scope === 'required').length,
    excludedUi: exclusions.size, examples };
}

export function evaluateExamples(inventory, mappings, native, upstream) {
  const required = new Map(inventory.examples.filter(example => example.scope === 'required').map(example => [example.name, example]));
  const mapped = new Map();
  for (const mapping of mappings) {
    if (!required.has(mapping.name) || mapped.has(mapping.name) || !mapping.checks?.length ||
        mapping.checks.some(check => typeof check !== 'string' || !check.trim()) ||
        new Set(mapping.checks).size !== mapping.checks.length)
      throw Error('Invalid example behavior contract: ' + mapping.name);
    mapped.set(mapping.name, mapping);
  }
  if (native && upstream && (!native.runId || native.runId !== upstream.runId))
    throw Error('Example executions belong to different runs.');
  function evidenceByName(evidence, runtime) {
    if (evidence == null) return new Map();
    if (evidence.commit !== inventory.commit || evidence.runtime !== runtime || !Array.isArray(evidence.examples))
      throw Error('Mismatched example execution evidence: ' + runtime);
    const entries = new Map();
    for (const entry of evidence.examples) {
      const example = required.get(entry.name), contract = mapped.get(entry.name);
      if (!example || !contract || entries.has(entry.name) || entry.sourceSha256 !== example.sourceSha256 ||
          !Array.isArray(entry.checks) || new Set(entry.checks.map(check => check.name)).size !== entry.checks.length ||
          entry.checks.some(check => !contract.checks.includes(check.name)))
        throw Error('Unmapped, duplicate, or stale example result: ' + entry.name);
      entries.set(entry.name, entry);
    }
    return entries;
  }
  const nativeByName = evidenceByName(native, 'csharp');
  const upstreamByName = evidenceByName(upstream, 'upstream');
  const examples = inventory.examples.map(example => {
    if (example.scope === 'excluded-ui') return { name: example.name, status: 'excluded-ui', reason: example.exclusion.reason, evidence: example.exclusion.evidence };
    const mapping = mapped.get(example.name);
    const checks = (mapping?.checks ?? []).map(name => {
      const local = nativeByName.get(example.name)?.checks.find(check => check.name === name);
      const original = upstreamByName.get(example.name)?.checks.find(check => check.name === name);
      const cliPassed = name !== 'entrypoint' || (local?.cli?.status === 'passed' && local.cli.executed === true && local.cli.exitCode === 0
        && (!mapping.dialogue || (local.cli.upstream?.status === 'passed' && local.cli.upstream.executed === true && local.cli.upstream.exitCode === 0)));
      const status = local?.cli?.status === 'failed' || [local, original].some(check => check?.status === 'failed') ? 'failed'
        : [local, original].every(check => check?.status === 'passed' && check.executed === true && Number.isInteger(check.assertions) && check.assertions > 0) && cliPassed ? 'passed' : 'pending';
      return { name, status };
    });
    const status = checks.some(check => check.status === 'failed') ? 'failed'
      : checks.length > 0 && checks.every(check => check.status === 'passed') ? 'passed' : 'pending';
    return { name: example.name, status, checks };
  });
  const passed = examples.filter(example => example.status === 'passed').length;
  const failed = examples.filter(example => example.status === 'failed').length;
  return { total: inventory.total, required: inventory.required, excludedUi: inventory.excludedUi,
    passed, failed, pending: inventory.required - passed - failed,
    complete: passed === inventory.required, examples };
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
  const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
  const inventory = inventoryExamples(read('src/XState/upstream.json'), read('tmp/xstate-parity/upstream-source-verification.json'));
  writeFileSync(resolve(root, 'src/XState.Tests/examples-inventory.json'), JSON.stringify(inventory, null, 2) + '\n');
  console.log(JSON.stringify({ total: inventory.total, required: inventory.required, excludedUi: inventory.excludedUi }));
}
