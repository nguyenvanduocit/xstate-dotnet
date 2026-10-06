import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const upstream=part=>pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src',part)).href;
const {createMachine,fromTransition,getInitialSnapshot,raise,sendTo,sendParent}=await import(upstream('index.ts'));
const {TestModel,createTestModel}=await import(upstream('graph/index.ts'));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-test-model.json'),'utf8'));
const chain=()=>createTestModel(createMachine({initial:'a',states:{a:{on:{GO:'b'}},b:{on:{NEXT:'c'}},c:{}}}));
const multi=()=>createTestModel(createMachine({initial:'a',states:{a:{on:{EVENT:'b'}},b:{on:{EVENT:'c'}},c:{on:{EVENT:'d',EVENT_2:'e'}},d:{},e:{}}}));
it('awaits callbacks in the same order as native and records successful outcomes',async()=>{
 const model=createTestModel(createMachine({initial:'a',states:{a:{on:{GO:'b'}},b:{}}}));const path=model.getShortestPaths()[0],trace=[];
 let release;const pending=new Promise(resolve=>release=resolve);let done=false;
 const task=path.test({events:{'xstate.init':async()=>{trace.push('init:before');await pending;trace.push('init:after');},GO:async()=>{trace.push('go:before');await Promise.resolve();trace.push('go:after');}},states:{a:s=>trace.push(s.value),b:s=>trace.push(s.value),'*':()=>trace.push('fallback')}}).then(r=>{done=true;return r;});
 expect(done).toBe(false);expect(trace).toEqual(['init:before']);release();const result=await task;
 expect(trace).toEqual(native.ordering);expect(result.steps).toHaveLength(2);expect(result.state.error).toBeNull();expect(result.steps.every(s=>s.state.error===null&&s.event.error===null)).toBe(true);
});
it('matches failure trace text while documenting JS original-error identity',async()=>{
 const rows=[];
 for(const eventError of [true,false]){
  const model=chain(),path=model.getShortestPaths()[0],trace=[],original=new Error('callback failed');
  let caught;
  try{await path.test({events:{GO:()=>{trace.push('GO');if(eventError)throw original;}},states:{'*':s=>{trace.push(s.value);if(s.matches('b'))throw original;}}});}catch(error){caught=error;}
  expect(caught).toBe(original);rows.push({eventError,trace,message:caught.message});
 }
 expect(rows).toEqual(native.failures);
});
it('merges provided options and preserves explicit null and Infinity',()=>{
 const model=new TestModel(fromTransition(v=>v+1,0),{limit:5,input:'model',stopWhen:()=>true});model.defaultTraversalOptions={limit:2,input:'defaults',toState:()=>false};const rows=[];
 model.getPaths((_,options)=>{expect(options.toState).toBeTypeOf('function');rows.push({limit:options.limit,input:options.input});return [];});
 model.getPaths((_,options)=>{expect(options.stopWhen).toBeNull();expect(options.limit).toBe(Infinity);rows.push({limit:'Infinity',input:options.input});return [];},{limit:Infinity,input:null,stopWhen:null});
 expect(rows).toEqual(native.options);
});
it('expands active event cases and bypasses model defaults for explicit event paths',()=>{
 const calls=[];const model=createTestModel(createMachine({initial:'a',states:{a:{on:{GO:'b'}},b:{}}}),{events:s=>{calls.push(s.value);return [{type:'GO',value:1},{type:'GO',value:2},{type:'UNKNOWN'}];}});
 const generated=model.getShortestPaths(),overridden=model.getShortestPaths({events:[]});model.options.filterEvents=()=>false;const explicitPath=model.getPathsFromEvents([{type:'GO'}]);
 expect({calls,generated:generated.map(p=>p.description),overridden:overridden.map(p=>p.description),explicitPath:explicitPath[0].description}).toEqual(native.providers);
});
it('rejects numeric inline delays before executing context or dynamic delay functions',()=>{
 let contextCalls=0,delayCalls=0,rejected=0,accepted=0;
 for(const delay of [0,NaN,'later',()=>{delayCalls++;return 1;}])
 for(const action of [raise(()=>({type:'EV'}),{delay}),raise({type:'EV'},{delay}),sendTo('child',{type:'EV'},{delay}),sendTo(({self})=>self,{type:'EV'},{delay}),sendParent({type:'EV'},{delay})])
 for(const placement of ['entry','exit','event']){
  const child=placement==='event'?{on:{EV:{actions:[action]}}}:{[placement]:[action]};
  const machine=createMachine({context:()=>{contextCalls++;return {};},initial:'a',states:{a:child}});
  try{createTestModel(machine);accepted++;}catch(error){expect(error.message).toBe('Delayed actions on test machines are not supported');rejected++;}
 }
 expect({rejected,accepted,contextCalls,delayCalls}).toEqual(native.validation);
});
it('retains model options through previously created test path closures',async()=>{
 const model=multi(),path=model.getShortestPaths()[0],seen=[];model.options.stateMatcher=(_,key)=>key==='custom';
 await path.test({states:{custom:s=>seen.push(s.value),'*':()=>{throw Error('Unexpected fallback');}}});expect(seen).toEqual(native.mutable);
});
it('deduplicates default event prefixes and reads allowDuplicatePaths from call options',()=>{
 const model=multi();model.options.serializeEvent=()=> 'same';model.options.allowDuplicatePaths=true;const snapshot=getInitialSnapshot(model.testLogic);
 const paths=[['A'],['A','B'],['C']].map(events=>({state:snapshot,steps:events.map(type=>({state:snapshot,event:{type}})),weight:events.length}));
 expect(model.getPaths(()=>paths).map(p=>p.steps.map(s=>s.event.type))).toEqual(native.prefixes);expect(model.getPaths(()=>paths,{allowDuplicatePaths:true})).toHaveLength(3);
});
it('matches descriptions for metadata functions and parallel final states',()=>{
 const machine=createMachine({type:'parallel',states:{left:{meta:{description:'ready'}},right:{type:'final',meta:{description:()=> 'dynamic'}}}});
 expect(createTestModel(machine).getShortestPaths()[0].description).toBe(native.metadata);
});
