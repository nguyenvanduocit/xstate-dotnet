import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createMachine, fromCallback } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const { getStateNodes, toDirectedGraph } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/graph/index.ts')).href);
function diagram() {
  return createMachine({ id: 'light', initial: 'green', states: {
    green: { on: { TIMER: 'yellow' } }, yellow: { on: { TIMER: 'red' } },
    red: { initial: 'walk', states: { walk: { on: { COUNTDOWN: 'wait' } }, wait: { on: { COUNTDOWN: 'stop' } }, stop: { on: { COUNTDOWN: 'finished' } }, finished: { type: 'final' } }, onDone: 'green' }
  } });
}
function multi() {
  return createMachine({ id: 'graph', initial: 'branch', on: {
    BLOCK: {}, EMPTY: { target: [] }, GO: [{ target: ['#x', '#y'], meta: {} }, { target: '#x', guard: () => false }], NO_BRANCHES: []
  }, states: { branch: { type: 'parallel', states: { x: { id: 'x', on: { BACK: '#graph' } }, y: { id: 'y' } } } } });
}
describe('directed graph reference', () => {
  it('matches the original upstream snapshot fixture and C# JSON graph', () => {
    const fixturePath = resolve(root, 'src/XState.Tests/fixtures/upstream-directed-graph.json');
    const provenance = JSON.parse(readFileSync(resolve(root, 'src/XState.Tests/fixtures/upstream-directed-graph.provenance.json'), 'utf8'));
    const hash = bytes => createHash('sha256').update(bytes).digest('hex');
    expect(provenance.upstreamCommit).toBe(pin.commit);
    expect(hash(readFileSync(resolve(root, pin.sourceDirectory, provenance.source)))).toBe(provenance.sourceSha256);
    expect(hash(readFileSync(fixturePath))).toBe(provenance.fixtureSha256);
    const expected = JSON.parse(JSON.stringify(toDirectedGraph(diagram())));
    expect(JSON.parse(readFileSync(fixturePath, 'utf8'))).toEqual(expected);
    expect(JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-directed-graph.json'), 'utf8'))).toEqual(expected);
  });
  it('enumerates descendants in preorder excluding the source', () => {
    const machine = diagram(); const nodes = getStateNodes(machine);
    expect(nodes.map(n => n.key)).toEqual(['green', 'yellow', 'red', 'walk', 'wait', 'stop', 'finished']);
    expect(nodes).not.toContain(machine.root); expect(nodes[3]).toBe(machine.states.red.states.walk);
    expect(getStateNodes(machine.states.green)).toEqual([]);
    expect(getStateNodes(machine.states.red).map(n => n.key)).toEqual(['walk', 'wait', 'stop', 'finished']);
  });
  it('preserves targetless self edges, empty-target index gaps and multi-target identities', () => {
    const machine = multi(), graph = toDirectedGraph(machine);
    expect(graph.edges.map(e => e.id)).toEqual(['graph:0:0', 'graph:2:0', 'graph:2:1', 'graph:3:0']);
    expect(graph.edges.map(e => e.target.id)).toEqual(['graph', 'x', 'y', 'x']);
    expect(graph.edges.map(e => e.label.text)).toEqual(['BLOCK', 'GO', 'GO', 'GO']);
    expect(graph.edges.every(e => e.source === machine.root)).toBe(true);
    expect(graph.edges[1].transition).toBe(graph.edges[2].transition); expect(graph.edges[3].transition).toBe(machine.root.on.GO[1]);
  });
  it('does not initialize context or evaluate guards actions sources and inputs', () => {
    const effect = vi.fn();
    const machine = createMachine({ id: 'inert', context: effect, on: { GO: { guard: effect, actions: effect } }, invoke: { src: fromCallback(effect), input: effect } });
    const graph = toDirectedGraph(machine); getStateNodes(machine); JSON.stringify(graph);
    expect(effect).not.toHaveBeenCalled(); expect(graph.edges).toHaveLength(1);
  });
  it('includes generated transitions and excludes initial and always', () => {
    const machine = createMachine({ id: 'generated', on: { NORMAL: {} }, onDone: {}, after: { 5: {} }, always: {},
      invoke: { id: 'child', src: fromCallback(() => {}), onDone: {}, onError: {}, onSnapshot: {} }
    });
    const graph = toDirectedGraph(machine);
    expect(graph.edges.map(e => e.label.text)).toEqual(['NORMAL', 'xstate.done.state.generated', 'xstate.done.actor.child', 'xstate.error.actor.child', 'xstate.snapshot.child', 'xstate.after.5.generated']);
    expect(graph.edges.every(e => e.source === e.target)).toBe(true);
  });
  it('retains outside targets for a subtree graph', () => {
    const machine = multi(), node = machine.getStateNodeById('x'), graph = toDirectedGraph(node);
    expect(graph.id).toBe('x'); expect(graph.children).toEqual([]); expect(graph.edges).toHaveLength(1);
    expect(graph.edges[0].target).toBe(machine.root); expect(graph.stateNode).toBe(node);
  });
  it('serializes graph, edge and label using IDs without compiled references', () => {
    const graph = toDirectedGraph(multi()), json = JSON.parse(JSON.stringify(graph));
    expect(Object.keys(json)).toEqual(['id', 'children', 'edges']); expect(Object.keys(json.edges[0])).toEqual(['source', 'target', 'label']);
    expect(JSON.parse(JSON.stringify(graph.edges[0]))).toEqual({ source: 'graph', target: 'graph', label: { text: 'BLOCK' } });
    expect(JSON.parse(JSON.stringify(graph.edges[0].label))).toEqual({ text: 'BLOCK' });
    const second = toDirectedGraph(multi()); expect(graph).not.toBe(second);
    const machine = multi(), first = toDirectedGraph(machine), next = toDirectedGraph(machine);
    expect(first.edges[0]).not.toBe(next.edges[0]); expect(first.edges[0].transition).toBe(next.edges[0].transition);
  });
  it('preserves numeric child and event order', () => {
    const machine = createMachine({ initial: '10', states: { '10': {}, '2': {}, last: {} }, on: { '10': {}, '2': {}, last: {} } });
    const graph = toDirectedGraph(machine);
    expect(graph.children.map(c => c.stateNode.key)).toEqual(['2', '10', 'last']); expect(graph.edges.map(e => e.label.text)).toEqual(['2', '10', 'last']);
  });
  it('serializes deeply nested structures', () => {
    let config = {}; for (let i = 0; i < 150; i++) config = { initial: 'child', states: { child: config } };
    const machine = createMachine(config); expect(getStateNodes(machine)).toHaveLength(150);
    let node = JSON.parse(JSON.stringify(toDirectedGraph(machine))), depth = 0;
    while (node.children.length) { depth++; node = node.children[0]; } expect(depth).toBe(150);
  });
});
