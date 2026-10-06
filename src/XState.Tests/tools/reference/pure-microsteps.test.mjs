import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, getInitialSnapshot, getNextSnapshot, getMicrosteps, getInitialMicrosteps, assign, raise, fromCallback } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const effect = () => { throw new Error('Pure effect executed'); };
const child = fromCallback(() => { throw new Error('Pure child started'); });
describe('pure microstep reference', () => {
  it('isolates per-step context and actions', () => {
    const machine = createMachine({ context: { count: 0 }, initial: 'a', states: {
      a: { on: { GO: { target: 'b', actions: [assign({ count: 1 }), effect] } } },
      b: { always: { target: 'c', actions: [assign({ count: 2 }), effect] } }, c: {}
    } });
    const initial = getInitialSnapshot(machine); const steps = getMicrosteps(machine, initial, { type: 'GO' });
    expect(initial.context.count).toBe(0); expect(steps.map(([s]) => s.context.count)).toEqual([1, 2]);
    expect(steps.map(([, a]) => a.map(action => action.info.context.count))).toEqual([[1], [2]]);
    expect(steps[0][1]).not.toBe(steps[1][1]);
  });
  it('preserves no-op identity and skips init event dispatch', () => {
    const machine = createMachine({ on: { 'xstate.init': { actions: effect } } }); const initial = getInitialSnapshot(machine);
    const steps = getMicrosteps(machine, initial, { type: 'UNKNOWN' });
    expect(steps).toHaveLength(1); expect(steps[0][0]).toBe(initial); expect(steps[0][1]).toEqual([]);
    expect(getMicrosteps(machine, initial, { type: 'xstate.init' })).toEqual([]);
    expect(getNextSnapshot(machine, initial, { type: 'xstate.init' })).toBe(initial);
  });
  it('selects external transitions on terminal input snapshots', () => {
    const machine = createMachine({ initial: 'a', states: { a: { on: { GO: { target: 'b', actions: effect } } }, b: {} } });
    for (const status of ['stopped', 'error', 'done']) {
      const initial = machine.resolveState({ value: 'a', status }); const steps = getMicrosteps(machine, initial, { type: 'GO' });
      expect(steps).toHaveLength(1); expect(steps[0][0].value).toBe('b'); expect(steps[0][0].status).toBe(status); expect(steps[0][1]).toHaveLength(1);
      expect(getNextSnapshot(machine, initial, { type: 'GO' }).value).toBe('b');
    }
  });
  it('stop excludes child cleanup actions and always clones snapshot', () => {
    const machine = createMachine({ invoke: { id: 'child', src: child } }); const initial = getInitialSnapshot(machine);
    const [[stopped, actions]] = getMicrosteps(machine, initial, { type: 'xstate.stop' });
    expect(stopped.status).toBe('stopped'); expect(stopped.children).toEqual({}); expect(actions).toEqual([]); expect(Object.keys(initial.children)).toEqual(['child']);
    expect(getMicrosteps(machine, stopped, { type: 'xstate.stop' })[0][0]).not.toBe(stopped);
    const failure = new Error('child failed'); const [[failed, failedActions]] = getMicrosteps(machine, initial, { type: 'xstate.error.actor.child', error: failure });
    expect(failed.status).toBe('error'); expect(failed.error).toBe(failure); expect(failedActions).toEqual([]);
  });
  it('final snapshot keeps children and excludes later stop effects', () => {
    const machine = createMachine({ invoke: { id: 'child', src: child }, initial: 'a', states: {
      a: { on: { GO: 'done' } }, done: { type: 'final', entry: effect, exit: effect }
    } });
    const [[snapshot, actions]] = getMicrosteps(machine, getInitialSnapshot(machine), { type: 'GO' });
    expect(snapshot.status).toBe('done'); expect(Object.keys(snapshot.children)).toEqual(['child']); expect(actions).toHaveLength(2);
  });
  it('initial microsteps propagate initializer and resolution errors', () => {
    const failure = new Error('resolution failed');
    for (const machine of [createMachine({ context: () => { throw failure; } }), createMachine({ context: {}, entry: assign(() => { throw failure; }) })]) {
      expect(getInitialSnapshot(machine).status).toBe('error'); expect(() => getInitialMicrosteps(machine)).toThrow(failure);
    }
  });
  it('includes initial entry and raised event without starting child', () => {
    const machine = createMachine({ invoke: { id: 'child', src: child }, initial: 'a', states: {
      a: { entry: raise({ type: 'NEXT' }), on: { NEXT: { target: 'b', actions: effect } } }, b: {}
    } });
    const steps = getInitialMicrosteps(machine); expect(steps.map(([s]) => s.value)).toEqual(['a', 'b']);
    expect(steps.map(([, a]) => a.length)).toEqual([2, 1]); expect(steps[1][1][0].info.event.type).toBe('NEXT');
  });
  it('does not mutate previous step children or action arrays', () => {
    const machine = createMachine({ initial: 'a', states: {
      a: { on: { GO: 'b' } }, b: { invoke: { id: 'child', src: child }, always: 'c' }, c: {}
    } });
    const steps = getMicrosteps(machine, getInitialSnapshot(machine), { type: 'GO' });
    expect(Object.keys(steps[0][0].children)).toEqual(['child']); expect(steps[1][0].children).toEqual({});
    expect(steps.map(([, a]) => a.map(action => action.type))).toEqual([['xstate.spawnChild'], ['xstate.stopChild']]);
  });
});
it('pure microsteps preserve failure through transition and stop', () => {
  const machine = createMachine({ initial: 'a', states: { a: { on: { GO: 'b' } }, b: {} } });
  const failure = new Error('original failure');
  const [[initial]] = getMicrosteps(machine, getInitialSnapshot(machine), { type: 'xstate.error.actor.child', error: failure });
  for (const type of ['GO', 'xstate.stop']) {
    expect(getMicrosteps(machine, initial, { type })[0][0].error).toBe(failure);
    expect(getNextSnapshot(machine, initial, { type }).error).toBe(failure);
  }
});
it('pure microsteps reject a literal wildcard event', () => {
  const machine = createMachine({});
  expect(() => getMicrosteps(machine, getInitialSnapshot(machine), { type: '*' })).toThrow("An event cannot have the wildcard type ('*')");
});
