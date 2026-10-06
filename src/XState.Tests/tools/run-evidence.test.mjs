import { test } from 'node:test';
import assert from 'node:assert/strict';
import { beginRun, captureStage, finishRun, verifyRun, stageFiles } from './run-evidence.mjs';
const read = path => 'result:' + path;
function captured() {
  const run = beginRun('commit');
  for (const stage of Object.keys(stageFiles)) captureStage(run, stage, read);
  return run;
}
test('requires all JS, native, differential, example and compiler stages in one completed run', () => {
  const run = captured(); assert.throws(() => verifyRun(run, 'commit', read));
  finishRun(run, 'commit', read); verifyRun(run, 'commit', read);
});
test('missing stages cannot finish even when other results passed', () => {
  const run = captured(); delete run.stages.upstream;
  assert.throws(() => finishRun(run, 'commit', read));
});
test('a newer or changed artifact invalidates the previous run', () => {
  const run = captured(); finishRun(run, 'commit', read);
  assert.throws(() => verifyRun(run, 'commit', path => path === 'csharp-examples.json' ? 'new run' : read(path)));
});
test('rejects mismatched commit, failed run and duplicate stages', () => {
  const run = captured(); assert.throws(() => captureStage(run, 'source', read));
  finishRun(run, 'commit', read); assert.throws(() => verifyRun(run, 'other', read));
  run.status = 'failed'; assert.throws(() => verifyRun(run, 'commit', read));
});
test('rejects replaced or duplicated artifact paths', () => {
  const run = captured(); run.stages.native.artifacts[1] = run.stages.native.artifacts[0];
  assert.throws(() => finishRun(run, 'commit', read));
});
