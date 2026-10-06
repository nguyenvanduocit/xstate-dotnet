import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, fromObservable, fromEventObservable, assign } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('observable lifecycle reference', () => {
  it('queues synchronous values before completion and does not unsubscribe again', () => {
    let disposals = 0;
    const actor = createActor(fromObservable(() => ({ subscribe(observer) {
      observer.next(1); observer.next(2); observer.complete();
      return { unsubscribe() { disposals++; } };
    } })), { input: 42 });
    const snapshots = [];
    actor.subscribe(s => snapshots.push([s.status, s.context]));
    actor.start();
    actor.stop();
    expect(snapshots).toEqual([['active', undefined], ['active', 1], ['active', 2], ['done', 2]]);
    expect(disposals).toBe(0);
    expect(actor.getSnapshot().input).toBeUndefined();
  });
  it('error keeps last context, clears input and does not unsubscribe again', () => {
    const failure = new Error('observable failure');
    let listener;
    let disposals = 0;
    let seen;
    const actor = createActor(fromObservable(() => ({ subscribe(observer) {
      listener = observer;
      return { unsubscribe() { disposals++; } };
    } })), { input: 42 });
    actor.subscribe({ error: error => seen = error });
    actor.start();
    listener.next(7); listener.error(failure); actor.stop();
    expect(seen).toBe(failure);
    expect(actor.getSnapshot()).toMatchObject({ status: 'error', context: 7, input: undefined, _subscription: undefined });
    expect(disposals).toBe(0);
  });
  it('stop ignores reentrant events from teardown and disposes once', () => {
    let disposals = 0;
    const actor = createActor(fromObservable(() => ({ subscribe(observer) {
      observer.next(7);
      return { unsubscribe() { disposals++; observer.next(99); observer.complete(); } };
    } })), { input: 12 });
    const values = [];
    actor.subscribe(s => { if (s.context !== undefined) values.push(s.context); });
    actor.start(); actor.stop(); actor.stop();
    expect(disposals).toBe(1);
    expect(values).toEqual([7]);
    expect(actor.getSnapshot()).toMatchObject({ status: 'stopped', context: 7, input: undefined });
  });
  it('event observable forwards values without changing its context', () => {
    const childLogic = fromEventObservable(() => ({ subscribe(observer) {
      observer.next({ type: 'A' }); observer.next({ type: 'B' }); observer.complete();
      return { unsubscribe() {} };
    } }));
    const seen = [];
    const parent = createActor(createMachine({
      context: ({ spawn }) => ({ child: spawn(childLogic, { id: 'event' }) }),
      on: { '*': { actions: ({ event }) => seen.push(event.type) } }
    })).start();
    expect(seen).toEqual(['A', 'B', 'xstate.done.actor.event']);
    expect(parent.getSnapshot().context.child.getSnapshot()).toMatchObject({ status: 'done', context: undefined });
    parent.stop();
  });
  it('persistence removes subscription and resubscribes active snapshots', () => {
    const listeners = [];
    const disposals = [];
    const logic = fromObservable(() => ({ subscribe(observer) {
      const index = listeners.push(observer) - 1;
      disposals.push(0);
      return { unsubscribe() { disposals[index]++; } };
    } }));
    const actor = createActor(logic, { input: 42 }).start();
    listeners[0].next(7);
    const persisted = actor.getPersistedSnapshot();
    expect('_subscription' in persisted).toBe(false);
    actor.stop();
    const restored = createActor(logic, { snapshot: persisted }).start();
    expect(restored.getSnapshot()).toMatchObject({ context: 7, input: 42 });
    listeners[1].next(8);
    expect(restored.getSnapshot().context).toBe(8);
    restored.stop();
    expect(disposals).toEqual([1, 1]);
  });
});
