import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, fromTransition } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('machine persistence reference', () => {
  const counter = fromTransition((s, ev) => ev.type === 'INC' ? s + 1 : s, 1);
  it('copies actor-reference branches and shares unchanged context data', () => {
    const unchanged = { number: 7 };
    const machine = createMachine({ context: ({ spawn }) => {
      const child = spawn('counter', { id: 'child' });
      return { direct: child, array: [child], nested: { child }, unchanged };
    } }, { actors: { counter } });
    const actor = createActor(machine).start();
    const original = actor.getSnapshot().context;
    const persisted = actor.getPersistedSnapshot();
    expect(persisted.context).not.toBe(original);
    expect(persisted.context.direct).toEqual({ xstate$$type: 1, id: 'child' });
    expect(persisted.context.unchanged).toBe(unchanged);
    const restored = createActor(machine, { snapshot: persisted }).start();
    const child = restored.getSnapshot().children.child;
    const context = restored.getSnapshot().context;
    expect(context.direct).toBe(child); expect(context.array[0]).toBe(child); expect(context.nested.child).toBe(child);
    expect(original.direct).not.toBe(child); expect(context.unchanged).toBe(unchanged);
    actor.stop(); restored.stop();
  });
  it('restore mutates persisted context and reusing it retains the first revived reference', () => {
    const machine = createMachine({ context: ({ spawn }) => ({ child: spawn('counter', { id: 'child' }) }) }, { actors: { counter } });
    const actor = createActor(machine).start(); const snapshot = actor.getPersistedSnapshot(); actor.stop();
    const first = createActor(machine, { snapshot }).start();
    expect(snapshot.context).toBe(first.getSnapshot().context);
    expect(snapshot.context.child).toBe(first.getSnapshot().children.child);
    const second = createActor(machine, { snapshot }).start();
    expect(second.getSnapshot().context.child).toBe(first.getSnapshot().children.child);
    expect(second.getSnapshot().context.child).not.toBe(second.getSnapshot().children.child);
    first.stop(); second.stop();
  });
  it('missing child implementations clear reference markers on restore', () => {
    const machine = createMachine({ context: ({ spawn }) => ({ child: spawn('counter', { id: 'child' }) }) }, { actors: { counter } });
    const actor = createActor(machine).start(); const snapshot = actor.getPersistedSnapshot(); actor.stop();
    snapshot.children.child.src = 'missing';
    const restored = createActor(machine, { snapshot }).start();
    expect(restored.getSnapshot().children).toEqual({});
    expect(restored.getSnapshot().context.child).toBeUndefined(); restored.stop();
  });
  it('restore chooses destination implementation for a named child', () => {
    const machine = createMachine({ invoke: { id: 'child', src: 'counter' } }, { actors: { counter } });
    const actor = createActor(machine).start(); const snapshot = actor.getPersistedSnapshot(); actor.stop();
    const next = machine.provide({ actors: { counter: fromTransition((s, ev) => ev.type === 'INC' ? s + 100 : s, 0) } });
    const restored = createActor(next, { snapshot }).start();
    restored.getSnapshot().children.child.send({ type: 'INC' });
    expect(restored.getSnapshot().children.child.getSnapshot().context).toBe(101); restored.stop();
  });
  it('unsafe inline allowance propagates to nested children', () => {
    const nested = createMachine({ context: ({ spawn }) => { spawn(counter, { id: 'grandchild' }); return {}; } });
    const machine = createMachine({ context: ({ spawn }) => { spawn(nested, { id: 'child' }); return {}; } });
    const actor = createActor(machine).start();
    expect(() => actor.getPersistedSnapshot()).toThrow('An inline child actor cannot be persisted.');
    const snapshot = actor.getPersistedSnapshot({ __unsafeAllowInlineActors: true });
    const restored = createActor(machine, { snapshot }).start();
    expect(restored.getSnapshot().children.child.getSnapshot().children.grandchild.getSnapshot().context).toBe(1);
    actor.stop(); restored.stop();
  });
});
