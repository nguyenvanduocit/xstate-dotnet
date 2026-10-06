import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, assign, fromCallback, initialTransition } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

// Supplemental reference probes, never counted as translated upstream tests.
describe('pre-initial snapshot reference', () => {
  it('context failure retains default value and root metadata', () => {
    const failure = new Error('context failed');
    let entries = 0;
    const machine = createMachine({
      id: 'root', tags: ['root'], meta: 'root meta', type: 'parallel',
      context: () => { throw failure; }, entry: () => entries++,
      states: {
        left: { initial: 'saving', states: { saving: { tags: ['saving'], meta: 'saving meta' } } },
        right: { initial: 'idle', states: { idle: {} } }
      }
    });
    const actor = createActor(machine);
    const snapshot = actor.getSnapshot();
    expect(snapshot.status).toBe('error');
    expect(snapshot.error).toBe(failure);
    expect(snapshot.value).toEqual({ left: 'saving', right: 'idle' });
    expect(snapshot.hasTag('root')).toBe(true);
    expect(snapshot.hasTag('saving')).toBe(false);
    expect(snapshot.getMeta()).toEqual({ root: 'root meta' });
    expect(snapshot.context).toEqual({});
    const errors = [];
    actor.subscribe({ error: e => errors.push(e) });
    actor.start();
    expect(entries).toBe(0);
    expect(errors).toEqual([failure]);
  });
  it('entry assign failure retains the initialized context', () => {
    const machine = createMachine({
      id: 'root', tags: ['root'], initial: 'a', context: () => ({ count: 7 }),
      states: { a: { tags: ['child'], entry: [
        assign({ count: 42 }), assign(() => { throw new Error('assignment failed'); })
      ] } }
    });
    const snapshot = createActor(machine).getSnapshot();
    expect(snapshot.status).toBe('error');
    expect(snapshot.context).toEqual({ count: 7 });
    expect(snapshot.matches('a')).toBe(true);
    expect(snapshot.hasTag('root')).toBe(true);
    expect(snapshot.hasTag('child')).toBe(false);
  });
  it('always guard failure discards partial context and state', () => {
    const machine = createMachine({
      initial: 'a', context: () => ({ count: 7 }),
      states: {
        a: { always: { target: 'b', actions: assign({ count: 42 }) } },
        b: { always: { guard: () => { throw new Error('guard failed'); } } }
      }
    });
    const snapshot = createActor(machine).getSnapshot();
    expect(snapshot.status).toBe('error');
    expect(snapshot.context).toEqual({ count: 7 });
    expect(snapshot.matches('a')).toBe(true);
    expect(snapshot.error.message).toBe('guard failed');
  });
  it('pure initial transition retains preceding effects without execution', () => {
    let effects = 0;
    const machine = createMachine({
      initial: 'a', context: () => ({ count: 7 }),
      states: { a: { entry: [
        () => effects++, assign(() => { throw new Error('resolve failed'); }), () => effects++
      ] } }
    });
    const [snapshot, actions] = initialTransition(machine);
    expect(snapshot.status).toBe('error');
    expect(snapshot.matches('a')).toBe(true);
    expect(snapshot.context).toEqual({ count: 7 });
    expect(actions).toHaveLength(1);
    expect(effects).toBe(0);
  });
  it('failure retains context-spawned children without starting them', () => {
    let started = 0;
    const machine = createMachine({
      context: ({ spawn }) => {
        spawn(fromCallback(() => { started++; }), { id: 'context-child' });
        return { count: 7 };
      },
      entry: assign(() => { throw new Error('entry failed'); })
    });
    const actor = createActor(machine);
    expect(actor.getSnapshot().status).toBe('error');
    expect(actor.getSnapshot().context).toEqual({ count: 7 });
    expect(Object.keys(actor.getSnapshot().children)).toEqual(['context-child']);
    actor.subscribe({ error: () => {} });
    actor.start();
    expect(started).toBe(0);
  });
  it('errored initial snapshot can inspect transitions without executing effects', () => {
    let effects = 0;
    const machine = createMachine({
      context: () => { throw new Error('context failed'); }, initial: 'saving',
      states: {
        saving: { on: { NEXT: { target: 'done', actions: () => effects++ } } },
        done: { type: 'final' }
      }
    });
    const snapshot = createActor(machine).getSnapshot();
    expect(snapshot.can({ type: 'NEXT' })).toBe(true);
    expect(snapshot.can({ type: 'UNKNOWN' })).toBe(false);
    expect(effects).toBe(0);
  });
});
