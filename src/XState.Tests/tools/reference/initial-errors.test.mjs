import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, assign } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const native = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-initial-errors.json'), 'utf8'));
const values = () => ['failure', 17, false, null, { code: 42, detail: null }, Error('exception')];
const persist = actor => JSON.parse(JSON.stringify(actor.getPersistedSnapshot()));
for (const output of [false, true]) for (const [index, reason] of values().entries()) {
  const mode = output ? 'output' : 'assign';
  it(`initial ${mode} failure ${index} preserves identity, rollback, persistence and parent error`, () => {
    const machine = createMachine({ initial: 'initial', context: () => ({ count: 0 }),
      entry: [assign({ count: 1 }), ...(output ? [] : [assign(() => { throw reason; })])],
      ...(output ? { output: () => { throw reason; } } : {}),
      states: { initial: output ? { type: 'final' } : {} }
    });
    const actor = createActor(machine), seen = [];
    actor.subscribe({ error: error => seen.push(error) });
    try {
      const snapshot = actor.getSnapshot(); expect(snapshot.status).toBe('error'); expect(snapshot.error).toBe(reason);
      expect(snapshot.context.count).toBe(0);
      const before = persist(actor); actor.start(); expect(seen).toHaveLength(1); expect(seen[0]).toBe(reason);
      const after = persist(actor); expect(after).toEqual(before);
      const restored = createActor(machine, { snapshot: after }), restoredSeen = [];
      restored.subscribe({ error: error => restoredSeen.push(error) });
      let restoredJson;
      try {
        restored.start(); expect(restoredSeen).toHaveLength(1); expect(restored.getSnapshot().status).toBe('error');
        expect(restoredSeen[0]).toBe(restored.getSnapshot().error); restoredJson = persist(restored); expect(restoredJson).toEqual(after);
      } finally { restored.stop(); }
      const parentSeen = [];
      const parent = createActor(createMachine({ initial: 'running', states: {
        running: { invoke: { src: machine, onError: { target: 'handled', actions: ({ event }) => parentSeen.push(event.error) } } }, handled: {}
      } })).start();
      try {
        expect(parentSeen).toHaveLength(1); expect(parentSeen[0]).toBe(reason); expect(parent.getSnapshot().value).toBe('handled');
        expect({ before, after, restored: restoredJson, observed: seen.length,
          parentValue: parent.getSnapshot().value, sameParentError: parentSeen[0] === reason }).toEqual(native[`${mode}:${index}`]);
      } finally { parent.stop(); }
    } finally { actor.stop(); }
  });
}
