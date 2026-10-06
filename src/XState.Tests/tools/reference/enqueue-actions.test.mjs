import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, enqueueActions, assign, emit } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('enqueue collection reference', () => {
  it('keeps escaped check bound to the collection snapshot', () => {
    let saved;
    const actor = createActor(createMachine({ context: { count: 7 }, entry: enqueueActions(args => {
      saved = args;
      args.enqueue.assign({ count: 42 });
      expect(args.check(({ context }) => context.count === 7)).toBe(true);
    }) })).start();
    expect(actor.getSnapshot().context.count).toBe(42);
    expect(saved.check(({ context }) => context.count === 7)).toBe(true);
    saved.enqueue.assign({ count: 99 });
    expect(actor.getSnapshot().context.count).toBe(42);
    actor.stop();
  });
  it('iterates actions appended by a running effect', () => {
    const seen = [];
    const actor = createActor(createMachine({ on: { GO: { actions: enqueueActions(({ enqueue }) => {
      enqueue(() => { seen.push(1); enqueue(() => seen.push(3)); });
      enqueue(() => seen.push(2));
    }) } } })).start();
    actor.send({ type: 'GO' });
    expect(seen).toEqual([1, 2, 3]);
    actor.stop();
  });
  it('does not inherit collector params and resolves emitted payload before later assigns', () => {
    const seen = [];
    const actor = createActor(createMachine({ context: { count: 1 }, on: { GO: { actions: { type: 'collect', params: 5 } } } }, {
      actions: { collect: enqueueActions(({ enqueue }, params) => {
        expect(params).toBe(5);
        enqueue(assign({ count: 2 }));
        enqueue(enqueueActions(({ context, enqueue }, nestedParams) => {
          expect(nestedParams).toBeUndefined();
          expect(context.count).toBe(2);
          enqueue(emit(({ context }) => ({ type: 'value', count: context.count })));
          enqueue(assign({ count: 4 }));
        }));
      }) }
    }));
    actor.on('value', event => seen.push([event.count, actor.getSnapshot().context.count]));
    actor.start();
    actor.send({ type: 'GO' });
    expect(seen).toEqual([[2, 4]]);
    actor.stop();
  });
});
