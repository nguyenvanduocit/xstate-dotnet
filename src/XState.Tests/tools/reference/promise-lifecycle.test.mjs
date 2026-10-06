import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { setTimeout as sleep } from 'node:timers/promises';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, fromPromise } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('promise lifecycle reference', () => {
  it('publishes completed promises as microtasks before timers', async () => {
    const seen = [];
    const actor = createActor(fromPromise(() => Promise.resolve(42)));
    actor.subscribe(snapshot => seen.push(snapshot.status));
    actor.start();
    expect(actor.getSnapshot().status).toBe('active');
    seen.push('turn');
    await sleep(0);
    seen.push('timer');
    expect(seen).toEqual(['active', 'turn', 'done', 'timer']);
  });
  it('creator errors keep input and do not abort the signal', () => {
    const failure = new Error('creator failed');
    let aborted = 0;
    let seen;
    const actor = createActor(fromPromise(({ signal }) => {
      signal.addEventListener('abort', () => aborted++);
      throw failure;
    }), { input: { count: 7 } });
    actor.subscribe({ error: error => seen = error });
    actor.start();
    expect(seen).toBe(failure);
    expect(actor.getSnapshot().input).toEqual({ count: 7 });
    expect(aborted).toBe(0);
  });
  it('stop ignores a late rejection and clears input', async () => {
    const deferred = Promise.withResolvers();
    const actor = createActor(fromPromise(() => deferred.promise), { input: { count: 7 } }).start();
    let calls = 0;
    actor.subscribe({ next: () => calls++, error: () => calls++ });
    actor.stop();
    deferred.reject(new Error('late'));
    await Promise.resolve();
    expect(calls).toBe(0);
    expect(actor.getSnapshot().status).toBe('stopped');
    expect(actor.getSnapshot().input).toBeUndefined();
    expect(actor.getSnapshot().output).toBeUndefined();
  });
});
