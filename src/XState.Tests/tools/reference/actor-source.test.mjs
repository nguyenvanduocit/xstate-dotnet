import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, fromTransition, assign, spawnChild } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('actor source reference', () => {
  it('array invoke indices keep string-key semantics', () => {
    const actor = createActor(createMachine({ id: 'source',
      invoke: [{ id: 'original', src: fromTransition(s => s, 7) }],
      entry: spawnChild('xstate.invoke.00.source', { id: 'copy' })
    }));
    let seen;
    actor.subscribe({ error: error => seen = error });
    actor.start();
    expect(actor.getSnapshot().status).toBe('error');
    expect(seen).toBeInstanceOf(TypeError);
    expect(seen.message).toBe("Cannot read properties of undefined (reading 'src')");
  });
  it('root source is the original logic', () => {
    const logic = fromTransition(s => s, 7);
    expect(createActor(logic).src).toBe(logic);
  });
  it('inline invoke sources use config index and state-node ID despite custom actor ID', () => {
    const logic = fromTransition(s => s, 7);
    const actor = createActor(createMachine({
      id: 'machine', initial: 'active', states: {
        active: { id: 'active-node', invoke: [{ id: 'custom-id', src: logic }, { src: logic, onSnapshot: {} }] }
      }
    })).start();
    const children = actor.getSnapshot().children;
    expect(children['custom-id'].src).toBe('xstate.invoke.0.active-node');
    expect(children['1.active-node'].src).toBe('xstate.invoke.1.active-node');
    expect(children['custom-id']._syncSnapshot).toBe(false);
    expect(children['1.active-node']._syncSnapshot).toBe(true);
    actor.stop();
  });
  it('spawn keeps original inline or named source through both APIs', () => {
    const logic = fromTransition(s => s, 7);
    const actor = createActor(createMachine({ entry: [
      assign(({ spawn }) => {
        spawn(logic, { id: 'inline', syncSnapshot: true });
        spawn('worker', { id: 'named' });
        return {};
      }), spawnChild(logic, { id: 'action-inline' }), spawnChild('worker', { id: 'action-named' })
    ] }, { actors: { worker: logic } })).start();
    const children = actor.getSnapshot().children;
    for (const id of ['inline', 'action-inline']) expect(children[id].src).toBe(logic);
    for (const id of ['named', 'action-named']) expect(children[id].src).toBe('worker');
    expect(children.inline._syncSnapshot).toBe(true);
    actor.stop();
  });
  it('reserved invoke source names select config logic before implementations', () => {
    const logic = fromTransition(s => s, 7);
    const actor = createActor(createMachine({ id: 'source',
      invoke: [{ id: 'original', src: logic }],
      entry: spawnChild('xstate.invoke.0.source', { id: 'copy' })
    }, { actors: { 'xstate.invoke.0.source': fromTransition(s => s, 99) } })).start();
    expect(actor.getSnapshot().children.copy.getSnapshot().context).toBe(7);
    actor.stop();
  });
});
