import { readFileSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const fixturePath = resolve(root, 'src/XState.Tests/fixtures/upstream-parallel.json');
const fixture = JSON.parse(readFileSync(fixturePath, 'utf8'));
const native = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-parallel-fixtures.json'), 'utf8'));
const { machines } = await import(pathToFileURL(resolve(root, 'tmp/xstate-parity/parallel-machines.mts')).href);
const { getInitialSnapshot, getNextSnapshot } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const hash = bytes => createHash('sha256').update(bytes).digest('hex');
it('parallel fixture provenance matches pinned source and the native input artifact', () => {
  expect(fixture.commit).toBe(pin.commit);
  expect(fixture.sourceSha256).toBe(hash(readFileSync(resolve(root, pin.sourceDirectory, fixture.source))));
  expect(native.fixtureSha256).toBe(hash(readFileSync(fixturePath)));
  expect(native.rows.map(row => row.name)).toEqual(Object.keys(fixture.fixtures));
  expect(Object.keys(machines)).toEqual(Object.keys(fixture.fixtures));
});
for (const row of native.rows) {
  it('parallel original initializer and event snapshots: ' + row.name, () => {
    const machine = machines[row.name]; const initial = getInitialSnapshot(machine);
    const view = snapshot => ({ value: JSON.stringify(snapshot.value), status: snapshot.status });
    expect(row.initial).toEqual(view(initial)); expect(row.events.map(event => event.type)).toEqual(machine.events);
    for (const event of row.events) expect(event.snapshot, event.type).toEqual(view(getNextSnapshot(machine, initial, {type: event.type})));
  });
}
