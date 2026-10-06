import assert from 'node:assert/strict';
import { test } from 'node:test';
import { extractFixtures } from './extract-parallel-fixtures.mjs';
const extract = source => extractFixtures(source, 'test.ts');
test('retains the complete config, named actions, literal raises and original JS expression', () => {
  const source = `const m = createMachine({initial:'a', states:{a:{entry:['named',raise({type:'GO',value:2})], on:{GO:{target:'b',actions:'other'}}},b:{}}});`;
  const result = extract(source); assert.equal(result.rejected.length, 0);
  const fixture = result.fixtures['m@1']; assert.deepEqual(fixture.config.states.a.entry, ['named',{__raiseEvent:{type:'GO',value:2}}]);
  assert.equal(fixture.config.states.a.on.GO.actions, 'other');
  assert.equal(result.expressions['m@1'], source.slice(source.indexOf('createMachine'), -1));
});
test('rejects the whole fixture if a callback or captured variable is unsupported', () => {
  for (const entry of ['() => {}', 'captured', 'raise(() => ({type:"GO"}))', 'raise({type:"GO"}, {delay:1})']) {
    const result = extract(`const m = createMachine({initial:'a',states:{a:{entry:${entry}},b:{}}});`);
    assert.equal(Object.keys(result.fixtures).length, 0); assert.equal(result.rejected.length, 1); assert.deepEqual(result.expressions, {});
  }
});
test('rejects unknown config/action options and machine implementations', () => {
  for (const source of [
    'const m = createMachine({unknown:true});',
    'const m = createMachine({entry:{type:"named"}});',
    'const m = createMachine({on:{GO:{guard:()=>true}}});',
    'const m = createMachine({}, {actions:{}});'
  ]) { const result = extract(source); assert.equal(Object.keys(result.fixtures).length, 0); assert.equal(result.rejected.length, 1); }
});
test('does not merge duplicate variable names from different scopes', () => {
  const result = extract('const machine=createMachine({id:"outer"});\nfunction f(){const machine=createMachine({id:"inner"});}');
  assert.equal(result.fixtures['machine@1'].config.id, 'outer'); assert.equal(result.fixtures['machine@2'].config.id, 'inner');
});
