import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const { createEmptyActor } = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);

describe('empty actor reference', () => {
  it('queues before start, publishes a fresh snapshot per event and completes on stop', () => {
    const actor = createEmptyActor(); const snapshots = []; let completed = 0;
    actor.subscribe({ next: snapshot => snapshots.push(snapshot), complete: () => completed++ });
    const before = actor.getSnapshot(); actor.send({ type: 'BEFORE_START' });
    expect(snapshots).toHaveLength(0); expect(actor.getSnapshot()).toBe(before);
    actor.start(); expect(snapshots).toHaveLength(2); expect(snapshots[0]).toBe(before); expect(snapshots[1]).not.toBe(before);
    actor.send({ type: 'ANY', value: 42 }); expect(snapshots).toHaveLength(3);
    actor.stop(); expect(snapshots).toHaveLength(4); expect(completed).toBe(1); expect(actor.getSnapshot().status).toBe('active');
    const stopped = actor.getSnapshot(); actor.send({ type: 'IGNORED' }); actor.stop();
    expect(actor.getSnapshot()).toBe(stopped); expect(snapshots).toHaveLength(4);
  });
  it('shares the singleton logic while retaining separate identity, snapshot and system', () => {
    const first = createEmptyActor(); const second = createEmptyActor();
    expect(first.logic).toBe(second.logic); expect(first.getSnapshot()).not.toBe(second.getSnapshot());
    expect(first.system).not.toBe(second.system); expect(first.id).not.toBe(second.id); expect(first.id).toBe(first.sessionId);
    first.stop(); second.stop();
  });
  it('persists by identity and omits undefined fields from JSON', () => {
    const actor = createEmptyActor().start(); actor.send({ type: 'GO' });
    expect(actor.getPersistedSnapshot()).toBe(actor.getSnapshot());
    expect(actor.getSnapshot()).toEqual({ status: 'active', context: undefined, output: undefined, error: undefined });
    expect(JSON.stringify(actor.getPersistedSnapshot())).toBe('{"status":"active"}'); actor.stop();
  });
  it('discards queued work without publishing or completing when stopped before start', () => {
    const actor = createEmptyActor(); let snapshots = 0; let completed = 0;
    actor.subscribe({ next: () => snapshots++, complete: () => completed++ });
    actor.send({ type: 'QUEUED' }); const before = actor.getSnapshot(); actor.stop();
    expect(snapshots).toBe(0); expect(completed).toBe(0); expect(actor.getSnapshot()).toBe(before);
  });
});
