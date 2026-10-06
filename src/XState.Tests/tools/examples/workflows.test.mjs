import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { createActor } from 'xstate';
import { contracts } from '../examples.mjs';
const harness = vi.hoisted(() => ({ snapshots: null, logs: null, answers: [], closed: 0 }));
vi.mock('readline', () => ({ default: { createInterface: () => ({
  question(question, callback) {
    harness.logs.push(question);
    if (!harness.answers.length) throw Error('Unexpected onboarding question: ' + question);
    const answer = harness.answers.shift(); queueMicrotask(() => callback(answer));
  },
  close() { harness.closed++; }
}) } }));
vi.mock('xstate', async importOriginal => {
  const original = await importOriginal();
  return { ...original, createActor: (...args) => {
    const actor = original.createActor(...args);
    if (harness.snapshots) actor.subscribe(snapshot => harness.snapshots.push({
      value: JSON.parse(JSON.stringify(snapshot.value)), status: snapshot.status,
      context: JSON.parse(JSON.stringify(snapshot.context)), hasOutput: snapshot.output !== undefined, output: snapshot.output ?? null
    }));
    return actor;
  } };
});
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const read = path => JSON.parse(readFileSync(resolve(root, path), 'utf8'));
const pin = read('src/XState/upstream.json');
const inventory = read('src/XState.Tests/examples-inventory.json');
const native = read('tmp/xstate-parity/csharp-examples.json');
const results = [];
const modules = new Map();
const clone = value => JSON.parse(JSON.stringify(value));
function inputs(name, alternate, scenario) {
  if (name === 'workflow-filling-water' && scenario === 'fractional') return { input: { current: 0.5, max: 2 } };
  if (name === 'workflow-filling-water') return { input: { current: scenario === 'already-full' ? 10 : scenario === 'over-capacity' ? 12 : 0, max: 10 } };
  if (name === 'workflow-greeting') return { input: { person: { name: alternate ? 'Ada' : 'Jenny' } } };
  if (name === 'workflow-event-greeting') return { event: { type: 'greet', greet: { name: alternate ? 'Ada' : 'Jenny' } } };
  if (name === 'workflow-math-problem') return { input: { expressions: alternate ? [] : ['2+2', '4-1', '10x3', '20/2'] } };
  if (name === 'workflow-async-function') return { input: { customer: alternate ? 'ada@example.com' : 'david@example.com' } };
  return {};
}
for (const contract of contracts) {
  const source = inventory.examples.find(example => example.name === contract.name);
  const result = { name: contract.name, sourceSha256: source.sourceSha256, checks: [] };
  results.push(result);
  for (const scenario of contract.checks) {
    it(contract.name + ' > ' + scenario, async () => {
      const startAssertions = expect.getState().assertionCalls;
      const record = { name: scenario, status: 'failed', executed: true, assertions: 0 };
      result.checks.push(record);
      const logs = [];
      const spy = vi.spyOn(console, 'log').mockImplementation((...args) => logs.push(args.map(value => value === undefined ? 'undefined' : value !== null && typeof value === 'object' ? JSON.stringify(value) : String(value)).join(' ')));
      let actor, timerSpy;
      try {
        const nativeCheck = native.examples.find(example => example.name === contract.name)?.checks.find(check => check.name === scenario);
        expect(nativeCheck?.status).toBe('passed');
        if (contract.name === 'workflow-async-subflow') {
          // Observe the actor created by the real entrypoint. Only the readline
          // transport is scripted here; the CLI stage also runs real OS pipes.
          if (scenario === 'entrypoint') expect(nativeCheck.cli.upstream.status).toBe('passed');
          vi.resetModules();
          harness.snapshots = []; harness.logs = logs; harness.answers = [scenario === 'alternate-input' ? 'Ada' : 'Jenny', '']; harness.closed = 0;
          await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'examples', contract.name, 'main.ts')).href);
          await vi.waitFor(() => expect(harness.closed).toBe(1), { timeout: 8000 });
          expect(harness.answers).toHaveLength(0);
          const observation = { snapshots: harness.snapshots, logs, completions: harness.closed };
          expect(observation).toEqual(nativeCheck.observation);
          record.observation = clone(observation); record.status = 'passed'; return;
        }
        if (!modules.has(contract.name)) {
          // Execute the unmodified upstream entrypoint, including its own sample actor.
          const module = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'examples', contract.name, 'main.ts')).href);
          modules.set(contract.name, module);
          await vi.waitFor(() => expect(logs.at(-1)).toBe('workflow completed undefined'), { timeout: 8000 });
          expect(logs).toEqual(nativeCheck.observation.logs);
          logs.length = 0;
        }
        if (scenario === 'long-first') {
          const originalTimeout = globalThis.setTimeout;
          timerSpy = vi.spyOn(globalThis, 'setTimeout').mockImplementation((callback, ms, ...args) =>
            originalTimeout(callback, ms === 1000 || ms === 3000 ? 4000 - ms : ms, ...args));
        }
        const { workflow } = modules.get(contract.name);
        const sample = inputs(contract.name, scenario === 'alternate-input', scenario);
        const snapshots = [];
        let completions = 0;
        actor = createActor(workflow, { input: sample.input });
        const completed = new Promise((resolveCompletion, reject) => actor.subscribe({
          next: snapshot => {
            snapshots.push({ value: clone(snapshot.value), status: snapshot.status, context: clone(snapshot.context),
              hasOutput: snapshot.output !== undefined, output: snapshot.output ?? null });
            if (contract.name === 'workflow-filling-water') {
              console.log('workflow state', snapshot.value);
              console.log('workflow context', snapshot.context);
            }
          },
          error: reject,
          complete: () => { completions++; console.log('workflow completed', actor.getSnapshot().output); resolveCompletion(); }
        }));
        actor.start();
        if (sample.event) actor.send(sample.event);
        await completed;
        expect(actor.getSnapshot().status).toBe('done');
        expect(completions).toBe(1);
        expect(actor.getSnapshot().output).toBeUndefined();
        const observation = { snapshots, logs, completions };
        expect(observation).toEqual(nativeCheck.observation);
        record.observation = clone(observation);
        record.status = 'passed';
      } catch (error) { record.error = String(error.stack ?? error); throw error; }
      finally { actor?.stop(); timerSpy?.mockRestore(); harness.snapshots = null; harness.logs = null; spy.mockRestore(); record.assertions = expect.getState().assertionCalls - startAssertions; }
    });
  }
}
afterAll(() => writeFileSync(resolve(root, 'tmp/xstate-parity/upstream-examples.json'), JSON.stringify({ commit: pin.commit, runtime: 'upstream', runId: native.runId, examples: results }, null, 2) + '\n'));
