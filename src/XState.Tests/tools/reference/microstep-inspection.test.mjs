import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, assign, raise } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('microstep inspection reference', () => {
  it('retains unknown and action-only snapshot identity and final microstep identity', () => {
    const steps = [];
    const actor = createActor(createMachine({ initial: 'a', states: {
      a: { on: { EFFECT: { actions: () => {} }, GO: 'b' } }, b: { always: 'c' }, c: {}
    } }), { inspect: e => { if (e.type === '@xstate.microstep') steps.push(e); } }).start();
    const initial = actor.getSnapshot();
    actor.send({ type: 'unknown' }); actor.send({ type: 'EFFECT' }); actor.send({ type: 'GO' });
    expect(steps.map(e => e.snapshot.value)).toEqual(['a', 'a', 'b', 'c']);
    expect(steps[0].snapshot).toBe(initial); expect(steps[1].snapshot).toBe(initial);
    expect(steps[3].snapshot).toBe(actor.getSnapshot());
    actor.stop();
  });
  it('preserves descriptor and intermediate context across eventless steps', () => {
    const steps = [];
    const actor = createActor(createMachine({ context: { count: 0 }, initial: 'a', states: {
      a: { on: { 'go.*': { target: 'b', actions: assign({ count: ({ context }) => context.count + 1 }) } } },
      b: { always: { target: 'c', actions: assign({ count: ({ context }) => context.count + 1 }) } }, c: {}
    } }), { inspect: e => { if (e.type === '@xstate.microstep') steps.push(e); } }).start();
    const event = { type: 'go.now' }; actor.send(event);
    expect(steps.map(e => [e.event === event, e.snapshot.context.count, e._transitions[0].eventType])).toEqual([[true, 1, 'go.*'], [true, 2, '']]);
    actor.stop();
  });
  it('reports parent stop before deferred child stop', () => {
    const steps = [];
    const actor = createActor(createMachine({ invoke: { id: 'worker', src: createMachine({}) } }), {
      inspect: e => { if (e.type === '@xstate.microstep') steps.push(e); }
    }).start();
    const worker = actor.getSnapshot().children.worker;
    worker.send({ type: 'unknown' }); actor.stop();
    expect(steps.map(e => [e.actorRef === worker ? 'child' : 'parent', e.snapshot.status, e._transitions.length])).toEqual([
      ['child', 'active', 0], ['parent', 'stopped', 0], ['child', 'stopped', 0]
    ]);
    expect(steps.every(e => e.rootId === actor.sessionId)).toBe(true);
  });
  it('reports raised initialization steps before init and preserves definition identity', () => {
    const events = [];
    const actor = createActor(createMachine({ initial: 'a', states: {
      a: { entry: raise({ type: 'to_b' }), on: { to_b: 'b' } },
      b: { entry: raise({ type: 'to_c' }), on: { to_c: 'c' } }, c: { on: { SAME: {} } }
    } }), { inspect: e => events.push(e) });
    expect(events.map(e => e.type)).toEqual(['@xstate.actor', '@xstate.microstep', '@xstate.microstep']);
    expect(events.slice(1).map(e => [e.event.type, e.snapshot.value])).toEqual([['to_b', 'b'], ['to_c', 'c']]);
    actor.start(); actor.send({ type: 'SAME' }); actor.send({ type: 'SAME' });
    const same = events.filter(e => e.type === '@xstate.microstep' && e.event.type === 'SAME');
    expect(same[0]._transitions[0]).toBe(same[1]._transitions[0]);
    actor.stop();
  });
  it('reports unhandled actor error snapshot and no transitions', () => {
    const steps = [];
    const actor = createActor(createMachine({}), { inspect: e => { if (e.type === '@xstate.microstep') steps.push(e); } });
    actor.subscribe({ error: () => {} }); actor.start();
    const error = new Error('child failure'); actor.send({ type: 'xstate.error.actor.child', error, actorId: 'child' });
    expect(steps).toHaveLength(1); expect(steps[0]._transitions).toEqual([]);
    expect(steps[0].snapshot.status).toBe('error'); expect(steps[0].snapshot.error).toBe(error);
    expect(steps[0].snapshot).toBe(actor.getSnapshot());
  });
});

it('keeps snapshot identity for action-only and reentry but replaces it after a round trip', () => {
  for (const inspect of [undefined, () => {}]) {
    let count = 0;
    const actor = createActor(createMachine({ initial: 'a', states: {
      a: { entry: () => count++, on: { EFFECT: { actions: () => count++ }, SELF: { target: 'a', reenter: true }, GO: 'b' } },
      b: { always: 'a' }
    } }), { inspect }).start();
    const initial = actor.getSnapshot();
    actor.send({ type: 'EFFECT' }); expect(actor.getSnapshot()).toBe(initial);
    actor.send({ type: 'SELF' }); expect(actor.getSnapshot()).toBe(initial); expect(count).toBe(3);
    actor.send({ type: 'GO' }); expect(actor.getSnapshot().value).toBe('a'); expect(actor.getSnapshot()).not.toBe(initial);
    actor.stop();
  }
});
it('retains microstep snapshot after inspector unsubscribes during delivery', () => {
  const actor = createActor(createMachine({ initial: 'a', states: { a: { on: { GO: 'b' } }, b: {} } })).start();
  let observed;
  const sub = actor.system.inspect(e => { if (e.type === '@xstate.microstep') { observed = e.snapshot; sub.unsubscribe(); } });
  actor.send({ type: 'GO' }); expect(actor.getSnapshot()).toBe(observed); actor.stop();
});
it('retains stopped child reference in completed parent microstep', () => {
  const steps = [];
  const actor = createActor(createMachine({ invoke: { id: 'child', src: createMachine({}) }, initial: 'a', states: {
    a: { on: { GO: 'done' } }, done: { type: 'final' }
  } }), { inspect: e => { if (e.type === '@xstate.microstep') steps.push(e); } }).start();
  const child = actor.getSnapshot().children.child;
  actor.send({ type: 'GO' }); const parentStep = steps.find(e => e.actorRef === actor);
  expect(parentStep.snapshot).toBe(actor.getSnapshot()); expect(parentStep.snapshot.children.child).toBe(child);
  expect(parentStep.snapshot.status).toBe('done'); expect(child.getSnapshot().status).toBe('stopped');
});
