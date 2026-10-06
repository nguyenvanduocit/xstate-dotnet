import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, SimulatedClock } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('scheduler reference', () => {
  it('preserves old timers when overwriting and cancelling an ID', () => {
    const seen = [];
    const clock = new SimulatedClock();
    const actor = createActor(createMachine({ on: { '*': { actions: ({ event }) => seen.push(event.type) } } }), { clock }).start();
    actor.system.scheduler.schedule(actor, actor, { type: 'old' }, 10, 'same');
    actor.system.scheduler.schedule(actor, actor, { type: 'new' }, 20, 'same');
    expect(Object.keys(actor.system.getSnapshot()._scheduledEvents)).toHaveLength(1);
    actor.system.scheduler.cancel(actor, 'same');
    clock.increment(10);
    expect(seen).toEqual(['old']);
    actor.stop();
  });
  it('normalizes numeric after event names', () => {
    for (const [key, expected, delay] of [
      ['01', '1', 1], ['0x10', '16', 16], ['0b11', '3', 3], ['0o10', '8', 8],
      ['', '0', 0], ['-0', '0', -0], ['1e3', '1000', 1000], ['1e20', '100000000000000000000', 1e20],
      ['1e21', '1e+21', 1e21], ['1e-6', '0.000001', 1e-6], ['1e-7', '1e-7', 1e-7], ['+Infinity', 'Infinity', Infinity]
    ]) {
      const actor = createActor(createMachine({ id: 'numeric', after: { [key]: {} } }), { clock: new SimulatedClock() }).start();
      const entries = Object.values(actor.system.getSnapshot()._scheduledEvents);
      expect(entries).toHaveLength(1);
      expect(entries[0].id).toBe('xstate.after.' + expected + '.numeric');
      expect(entries[0].delay).toBe(delay);
      actor.stop();
    }
    const actor = createActor(createMachine({ after: { NaN: {} } }, { delays: { NaN: 9 } }), { clock: new SimulatedClock() }).start();
    expect(Object.values(actor.system.getSnapshot()._scheduledEvents)[0].delay).toBe(9);
    actor.stop();
  });
  it('orders integer keys before named delays', () => {
    const actor = createActor(createMachine({ id: 'keys', after: { named: {}, 20: {}, 10: {} } }, { delays: { named: 30 } }), { clock: new SimulatedClock() }).start();
    expect(Object.values(actor.system.getSnapshot()._scheduledEvents).map(entry => entry.id)).toEqual([
      'xstate.after.10.keys', 'xstate.after.20.keys', 'xstate.after.named.keys'
    ]);
    actor.stop();
  });
  it('appends after transitions to existing event transitions', () => {
    const seen = [];
    const clock = new SimulatedClock();
    const actor = createActor(createMachine({
      id: 'collision', on: { 'xstate.after.1.collision': { actions: () => seen.push(0) } },
      after: { '01': { actions: () => seen.push(2) }, '1': { actions: () => seen.push(1) } }
    }), { clock }).start();
    clock.increment(1);
    expect(seen).toEqual([0, 0]);
    actor.stop();
  });
});
