import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, createActor, getInitialSnapshot, getNextTransitions, fromCallback } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
describe('state node reference', () => {
  it('exposes path parent machine and ID lookup identities', () => {
    const machine = createMachine({ id: 'lookup', initial: 'nested', states: { nested: { id: 'custom', initial: 'a.b', states: { 'a.b': {} } } } });
    const parent = machine.states.nested, leaf = parent.states['a.b'];
    expect(machine.root.path).toEqual([]); expect(leaf.path).toEqual(['nested', 'a.b']); expect(leaf.parent).toBe(parent); expect(leaf.machine).toBe(machine);
    expect(machine.getStateNodeById('custom')).toBe(parent); expect(machine.getStateNodeById('#custom')).toBe(parent);
    expect(machine.getStateNodeById('#custom.a\\.b')).toBe(leaf); expect(machine.getStateNodeById('lookup.nested.a\\.b')).toBe(leaf);
    expect(leaf.id).toBe('lookup.nested.a.b'); expect(() => machine.getStateNodeById('missing')).toThrow('does not exist');
  });
  it('distinguishes empty targets, targetless reentry and guard-only events', () => {
    const effect = vi.fn();
    const machine = createMachine({ on: { INERT: {}, EMPTY_TARGET: { target: [] }, GUARD_ONLY: { guard: () => true }, REENTER: { reenter: true }, ACTION: { actions: effect } } });
    expect(machine.root.ownEvents).toEqual(['EMPTY_TARGET', 'REENTER', 'ACTION']); expect(effect).not.toHaveBeenCalled();
    const snapshot = getInitialSnapshot(machine); expect(snapshot.can({ type: 'EMPTY_TARGET' })).toBe(true); expect(snapshot.can({ type: 'INERT' })).toBe(false); expect(snapshot.can({ type: 'REENTER' })).toBe(false);
    expect(machine.root.ownEvents).not.toBe(machine.root.ownEvents); expect(machine.events).toBe(machine.root.events);
  });
  it('keeps generated events in descriptor order excluding always', () => {
    const action = () => {};
    const machine = createMachine({ id: 'events', initial: 'child', on: { '10': { actions: action }, '2': { actions: action }, TEXT: { actions: action } },
      always: { guard: () => false, actions: action }, after: { 5: { actions: action } },
      invoke: { id: 'worker', src: fromCallback(() => {}), onDone: { actions: action }, onError: { actions: action }, onSnapshot: { actions: action } },
      states: { child: { on: { TEXT: { actions: action }, CHILD: { actions: action } } } }
    });
    expect(machine.events).toEqual(['2', '10', 'TEXT', 'xstate.done.actor.worker', 'xstate.error.actor.worker', 'xstate.snapshot.worker', 'xstate.after.5.events', 'CHILD']);
    expect(machine.root.after[0]).toBe(machine.root.on['xstate.after.5.events'][0]);
  });
  it('keeps metadata on all transition kinds and initial definitions', () => {
    const meta = {}, initialMeta = {}, transitionMeta = {}, action = () => {};
    const machine = createMachine({ id: 'metadata', meta, initial: { target: 'a', actions: action, meta: initialMeta, description: 'start' }, states: {
      a: { on: { GO: { meta: transitionMeta, description: 'go' } }, always: { meta: transitionMeta }, after: { 10: { meta: transitionMeta } },
        invoke: { src: fromCallback(() => {}), onDone: { meta: transitionMeta }, onError: { meta: transitionMeta }, onSnapshot: { meta: transitionMeta } } }
    } });
    const root = machine.root, child = machine.states.a;
    expect(root.meta).toBe(meta); expect(root.initial.meta).toBe(initialMeta); expect(root.initial.description).toBe('start'); expect(root.initial.eventType).toBe(null);
    expect(root.initial.source).toBe(root); expect(root.initial.target).toEqual([child]); expect(root.initial.actions).toEqual([action]);
    for (const transition of [...child.transitions.values()].flat().concat(child.always)) expect(transition.meta).toBe(transitionMeta);
    expect(child.on.GO[0].description).toBe('go'); expect(child.initial.target).toEqual([]);
  });
  it('next returns the compiled transition after guards without effects', () => {
    const guards = vi.fn().mockReturnValueOnce(false).mockReturnValueOnce(true), effect = vi.fn();
    const machine = createMachine({ on: { GO: [{ guard: guards }, { guard: guards, actions: effect }] } }); const snapshot = getInitialSnapshot(machine);
    const selected = machine.root.next(snapshot, { type: 'GO' }); expect(selected).toEqual([machine.root.on.GO[1]]);
    expect(guards).toHaveBeenCalledTimes(2); expect(effect).not.toHaveBeenCalled(); expect(machine.root.next(snapshot, { type: 'MISSING' })).toBeUndefined();
  });
  it('on omits empty arrays but transitions retains the descriptor', () => {
    const machine = createMachine({ on: { EMPTY: [] } });
    expect(machine.root.transitions.has('EMPTY')).toBe(true); expect(machine.root.on.EMPTY).toBeUndefined(); expect(machine.root.ownEvents).toEqual([]);
  });
  it('provide recompiles nodes while sharing original config and config mutations do not update node meta', () => {
    const machine = createMachine({ initial: 'a', states: { a: { on: { GO: 'b' } }, b: {} } }); const copy = machine.provide({});
    expect(copy.config).toBe(machine.config); expect(copy.root).not.toBe(machine.root); expect(copy.states.a.on.GO[0]).not.toBe(machine.states.a.on.GO[0]); expect(copy.states.a.machine).toBe(copy);
    machine.states.a.config.meta = 'later'; expect(machine.config.states.a.meta).toBe('later'); expect(machine.states.a.meta).toBeUndefined();
    expect(getNextTransitions(getInitialSnapshot(machine))[0]).toBe(machine.states.a.on.GO[0]);
  });
  it('only root and final nodes expose output declarations', () => {
    const output = () => 42;
    const machine = createMachine({ output, initial: 'a', states: { a: { output }, done: { type: 'final', output } } });
    expect(machine.root.output).toBe(output); expect(machine.states.a.output).toBeUndefined(); expect(machine.states.done.output).toBe(output);
  });
});
it('node next resolves guard implementations from the supplied snapshot', () => {
  const guard = vi.fn(() => true);
  const machine = createMachine({ on: { GO: { guard: 'check' } } }, { guards: { check: () => false } });
  const other = machine.provide({ guards: { check: guard } });
  expect(machine.root.next(getInitialSnapshot(other), { type: 'GO' })).toEqual([machine.root.on.GO[0]]);
  expect(guard).toHaveBeenCalledTimes(1);
});
it('parallel initial definitions resolve optional targets lazily', () => {
  const machine = createMachine({ type: 'parallel', initial: 'a', states: { a: {}, b: {} } });
  expect(machine.root.initial.target).toEqual([machine.states.a]);
  const invalid = createMachine({ type: 'parallel', initial: 'missing', states: { a: {} } });
  expect(invalid.root.path).toEqual([]); expect(() => invalid.root.initial).toThrow('not found');
});
