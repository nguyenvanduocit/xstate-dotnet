import assert from 'node:assert/strict';
import { test } from 'node:test';
import ts from '../../../data/library-source/xstate-test-tools/node_modules/typescript/lib/typescript.js';
import { literal, validateConfig } from './translate-data-tests.mjs';
function expression(source) {
  const ast = ts.createSourceFile('fixture.ts', 'const config = ' + source + ';', ts.ScriptTarget.Latest, true);
  return ast.statements[0].declarationList.declarations[0].initializer;
}
test('stateIn retains string paths, IDs, empty and nested state values', () => {
  for (const value of ['a.b', '#b.B1', '', {}, {a:{b:'c'},d:'e'}])
    assert.deepEqual(literal(expression('stateIn(' + JSON.stringify(value) + ')')), {__stateIn:value});
});
test('stateIn rejects captures, callbacks, options and malformed state values', () => {
  for (const source of ['stateIn()', 'stateIn("a", "b")', 'stateIn(captured)', 'stateIn(() => "a")', 'stateIn(null)', 'stateIn(3)', 'stateIn(["a"])', 'stateIn({a: true})'])
    assert.throws(() => literal(expression(source)), source);
});
test('stateIn config preserves complete nested guard values and sibling transitions', () => {
  const config = literal(expression('{on:{GO:[{target:"b",guard:stateIn({parallel:{region:"active"}})},{target:"c"}]}}'));
  validateConfig(config);
  assert.deepEqual(config, {on:{GO:[{target:'b',guard:{__stateIn:{parallel:{region:'active'}}}},{target:'c'}]}});
});
test('guard validation rejects unsupported or ambiguous forms', () => {
  for (const guard of [true, 'named', {}, {__stateIn:'a',__constantGuard:true}, {__constantGuard:'true'}, {__stateIn:[]}, {unknown:'x'}])
    assert.throws(() => validateConfig({on:{GO:{guard}}}));
});
test('literal constant predicates remain boolean and do not admit dynamic callbacks', () => {
  for (const value of [true,false]) {
    const config = literal(expression('{on:{GO:{guard:() => '+value+'}}}'));
    validateConfig(config); assert.equal(config.on.GO.guard.__constantGuard,value);
  }
  assert.throws(() => literal(expression('{on:{GO:{guard:() => captured}}}')));
});
