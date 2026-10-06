import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, transition, assign } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
function ordered(eventless = false, final = false) {
  const branch = key => ({ initial: 'one', meta: key, tags: [key], on: {
    ['AGAIN_' + key]: { target: '#order.' + key, reenter: true, actions: assign({ log: ({ context }) => context.log + key }) }
  }, states: {
    one: { meta: key + '.one', tags: ['shared', key + '.one'], on: { CHANGE: { target: 'two', actions: assign({ log: ({ context }) => context.log + key }) } },
      always: eventless ? { target: 'two', guard: ({ context }) => context.log.startsWith('!'), actions: assign({ log: ({ context }) => context.log + key }) } : undefined },
    two: { meta: key + '.two', tags: ['shared', key + '.two'], type: final ? 'final' : 'atomic', on: { ['BACK_' + key]: 'one' } }
  } });
  return createMachine({ id: 'order', type: 'parallel', meta: 'root', tags: ['root', 'shared'], context: { log: '' }, states: { A: branch('A'), B: branch('B') },
    on: { ENABLE: { actions: assign({ log: '!' }) }, ASSIGN: { actions: assign({ log: ({ context }) => context.log + '.' }) } } });
}
const reversed = machine => machine.resolveState({ value: { B: 'one', A: 'one' }, context: { log: '' } });
const next = (machine, snapshot, type) => transition(machine, snapshot, { type })[0];
const view = snapshot => ({ value: JSON.stringify(snapshot.value), context: snapshot.context.log, status: snapshot.status, nodes: Object.keys(snapshot.getMeta()), tags: [...snapshot.tags] });
describe('node order reference', () => {
  it('retains original order for assignment and same-set reentry', () => {
    const machine = ordered(); const before = reversed(machine);
    expect(view(next(machine, before, 'ASSIGN')).nodes).toEqual(view(before).nodes);
    expect(view(next(machine, before, 'AGAIN_A')).nodes).toEqual(view(before).nodes);
  });
  it('selects parallel events in value order and appends newly entered leaves', () => {
    const machine = ordered(); const changed = next(machine, reversed(machine), 'CHANGE');
    expect(changed.context.log).toBe('BA'); expect(view(changed).nodes).toEqual(['order','order.B','order.A','order.A.two','order.B.two']);
    const back = next(machine, changed, 'BACK_A');
    expect(view(back).nodes).toEqual(['order','order.B','order.A','order.B.two','order.A.one']);
    expect(view(back).value).toBe('{"B":"two","A":"one"}');
  });
  it('enumerates tags in insertion order and leaves earlier snapshots unchanged', () => {
    const machine = ordered(); const before = reversed(machine); const after = next(machine, before, 'CHANGE');
    expect([...before.tags]).toEqual(['root','shared','B','A','B.one','A.one']);
    expect([...after.tags]).toEqual(['root','shared','B','A','A.two','B.two']);
  });
  it('selects eventless transitions in snapshot leaf order', () => {
    const machine = ordered(true); expect(next(machine, reversed(machine), 'ENABLE').context.log).toBe('!BA');
  });
  it('sorts final snapshot nodes in descending document order', () => {
    const machine = ordered(false, true); const snapshot = next(machine, reversed(machine), 'CHANGE');
    expect(snapshot.status).toBe('done'); expect(view(snapshot).nodes).toEqual(['order.B.two','order.B','order.A.two','order.A','order']);
  });
  it('matches C# snapshot and action ordering across the lifecycle', () => {
    const machine = ordered(); const before = reversed(machine); const changed = next(machine, before, 'CHANGE');
    const expected = { before: view(before), assigned: view(next(machine, before, 'ASSIGN')), sameSet: view(next(machine, before, 'AGAIN_A')), changed: view(changed), back: view(next(machine, changed, 'BACK_A')) };
    expect(JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-node-order.json'), 'utf8'))).toEqual(expected);
  });
  it('keeps large tag sets in first occurrence order', () => {
    const tags = Array.from({ length: 32 }, (_, i) => 'tag-' + (31 - i));
    const machine = createMachine({ tags });
    expect([...machine.resolveState({ value: {} }).tags]).toEqual(tags);
  });
  it('keeps numeric value keys in object order despite final node sorting', () => {
    const branch = enabled => ({ initial: 'one', on: enabled ? { NEXT: { target: '.two', reenter: true } } : {}, states: { one: { type: enabled ? 'atomic' : 'final' }, two: { type: 'final' } } });
    const machine = createMachine({ type: 'parallel', states: { '10': branch(false), '2': branch(true) } });
    const snapshot = next(machine, machine.resolveState({ value: {} }), 'NEXT');
    expect(snapshot.status).toBe('done'); expect(JSON.stringify(snapshot.value)).toBe('{"2":"two","10":"one"}');
  });

  it('preserves null metadata presence, duplicate IDs and numeric metadata keys', () => {
    const config = { id: 'root', initial: 'child', meta: null, states: { child: {} } }; const machine = createMachine(config);
    const meta = machine.resolveState({ value: 'child' }).getMeta();
    expect(meta).toEqual({ root: null }); expect(Object.hasOwn(meta, 'root.child')).toBe(false);
    config.states.child.meta = null; expect(machine.resolveState({ value: 'child' }).getMeta()).toEqual({ root: null });
    const duplicate = createMachine({ id: 'same', initial: 'child', meta: 'root', states: { child: { id: 'same', meta: 'child' } } });
    expect(duplicate.resolveState({ value: 'child' }).getMeta()).toEqual({ same: 'child' });
    const numeric = createMachine({ id: '10', initial: 'child', meta: 'parent', states: { child: { id: '2', meta: 'child' } } });
    expect(Object.keys(numeric.resolveState({ value: 'child' }).getMeta())).toEqual(['2', '10']);
  });

});
