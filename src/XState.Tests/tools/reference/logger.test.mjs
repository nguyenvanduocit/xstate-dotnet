import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, initialTransition, assign, log, fromTransition } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('logger reference', () => {
  it('resolves default context/event before later assign and defers logging until start', () => {
    const calls = [];
    const actor = createActor(createMachine({ context: { count: 7 }, entry: [log(), assign({ count: 42 })], on: { LOG: { actions: log() } } }), {
      logger: (...args) => calls.push(args)
    });
    expect(calls).toEqual([]); actor.start();
    expect(calls[0][0].context).toEqual({ count: 7 }); expect(calls[0][0].event.type).toBe('xstate.init');
    const event = { type: 'LOG', data: 'payload' }; actor.send(event);
    expect(calls[1][0].context).toEqual({ count: 42 }); expect(calls[1][0].event).toBe(event); actor.stop();
  });
  it('omits empty/null labels and resolves named builtin params', () => {
    const calls = [], inspected = [];
    const actor = createActor(createMachine({ entry: [log(null), log(42, ''), log(false, ' '), { type: 'write', params: 13 }] }, {
      actions: { write: log((_, params) => params, 'label') }
    }), { logger: (...args) => calls.push(args), inspect: e => { if (e.type === '@xstate.action') inspected.push(e.action); } }).start();
    expect(calls).toEqual([[null], [42], [' ', false], ['label', 13]]);
    expect(inspected[3]).toEqual({ type: 'xstate.log', params: { value: 13, label: 'label' } }); actor.stop();
  });
  it('keeps system logger for grandchildren after a child override', () => {
    const rootCalls = [], childCalls = [];
    const rootActor = createActor(createMachine({ entry: log('root') }), { logger: (...args) => rootCalls.push(args) }).start();
    const child = createActor(createMachine({ entry: log('child'), invoke: { src: createMachine({ entry: log('grandchild') }) }, initial: 'active',
      states: { active: { on: { STOP: 'done' } }, done: { type: 'final' } }
    }), { parent: rootActor, logger: (...args) => childCalls.push(args) }).start();
    expect(rootCalls).toEqual([['root'], ['grandchild']]); expect(childCalls).toEqual([['child']]);
    expect(child.system).toBe(rootActor.system); child.send({ type: 'STOP' }); rootActor.stop();
  });
  it('turns logger failure into actor error and skips later actions', () => {
    const failure = new Error('logger failed'); let calls = 0, observed;
    const actor = createActor(createMachine({ entry: [log('fail'), () => calls++] }), { logger: () => { throw failure; } });
    actor.subscribe({ error: e => observed = e }); actor.start();
    expect(calls).toBe(0); expect(observed).toBe(failure); expect(actor.getSnapshot().status).toBe('error');
  });
  it('uses restored actor logger and exposes logger in custom logic scope', () => {
    const oldCalls = [], newCalls = [];
    const machine = createMachine({ entry: log('entry'), on: { LOG: { actions: log('event') } } });
    const first = createActor(machine, { logger: (...args) => oldCalls.push(args) }).start();
    const snapshot = first.getPersistedSnapshot(); first.stop();
    const restored = createActor(machine, { snapshot, logger: (...args) => newCalls.push(args) }).start();
    expect(newCalls).toEqual([]); restored.send({ type: 'LOG' });
    expect(oldCalls).toEqual([['entry']]); expect(newCalls).toEqual([['event']]); restored.stop();
    const scopeCalls = [];
    const custom = createActor(fromTransition((context, event, scope) => { scope.logger('scope', event.type); return context; }, 0), { logger: (...args) => scopeCalls.push(args) }).start();
    custom.send({ type: 'HELLO' }); expect(scopeCalls).toEqual([['scope', 'HELLO']]); custom.stop();
  });
  it('pure actor scope has an inert logger independent of system logger', () => {
    let logger, systemLogger;
    const custom = {
      getInitialSnapshot(scope) { logger = scope.logger; systemLogger = scope.system._logger; return { status: 'active', context: 0 }; },
      transition(snapshot) { return snapshot; }
    };
    initialTransition(custom); expect(logger).not.toBe(systemLogger);
    expect(logger('silence')).toBeUndefined();
    const [, actions] = initialTransition(createMachine({ entry: log('silence') }));
    expect(actions[0].params).toEqual({ value: 'silence', label: undefined });
    expect(actions[0].exec(actions[0].info, actions[0].params)).toBeUndefined();
  });
});
