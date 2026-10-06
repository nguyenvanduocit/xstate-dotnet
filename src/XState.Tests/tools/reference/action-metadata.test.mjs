import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, initialTransition, assign, raise, cancel, emit, sendTo, spawnChild, stopChild, enqueueActions, fromCallback } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('action metadata reference', () => {
  it('inspects immediately before execution and preserves undefined versus null', () => {
    const order = []; const actions = [];
    const actor = createActor(createMachine({ entry: ['custom', { type: 'custom', params: null }] }, {
      actions: { custom: (_, params) => order.push(params === undefined ? 'effect:missing' : 'effect:null') }
    }), { inspect: e => { if (e.type === '@xstate.action') { actions.push(e.action); order.push('inspect'); } } });
    expect(order).toEqual([]); actor.start();
    expect(order).toEqual(['inspect', 'effect:missing', 'inspect', 'effect:null']);
    expect(actions.map(a => a.params)).toEqual([undefined, null]); actor.stop();
  });
  it('keeps missing implementation as a no-op executable and inspects its params', () => {
    const machine = createMachine({ entry: { type: 'missing', params: 7 } });
    const [, actions] = initialTransition(machine); expect(actions[0].exec).toBeUndefined();
    const events = []; const actor = createActor(machine, { inspect: e => { if (e.type === '@xstate.action') events.push(e.action); } }).start();
    expect(events).toEqual([{ type: 'missing', params: 7 }]); actor.stop();
  });
  it('captures action info before assignments without running effects', () => {
    const seen = [];
    const [, actions] = initialTransition(createMachine({ context: { count: 40 }, entry: [
      ({ context }) => seen.push(context.count), assign({ count: ({ context }) => context.count + 1 }), ({ context }) => seen.push(context.count)
    ] }));
    expect(seen).toEqual([]); expect(actions.map(a => a.info.context.count)).toEqual([40, 41]);
    for (const action of actions) action.exec(action.info, action.params);
    expect(seen).toEqual([40, 41]);
  });
  it('exposes builtins and omits assign and enqueue collector records', () => {
    const src = fromCallback(() => {});
    const [, actions] = initialTransition(createMachine({ context: { count: 0 }, entry: [assign({ count: 1 }), enqueueActions(({ enqueue }) => {
      enqueue(spawnChild(src, { id: 'child', systemId: 'worker', input: 42 }));
      enqueue(sendTo('child', { type: 'HELLO' })); enqueue(raise({ type: 'RAISED' }));
      enqueue(cancel('none')); enqueue(emit({ type: 'NOTICE' })); enqueue(stopChild('child')); enqueue(stopChild('missing'));
    })] }));
    expect(actions.map(a => a.type)).toEqual(['xstate.spawnChild', 'xstate.sendTo', 'xstate.raise', 'xstate.cancel', 'xstate.emit', 'xstate.stopChild', 'xstate.stopChild']);
    expect(actions[0].params).toMatchObject({ id: 'child', systemId: 'worker', src, input: 42 });
    expect(actions[1].params.to).toBe(actions[0].params.actorRef); expect(actions[5].params).toBe(actions[0].params.actorRef);
    expect(actions[6].params).toBeUndefined(); expect(actions.map(a => a.info.context.count)).toEqual([1, 1, 1, 1, 1, 1, 1]);
  });
  it('mutates deferred send target after inspection of a running entry action', () => {
    let immediate; let recorded;
    const actor = createActor(createMachine({ initial: 'a', states: { a: { on: { GO: 'b' } }, b: {
      entry: sendTo('child', { type: 'PING' }), invoke: { id: 'child', src: fromCallback(() => {}) }
    } } }), { inspect: e => { if (e.type === '@xstate.action' && e.action.type === 'xstate.sendTo') { recorded = e.action.params; immediate = recorded.to; } } }).start();
    actor.send({ type: 'GO' }); expect(immediate).toBe('child'); expect(recorded.to).toBe(actor.getSnapshot().children.child); actor.stop();
  });
  it('clones snapshot for missing stop child even without inspection', () => {
    for (const inspect of [undefined, () => {}]) {
      const actor = createActor(createMachine({ on: { STOP: { actions: stopChild('missing') } } }), { inspect }).start();
      const initial = actor.getSnapshot(); actor.send({ type: 'STOP' });
      expect(actor.getSnapshot()).not.toBe(initial); expect(actor.getSnapshot().children).toBe(initial.children); actor.stop();
    }
  });
  it('prevents execution and reports error when action inspector throws', () => {
    const error = new Error('inspection failure'); let calls = 0; let observed;
    const actor = createActor(createMachine({ entry: () => calls++ }), { inspect: e => { if (e.type === '@xstate.action') throw error; } });
    actor.subscribe({ error: e => observed = e }); actor.start();
    expect(calls).toBe(0); expect(observed).toBe(error); expect(actor.getSnapshot().status).toBe('error');
  });
  it('uses builtin type and resolved params for named builtin implementation', () => {
    const [, actions] = initialTransition(createMachine({ entry: { type: 'alias', params: 42 } }, {
      actions: { alias: raise((_, params) => ({ type: 'VALUE', value: params })) }
    }));
    expect(actions).toHaveLength(1); expect(actions[0].type).toBe('xstate.raise'); expect(actions[0].params.event.value).toBe(42);
  });
});

it('missing stop in always transition keeps selecting until iteration limit', () => {
  const machine = createMachine({ options: { maxIterations: 2 }, always: { actions: stopChild('missing') } });
  const [snapshot] = initialTransition(machine);
  expect(snapshot.status).toBe('error'); expect(snapshot.error.message).toContain('more than 2 microsteps');
});

