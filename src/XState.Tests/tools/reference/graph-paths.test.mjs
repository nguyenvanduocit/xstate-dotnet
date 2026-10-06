import {readFileSync} from 'node:fs';
import {createHash} from 'node:crypto';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const source=part=>pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src',part)).href;
const {createMachine,createEmptyActor,fromTransition,assign}=await import(source('index.ts'));
const {getShortestPaths,getSimplePaths,getPathsFromEvents}=await import(source('graph/index.ts'));
const fixturePath=resolve(root,'src/XState.Tests/fixtures/upstream-graph-paths.json');
const fixture=JSON.parse(readFileSync(fixturePath,'utf8'));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-graph-paths.json'),'utf8'));
const resources=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-graph-path-resources.json'),'utf8'));
const hash=value=>createHash('sha256').update(value).digest('hex');
it('graph path fixture and native output identify the pinned upstream snapshots',()=>{
 expect(fixture.commit).toBe(pin.commit);expect(fixture.sourceSha256).toBe(hash(readFileSync(resolve(root,pin.sourceDirectory,fixture.source))));
 expect(native.fixtureSha256).toBe(hash(readFileSync(fixturePath)));expect(Object.keys(native.snapshots).sort()).toEqual(Object.keys(fixture.snapshots).sort());
});
const light=()=>createMachine({id:'light',initial:'green',states:{green:{on:{TIMER:'yellow',POWER_OUTAGE:'red.flashing',PUSH_BUTTON:[{actions:['doNothing']}]}},yellow:{on:{TIMER:'red',POWER_OUTAGE:'red.flashing'}},red:{on:{TIMER:'green',POWER_OUTAGE:'red.flashing'},initial:'walk',states:{walk:{on:{PED_COUNTDOWN:{target:'wait',actions:['startCountdown']}}},wait:{on:{PED_COUNTDOWN:'stop'}},stop:{},flashing:{}}}}});
const parallel=()=>{const branch=p=>({initial:p+'1',states:{[p+'1']:{on:{2:p+'2',3:p+'3'}},[p+'2']:{on:{3:p+'3',1:p+'1'}},[p+'3']:{}}});return createMachine({type:'parallel',id:'p',states:{a:branch('a'),b:branch('b')}});};
const equivalent=()=>createMachine({initial:'a',states:{a:{on:{FOO:'b',BAR:'b'}},b:{on:{FOO:'a',BAR:'a'}}}});
const count=()=>createMachine({id:'count',initial:'start',context:{count:0},states:{start:{always:{target:'finish',guard:({context})=>context.count===3},on:{INC:{actions:assign({count:({context})=>context.count+1})}}},finish:{}}});
const reducer=()=>fromTransition((s,e)=>e.type==='a'?1:e.type==='b'&&s===1?2:e.type==='reset'?0:s,0);
const reducerOptions={events:[{type:'a'},{type:'b'},{type:'reset'}],serializeState:(s,e)=>JSON.stringify(s)+' | '+JSON.stringify(e)};
const view=p=>({state:p.state.value??p.state.context,steps:p.steps.map(s=>({state:s.state.value??s.state.context,eventType:s.event.type}))});
for(const [key,expected] of Object.entries(fixture.snapshots))it('whole upstream path snapshot: '+key,()=>{
 let actual;
 if(key.startsWith('simple paths for transition'))actual=getShortestPaths(reducer(),reducerOptions).map(view);
 else if(key.startsWith('shortest paths for transition'))actual=getSimplePaths(reducer(),reducerOptions).map(view);
 else if(key.includes('getPathFromEvents()'))actual=view(getPathsFromEvents(light(),[{type:'TIMER'},{type:'TIMER'},{type:'TIMER'},{type:'POWER_OUTAGE'}])[0]);
 else if(key.includes('getShortestPaths()'))actual=getShortestPaths(key.includes('(parallel)')?parallel():light()).map(view);
 else if(key.includes('value-based'))actual=getSimplePaths(count(),{events:[{type:'INC',value:1}]}).map(view);
 else actual=getSimplePaths(key.includes('(parallel)')?parallel():key.includes('equivalent transitions')?equivalent():light()).map(view);
 expect(actual).toEqual(expected);expect(native.snapshots[key]).toEqual(actual);
});
it('input defaults and explicit null fromState preserve initializer call order',()=>{
 const rows=[];
 for(const mode of ['shortest','simple','events'])for(const explicitNull of [false,true]){
  const inputs=[];const machine=createMachine({context:({input})=>{inputs.push(input??null);return {value:input??-1};}});
  const options={input:7,...(explicitNull?{fromState:null}:{})};
  const paths=mode==='shortest'?getShortestPaths(machine,options):mode==='simple'?getSimplePaths(machine,options):getPathsFromEvents(machine,[],options);
  rows.push({mode,explicitNull,inputs,context:paths[0].state.context.value});
 }
 expect(resources.initialization).toEqual(rows);
});
it('observable scope actor counts match all three path getters',()=>{
 const counts={};for(const mode of ['shortest','simple','events']){
  const before=createEmptyActor();const machine=createMachine({});
  if(mode==='shortest')getShortestPaths(machine);else if(mode==='simple')getSimplePaths(machine);else getPathsFromEvents(machine,[]);
  const after=createEmptyActor();counts[mode]=Number(after.sessionId.slice(2))-Number(before.sessionId.slice(2))-1;before.stop();after.stop();
 }expect(resources.scopeCounts).toEqual(counts);
});
it('event path collisions keep the requested event reference and the last stored snapshot',()=>{
 const first={type:'FIRST',payload:1},last={type:'LAST',payload:2};const logic=fromTransition((_,ev)=>ev.payload,0);
 const path=getPathsFromEvents(logic,[first],{events:[first,last],serializeEvent:()=> 'same',stopWhen:s=>s.context!==0})[0];
 expect(resources.collision).toEqual({context:path.state.context,weight:path.weight,events:path.steps.map(s=>s.event.type),originalEvent:path.steps[1].event===first});
});
it('event paths accept unhandled events but fail for missing filtered edges',()=>{
 const machine=createMachine({});const ev={type:'UNHANDLED'};const path=getPathsFromEvents(machine,[ev])[0],empty=getPathsFromEvents(machine,[])[0];let failures=0;
 expect(getPathsFromEvents(machine,[ev],{toState:()=>false})).toEqual([]);
 for(const options of [{filterEvents:()=>false},{events:[]}]){try{getPathsFromEvents(machine,[ev],options);}catch(error){expect(error).toBeInstanceOf(TypeError);failures++;}}
 expect(resources.eventSelection).toEqual({unhandledSteps:path.steps.length,emptySteps:empty.steps.length,failures});
});
it('simple-path backtracking preserves snapshot-map replacement by visited edges',()=>{
 const logic=fromTransition((context,ev)=>({stage:ev.type==='LOOP'?context.stage:context.stage==='a'?'b':'c',version:context.version+1}),{stage:'a',version:0});
 const paths=getSimplePaths(logic,{serializeState:s=>s.context.stage,events:s=>s.context.stage==='a'?[{type:'ADV'}]:s.context.stage==='b'?[{type:'LOOP'},{type:'ADV'}]:[]});
 expect(resources.sharedSnapshots).toEqual(paths.map(p=>({state:p.state.context,weight:p.weight,steps:p.steps.map(s=>({state:s.state.context,event:s.event.type}))})));
});
