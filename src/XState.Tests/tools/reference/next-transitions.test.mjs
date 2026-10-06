import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, getNextTransitions, fromCallback, SimulatedClock } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('next transitions reference', () => {
  it('lists definitions without evaluating guards, preserving identity across calls', () => {
    let calls = 0; const inspected = [];
    const guard = () => { calls++; return false; };
    const action = () => calls++;
    const actor = createActor(createMachine({ on: { EVENT: { guard, actions: action }, EMPTY: {} } }), {
      inspect: e => { if (e.type === '@xstate.microstep') inspected.push(e); }
    }).start();
    const first = getNextTransitions(actor.getSnapshot()), second = getNextTransitions(actor.getSnapshot());
    expect(calls).toBe(0); expect(first[0].guard).toBe(guard); expect(first[0].actions[0]).toBe(action);
    expect(first).not.toBe(second); expect(first[0]).toBe(second[0]);
    actor.send({ type: 'EMPTY' }); expect(inspected[0]._transitions[0]).toBe(first[1]); actor.stop();
  });
  it('retains transitions on stopped and done snapshots', () => {
    const actor = createActor(createMachine({ on: { ROOT: {} } })).start(); actor.stop();
    expect(getNextTransitions(actor.getSnapshot()).map(t => t.eventType)).toEqual(['ROOT']);
    const done = createActor(createMachine({ initial: 'done', on: { ROOT: {} }, states: { done: { type: 'final', on: { FINAL: {} } } } })).start();
    expect(done.getSnapshot().status).toBe('done'); expect(getNextTransitions(done.getSnapshot()).map(t => t.eventType)).toEqual(['FINAL', 'ROOT']);
  });
  it('uses numeric object key ordering for events and parallel states', () => {
    const on = Object.fromEntries(['10', '02', '2', 'a', '4294967295', '0', '4294967294'].map(key => [key, {}]));
    const actor = createActor(createMachine({ on })).start();
    expect(getNextTransitions(actor.getSnapshot()).map(t => t.eventType)).toEqual(['0', '2', '10', '4294967294', '02', 'a', '4294967295']); actor.stop();
    const entered = [];
    const states = Object.fromEntries(['10', 'a', '2'].map(key => [key, { entry: () => entered.push(key), on: { ['EVENT_' + key]: {} } }]));
    const parallel = createActor(createMachine({ type: 'parallel', states })).start();
    expect(entered).toEqual(['2', '10', 'a']); expect(getNextTransitions(parallel.getSnapshot()).map(t => t.eventType)).toEqual(['EVENT_2', 'EVENT_10', 'EVENT_a']); parallel.stop();
  });
  it('lists generated invoke and delayed events before eventless transitions', () => {
    const actor = createActor(createMachine({ on: { REGULAR: {} }, onDone: {}, after: { 1000: {} }, always: { guard: () => false },
      invoke: { id: 'child', src: fromCallback(() => {}), onDone: {}, onError: {}, onSnapshot: {} }
    }), { clock: new SimulatedClock() }).start();
    expect(getNextTransitions(actor.getSnapshot()).map(t => t.eventType)).toEqual(['REGULAR', 'xstate.done.state.(machine)', 'xstate.done.actor.child', 'xstate.error.actor.child', 'xstate.snapshot.child', 'xstate.after.1000.(machine)', '']); actor.stop();
  });
  it('does not enumerate inactive siblings or history states', () => {
    const actor = createActor(createMachine({ initial: 'a', states: {
      a: { on: { ACTIVE: {} } }, b: { on: { INACTIVE: {} } }, history: { type: 'history', on: { HISTORY: {} } }
    } })).start();
    expect(getNextTransitions(actor.getSnapshot()).map(t => t.eventType)).toEqual(['ACTIVE']); actor.stop();
  });
});
