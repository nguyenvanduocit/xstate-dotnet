import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, fromCallback, transition } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const parallel = () => createMachine({ type: 'parallel', states: {
  A: { initial: 'A1', states: { A1: {}, A2: {} } }, B: { initial: 'B1', states: { B1: {}, B2: {} } }
} });
describe('resolve state reference', () => {
  it('normalizes redundant compound branches before deriving status and tags', () => {
    const machine = createMachine({ initial: 'first', states: { first: { type: 'final', tags: ['first'] }, second: { tags: ['second'] } } });
    const snapshot = machine.resolveState({ value: { second: {}, first: {} } });
    expect(snapshot.value).toBe('second'); expect(snapshot.status).toBe('active'); expect([...snapshot.tags]).toEqual(['second']);
  });
  it('keeps supplied parallel region ordering', () => {
    const snapshot = parallel().resolveState({ value: { B: 'B2', A: 'A2' } });
    expect(JSON.stringify(snapshot.value)).toBe('{"B":"B2","A":"A2"}');
    expect(snapshot._nodes.map(node => node.key)).toEqual(['(machine)', 'B', 'A', 'B2', 'A2']);
  });
  it('does not run initial actions context creators invoke callbacks or eventless transitions', () => {
    let calls = 0;
    const machine = createMachine({ context: () => { calls++; return {}; }, initial: 'a', entry: () => calls++, states: {
      a: { initial: { target: 'nested', actions: () => calls++ }, states: { nested: { always: '#done' } }, invoke: { src: fromCallback(() => { calls++; }) } },
      b: { id: 'done' }
    } });
    const context = { n: 42 }; const snapshot = machine.resolveState({ value: 'a', context });
    expect(snapshot.value).toEqual({ a: 'nested' }); expect(snapshot.context).toBe(context); expect(calls).toBe(0); expect(snapshot.children).toEqual({});
  });
  it('keeps explicit history nodes without activating initial siblings', () => {
    const machine = createMachine({ initial: 'a', states: { a: {}, h: { type: 'history', history: 'deep' } } });
    expect(machine.resolveState({ value: 'h' }).value).toEqual({ h: {} });
  });
  it('retains error output and status without evaluating output functions', () => {
    let outputs = 0;
    const machine = createMachine({ initial: 'active', output: () => { outputs++; return 'unexpected'; }, states: {
      active: {}, done: { type: 'final', output: () => { outputs++; return 'unexpected'; } }
    } });
    const error = new Error('failure'); const output = {};
    for (const status of ['active', 'stopped', 'done', 'error']) {
      const snapshot = machine.resolveState({ value: 'active', status, output, error });
      expect(snapshot.status).toBe(status); expect(snapshot.error).toBe(error); expect(snapshot.output).toBe(output);
    }
    const snapshot = machine.resolveState({ value: 'done', status: 'error', output, error });
    expect(snapshot.status).toBe('done'); expect(snapshot.error).toBe(error); expect(snapshot.output).toBe(output); expect(outputs).toBe(0);
  });
  it('uses supplied history references on subsequent history transitions', () => {
    const machine = createMachine({ id: 'history', initial: 'off', states: {
      off: { on: { GO: 'group.h' } }, group: { initial: 'a', states: { a: {}, b: {}, h: { type: 'history', history: 'deep' } } }
    } });
    const historyValue = { 'history.group.h': [machine.getStateNodeById('history.group.b')] };
    const snapshot = machine.resolveState({ value: 'off', historyValue });
    expect(snapshot.historyValue).toBe(historyValue);
    expect(transition(machine, snapshot, { type: 'GO' })[0].value).toEqual({ group: 'b' });
  });
  it('retains multiple compound children when the first child is non-atomic', () => {
    const machine = createMachine({ initial: 'a', states: { a: { initial: 'nested', states: { nested: {} } }, b: { type: 'final' } } });
    const snapshot = machine.resolveState({ value: { a: {}, b: {} } });
    expect(snapshot.value).toEqual({ a: 'nested', b: {} }); expect(snapshot.status).toBe('done');
  });
  it('orders numeric keys according to JavaScript property order', () => {
    const machine = createMachine({ initial: '2', states: { '10': {}, '2': {}, last: {} } });
    expect(machine.resolveState({ value: { '10': {}, '2': {} } }).value).toBe('2');
  });
  it('matches C# normalization, status and node order across 160 partial configurations', () => {
    const vectors = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-resolve-vectors.json'), 'utf8'));
    expect(vectors).toHaveLength(160);
    const machines = new Map(['compound', 'parallel'].map(kind => {
      const config = { id: 'vectors', type: kind, initial: 'A', states: {
        A: { initial: 'one', states: { one: {}, two: { type: 'final' } } },
        B: { type: 'final' },
        C: { type: 'parallel', states: { left: {}, right: { initial: 'one', states: { one: {}, two: { type: 'final' } } } } }
      } };
      function mark(node, id) { node.meta = id; for (const [key, child] of Object.entries(node.states ?? {})) mark(child, id + '.' + key); }
      mark(config, 'vectors'); return [kind, createMachine(config)];
    }));
    for (const vector of vectors) {
      const snapshot = machines.get(vector.kind).resolveState({ value: JSON.parse(vector.input), context: {} });
      expect(vector.value, vector.kind + ':' + vector.input).toBe(JSON.stringify(snapshot.value));
      expect(vector.status).toBe(snapshot.status);
      expect(vector.nodes).toEqual(Object.keys(snapshot.getMeta()));
    }
  });

});
