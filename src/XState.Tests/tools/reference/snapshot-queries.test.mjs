import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, createActor, assign } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const native = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-snapshot-queries.json'), 'utf8'));
const exampleMachine = createMachine({
  initial: 'one',
  states: {
    one: { entry: ['enter'], on: {
      EXTERNAL: { target: 'one', reenter: true }, INERT: {}, INTERNAL: { actions: ['doSomething'] },
      TO_TWO: 'two', TO_TWO_MAYBE: { target: 'two', guard: function maybe() { return true; } },
      TO_THREE: 'three', FORBIDDEN_EVENT: undefined, TO_FINAL: 'success'
    } },
    two: { initial: 'deep', states: { deep: { initial: 'foo', states: {
      foo: { on: { FOO_EVENT: 'bar', FORBIDDEN_EVENT: undefined } }, bar: { on: { BAR_EVENT: 'foo' } }
    } } }, on: { DEEP_EVENT: '.' } },
    three: { type: 'parallel', states: {
      first: { initial: 'p31', states: { p31: { on: { P31: '.' } } } },
      guarded: { initial: 'p32', states: { p32: { on: { P32: '.' } } } }
    }, on: { THREE_EVENT: '.' } },
    success: { type: 'final' }
  }, on: { MACHINE_EVENT: '.two' }
});
for (const done of [false, true]) it(`snapshot status ${done ? 'done' : 'active'}`, () => {
  const actor = createActor(exampleMachine);
  try {
    if (done) { actor.start(); actor.send({ type: 'TO_FINAL' }); }
    const { status, value } = actor.getSnapshot();
    expect(status).toBe(done ? 'done' : 'active');
    expect({ status, value }).toEqual(native[done ? 'done' : 'active']);
  } finally { actor.stop(); }
});
for (let kind = 0; kind < 6; kind++) it(`snapshot can, transition kind ${kind}`, () => {
  let calls = 0; const effect = () => calls++;
  const transitions = [{ actions: 'newAction' }, { actions: assign({ count: 1 }) }, 'a', { target: 'a', actions: effect }, { actions: effect }, undefined];
  const eventType = kind < 2 ? 'NEXT' : 'EV';
  const machine = createMachine({ initial: 'a', ...(kind === 1 ? { context: { count: 0 } } : {}), states: {
    a: { ...(kind === 2 ? { entry: effect } : {}), on: { [eventType]: transitions[kind] } }
  } });
  const actor = createActor(machine);
  try {
    const snapshot = actor.getSnapshot(), can = snapshot.can({ type: eventType });
    expect(can).toBe(kind !== 5); expect(calls).toBe(0);
    if (kind === 1) expect(snapshot.context.count).toBe(0);
    expect({ can, calls, count: kind === 1 ? snapshot.context.count : 0, value: snapshot.value }).toEqual(native['can:' + kind]);
  } finally { actor.stop(); }
});
it('restored snapshot retains tags before start', () => {
  const machine = createMachine({ initial: 'a', states: { a: { tags: 'foo' } } });
  const source = createActor(machine).start(); const persisted = source.getPersistedSnapshot(); source.stop();
  const actor = createActor(machine, { snapshot: persisted });
  try {
    const snapshot = actor.getSnapshot(); expect(snapshot.hasTag('foo')).toBe(true);
    expect({ hasTag: snapshot.hasTag('foo'), tags: [...snapshot.tags], value: snapshot.value }).toEqual(native.restoredTag);
  } finally { actor.stop(); }
});
