import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createActor, createMachine, not } =
  await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

// Supplemental probes for the C# parameter representation; not upstream coverage.
describe('guard parameter reference', () => {
  it('resolves an alias before evaluating outer params', () => {
    let outerCalls = 0;
    const seen = [];
    const actor = createActor(createMachine({ on: { GO: { guard: {
      type: 'alias', params: () => { outerCalls++; return 'outer'; }
    } } } }, { guards: {
      alias: { type: 'inner', params: 42 },
      inner: (_, params) => { seen.push(params); return true; }
    } })).start();
    actor.send({ type: 'GO' });
    expect(outerCalls).toBe(0);
    expect(seen).toEqual([42]);
    actor.stop();
  });
  it('rejects a missing guard before evaluating its params', () => {
    let paramsCalls = 0;
    const errors = [];
    const actor = createActor(createMachine({ on: { GO: { guard: {
      type: 'missing', params: () => { paramsCalls++; return 42; }
    } } } }));
    actor.subscribe({ error: error => errors.push(error) });
    actor.start();
    actor.send({ type: 'GO' });
    expect(paramsCalls).toBe(0);
    expect(errors).toHaveLength(1);
    expect(errors[0].message).toContain("Guard 'missing' is not implemented.'.");
    actor.stop();
  });
  it('evaluates composite outer params without forwarding them to nested predicates', () => {
    const seen = [];
    let outerCalls = 0;
    const actor = createActor(createMachine({ on: {
      GO: { guard: { type: 'composite', params: () => { outerCalls++; return 99; } } },
      NULL: { guard: { type: 'inner', params: null } }
    } }, { guards: {
      composite: not('inner'),
      inner: (_, params) => { seen.push(params); return false; }
    } })).start();
    actor.send({ type: 'GO' });
    actor.send({ type: 'NULL' });
    expect(outerCalls).toBe(1);
    expect(seen).toEqual([undefined, null]);
    actor.stop();
  });
});
