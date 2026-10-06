import assert from 'node:assert/strict';
import {test} from 'node:test';
import {extractTables} from './extract-transition-tables.mjs';
const extract=body=>extractTables(body,'fixture.test.ts');
const wrap=(config,expected)=>`describe('suite',()=>{const machine=createMachine(${config});const expected=${expected};testAll(machine,expected);});`;
test('preserves expanded IDs, object order, string matching, undefined and exact event splitting',()=>{
 const result=extract(wrap(`{initial:'a',states:{a:{on:{GO:'b'}},b:{}}}`,`{a:{10:'b',2:undefined,'GO,  NEXT':{b:'deep'}}}`));
 assert.equal(result.rejected.length,0);const tests=result.tables[0].tests;
 assert.deepEqual(tests.map(t=>t.kind),['unchanged','matches','equals']);
 assert.deepEqual(tests[2].events,['GO',' NEXT']);
 assert.equal(tests[0].id,'fixture.test.ts::suite > should go from a to undefined on 2');
 assert.deepEqual(tests[2].expected,Object.assign(Object.create(null),{b:'deep'}));
});
test('retains stateIn guard config and the original machine expression',()=>{
 const result=extract(wrap(`{on:{GO:{guard:stateIn('#x')}}}`,`{a:{GO:'a'}}`));
 assert.equal(result.rejected.length,0);assert.deepEqual(result.tables[0].config.on.GO.guard,{__stateIn:'#x'});
 assert.equal(result.tables[0].expression,"createMachine({on:{GO:{guard:stateIn('#x')}}})");
});
test('resolves outer bindings and keeps sibling scopes separate',()=>{
 const result=extract(`const outer=createMachine({id:'outer'});describe('one',()=>{const expected={a:{GO:'a'}};testAll(outer,expected);});describe('two',()=>{const outer=createMachine({id:'inner'});const expected={b:{GO:'b'}};testAll(outer,expected);});`);
 assert.equal(result.tables.length,2);assert.deepEqual(result.tables.map(t=>t.config.id),['outer','inner']);assert.equal(result.rejected.length,0);
});
test('rejects entire table when any row or configuration expression is unsupported',()=>{
 for(const [config,expected] of [[`{entry:()=>{}}`,`{a:{GO:'a'}}`],[`{}`,`{a:{GO:'a',BAD:someValue}}`],[`{}`,`{a:{GO:null}}`],[`{}`,`{a:{GO:[]}}`],[`{}`,`{a:{GO:{b:undefined}}}`],[`{}`,`{a:{GO:'a',...other}}`],[`{}`,`{a:{GO:'a',GO:'b'}}`]]){
  const result=extract(wrap(config,expected));assert.equal(result.tables.length,0,expected);assert.equal(result.rejected.length,1,expected);
 }
});
test('rejects mutations before or after registration and mutable bindings',()=>{
 for(const source of [wrap('{}',`{a:{GO:'a'}}`).replace('testAll(machine,expected);','expected.a.GO="b";testAll(machine,expected);'),wrap('{}',`{a:{GO:'a'}}`).replace('testAll(machine,expected);','testAll(machine,expected);expected.a.GO="b";'),wrap('{}',`{a:{GO:'a'}}`).replace('const expected','let expected')]){
  const result=extract(source);assert.equal(result.tables.length,0);assert.equal(result.rejected.length,1);
 }
});
test('does not collect callbacks from skipped suites',()=>{const result=extract(`describe.skip('skip',()=>{const machine=createMachine({});const expected={a:{GO:'a'}};testAll(machine,expected);});`);assert.equal(result.tables.length,0);});
test('rejects machine implementation arguments and table call arguments it cannot preserve',()=>{
 for(const source of [wrap('{}',`{a:{GO:'a'}}`).replace('createMachine({})','createMachine({}, {actions:{}})'),wrap('{}',`{a:{GO:'a'}}`).replace('testAll(machine,expected)','testAll(machine,{a:{GO:"a"}})')]){const result=extract(source);assert.equal(result.tables.length,0);assert.equal(result.rejected.length,1);}
});

const loop=`Object.keys(expected).forEach((fromState)=>{Object.keys(expected[fromState]).forEach((eventTypes)=>{const toState=expected[fromState][eventTypes];it(\`should go from \${fromState} to \${JSON.stringify(toState)} on \${eventTypes}\`,()=>{const resultState=testMultiTransition(machine,fromState,eventTypes);expect(resultState.value).toEqual(toState);});});});`;
const loopSource=body=>`describe('suite',()=>{const machine=createMachine({});const expected={a:{GO:'a'}};${body}});`;
test('inline table loop retains deep equality even for a string expectation',()=>{const result=extract(loopSource(loop));assert.equal(result.tables.length,1);assert.equal(result.tables[0].mode,'equals');assert.equal(result.tables[0].tests[0].kind,'equals');assert.equal(result.tables[0].tests[0].expected,'a');});
test('inline loop rejects additional assertions, altered events, title or callback effects',()=>{for(const changed of [loop.replace('expect(resultState.value).toEqual(toState);','expect(resultState.value).toEqual(toState);expect(1).toBe(1);'),loop.replace('machine,fromState,eventTypes','machine,fromState,"GO"'),loop.replace('should go from','wrong title'),loop.replace('const resultState=','doSomething();const resultState=')])assert.equal(extract(loopSource(changed)).tables.length,0);});
test('rejects bindings that shadow helper primitives',()=>{for(const name of ['undefined','stateIn','createMachine','Object','JSON'])assert.equal(extract(wrap('{}',`{a:{GO:undefined}}`).replace('const machine=',`const ${name}=42;const machine=`)).tables.length,0);});

test('normalizes multiline expressions inside interpolated testcase titles',()=>{const result=extract(loopSource(loop.replace('JSON.stringify(toState)','JSON.stringify(\n toState\n)')));assert.equal(result.tables.length,1);assert.equal(result.tables[0].tests[0].id,'fixture.test.ts::suite > should go from a to "a" on GO');});

test('rejects mutable loop locals without treating const and let as identical ASTs',()=>{const result=extract(loopSource(loop.replace('const toState','let toState')));assert.equal(result.tables.length,0);assert.equal(result.rejected.length,1);assert.match(result.rejected[0].reason,/Unsupported transition table loop/);});
