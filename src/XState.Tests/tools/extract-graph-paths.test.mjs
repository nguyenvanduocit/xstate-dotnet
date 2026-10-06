import assert from 'node:assert/strict';
import {test} from 'node:test';
import {readSnapshots} from './extract-graph-paths.mjs';
test('reads complete ordered path data with snapshot trailing commas',()=>{
 assert.deepEqual(readSnapshots('exports[`case 1`] = `[{"state":"a", "steps":[{"eventType":"xstate.init","state":{},},],},]`;'),{'case 1':[{state:'a',steps:[{eventType:'xstate.init',state:{}}]}]});
});
test('rejects executable snapshot expressions and extra statements',()=>{
 for(const value of ['run()', '[...captured]', '{[computed]:2}', '{}; run()']) assert.throws(()=>readSnapshots('exports[`case`] = `'+value+'`;'));
 assert.throws(()=>readSnapshots('execute();'));
});
test('rejects dynamic assignments and duplicate snapshot keys',()=>{
 assert.throws(()=>readSnapshots('exports[computed] = `[]`;'));
 assert.throws(()=>readSnapshots('exports[`same`] = `[]`; exports[`same`] = `[]`;'));
});
