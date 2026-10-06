import { readFileSync, writeFileSync, rmSync } from 'node:fs';
import { createHash, randomUUID } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

export const stageFiles = {
  source: ['upstream-source-verification.json'],
  upstream: ['upstream-results.json'],
  native: ['csharp-results.json', 'csharp-results.resources.json'],
  differential: ['reference-results.json'],
  examples: ['csharp-examples.json', 'upstream-examples.json', 'example-runner-results.json'],
  compiler: ['compile-results.json']
};
const digest = bytes => createHash('sha256').update(bytes).digest('hex');
export function beginRun(commit) { return { runId: randomUUID(), commit, status: 'running', startedAt: new Date().toISOString(), stages: {} }; }
export function captureStage(run, name, read) {
  if (run.status !== 'running' || !Object.hasOwn(stageFiles, name) || Object.hasOwn(run.stages, name))
    throw Error('Unexpected or repeated execution stage: ' + name);
  run.stages[name] = { completedAt: new Date().toISOString(), artifacts: stageFiles[name].map(path => ({ path, sha256: digest(read(path)) })) };
}
function verifyArtifacts(run, commit, read) {
  if (!run.runId || run.commit !== commit) throw Error('Execution run commit or identity mismatch.');
  if (Object.keys(run.stages).length !== Object.keys(stageFiles).length) throw Error('Execution stages are incomplete.');
  for (const [name, paths] of Object.entries(stageFiles)) {
    const artifacts = run.stages[name]?.artifacts;
    if (!Array.isArray(artifacts) || artifacts.length !== paths.length || new Set(artifacts.map(artifact => artifact.path)).size !== paths.length)
      throw Error('Execution evidence missing or duplicated for stage: ' + name);
    for (const path of paths) {
      const artifact = artifacts.find(entry => entry.path === path);
      if (!artifact || artifact.sha256 !== digest(read(path))) throw Error('Execution result changed after stage completion: ' + path);
    }
  }
}
export function finishRun(run, commit, read) {
  if (run.status !== 'running') throw Error('Execution run is not running.');
  verifyArtifacts(run, commit, read);
  run.status = 'verified'; run.finishedAt = new Date().toISOString();
}
export function verifyRun(run, commit, read) {
  if (run.status !== 'verified') throw Error('Execution run has not passed every required stage.');
  verifyArtifacts(run, commit, read);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../..');
  const directory = resolve(root, 'tmp/xstate-parity');
  const path = resolve(directory, 'run-evidence.json');
  const commit = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8')).commit;
  const read = name => readFileSync(resolve(directory, name));
  const command = process.argv[2];
  const run = command === 'begin' ? beginRun(commit) : JSON.parse(readFileSync(path, 'utf8'));
  if (command === 'begin') rmSync(resolve(directory, 'parity-report.json'), { force: true });
  else if (command === 'capture') captureStage(run, process.argv[3], read);
  else if (command === 'finish') finishRun(run, commit, read);
  else if (command === 'fail') { run.status = 'failed'; run.error = process.argv[3]; run.finishedAt = new Date().toISOString(); }
  else throw Error('Unknown execution-evidence command.');
  writeFileSync(path, JSON.stringify(run, null, 2) + '\n');
}
