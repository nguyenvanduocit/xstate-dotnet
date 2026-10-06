import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {matchesState,createActor}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const {testMultiTransition}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/test/utils.ts')).href);
const {machines,expectedTables}=await import(pathToFileURL(resolve(root,'tmp/xstate-parity/transition-table-machines.mts')).href);
const fixture=JSON.parse(readFileSync(resolve(root,'src/XState.Tests/fixtures/upstream-transition-tables.json'),'utf8'));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-transition-tables.json'),'utf8'));
const originalIds=[];
for(const table of fixture.tables){
 const machine=machines[table.key],expected=expectedTables[table.key],originalRows=[];
 for(const from of Object.keys(expected))for(const eventTypes of Object.keys(expected[from])){
  const to=expected[from][eventTypes];
  const id=table.source+'::'+[...table.suites,`should go from ${from} to ${JSON.stringify(to)} on ${eventTypes}`].join(' > ');
  originalIds.push(id);
  originalRows.push({id,from,eventTypes,events:eventTypes.split(/,\s?/),kind:table.mode==='equals'?'equals':to===undefined?'unchanged':typeof to==='string'?'matches':'equals',...(to===undefined?{}:{expected:to})});
  it(id,()=>{
   const resolveFrom=()=>machine.resolveState({value:from[0]==='{'?JSON.parse(from):from,context:{}});
   const initial=resolveFrom();const result=testMultiTransition(machine,from,eventTypes);
   if(table.mode==='equals')expect(result.value).toEqual(to);
   else if(to===undefined)expect(result.value).toEqual(resolveFrom().value);
   else if(typeof to==='string')expect(matchesState(to,result.value)).toBeTruthy();
   else expect(result.value).toEqual(to);
   expect({initial:initial.value,value:result.value}).toEqual(native[id]);
  });
 }
 // Compare the full expansion from original TS initializers to the JSON fixture.
 // A dropped row, event, expected value or altered assertion fails collection.
 expect(table.tests).toEqual(originalRows);
}
it('Example 6.8 actor respects history after leaving and returning',()=>{
 const table=fixture.tables.find(table=>table.source==='packages/core/test/examples/6.8.test.ts');
 const actor=createActor(machines[table.key]).start();
 try{const initial=actor.getSnapshot().value;actor.send({type:'1'});actor.send({type:'6'});actor.send({type:'5'});expect(actor.getSnapshot().value).toEqual({A:'C'});expect({initial,value:actor.getSnapshot().value}).toEqual(native.actorHistory);}finally{actor.stop();}
});
expect(Object.keys(native).sort()).toEqual([...originalIds,'actorHistory'].sort());
