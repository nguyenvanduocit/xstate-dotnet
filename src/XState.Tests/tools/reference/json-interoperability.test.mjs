import { readFileSync, writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, fromTransition } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const counter = fromTransition((s, ev) => ev.type === 'INC' ? s + 1 : s, 7);
const machine = createMachine({ context: ({ spawn }) => {
  const child = spawn('counter', { id: 'child', systemId: 'counter-system', syncSnapshot: true });
  return { child, array: [child], list: [child], map: { ref: child } };
} }, { actors: { counter } });

describe('snapshot JSON interoperability', () => {
  it('upstream restores a snapshot serialized by the native C# runtime', () => {
    const snapshot = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-json-snapshot.json'), 'utf8'));
    const actor = createActor(machine, { snapshot }).start();
    const state = actor.getSnapshot(); const child = state.children.child;
    expect(state.context.child).toBe(child); expect(state.context.array[0]).toBe(child);
    expect(state.context.list[0]).toBe(child); expect(state.context.map.ref).toBe(child);
    expect(actor.system.get('counter-system')).toBe(child);
    child.send({ type: 'INC' }); expect(child.getSnapshot().context).toBe(8); actor.stop();
  });
  it('exports an upstream JSON fixture for the native restoration test', () => {
    const actor = createActor(machine).start();
    const serialized = JSON.stringify(actor.getPersistedSnapshot());
    expect(JSON.parse(serialized)).toEqual(JSON.parse(readFileSync(resolve(root, 'src/XState.Tests/fixtures/upstream-json-snapshot.json'), 'utf8')));
    writeFileSync(resolve(root, 'tmp/xstate-parity/upstream-json-snapshot.json'), serialized);
    const restored = createActor(machine, { snapshot: JSON.parse(serialized) }).start();
    expect(restored.getSnapshot().context.child).toBe(restored.getSnapshot().children.child);
    actor.stop(); restored.stop();
  });
});
