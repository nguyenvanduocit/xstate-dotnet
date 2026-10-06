import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {assign,createActor,createMachine,fromCallback,initialTransition,transition}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-assignment.json'),'utf8'));
const counter=(count=0)=>createMachine({context:{count,foo:'bar'},initial:'counting',states:{counting:{on:{
 INC:{target:'counting',actions:assign(({context})=>({count:context.count+1}))},
 DEC:{target:'counting',actions:assign({count:({context})=>context.count-1})},
 WIN_PROP:{target:'counting',actions:assign({count:()=>100,foo:()=>'win'})},
 WIN_STATIC:{target:'counting',actions:assign({count:100,foo:'win'})},
 WIN_MIX:{target:'counting',actions:assign({count:()=>100,foo:'win'})},
 WIN:{target:'counting',actions:assign(()=>({count:100,foo:'win'}))},
 SET_MAYBE:{actions:assign({maybe:'defined'})}
}}}});
const observe=ref=>({state:ref.getSnapshot().value,context:ref.getSnapshot().context});
for(const type of ['INC','DEC'])for(const count of [0,50])it(`merges ${type} assignments into initial count ${count}`,()=>{
 const ref=createActor(counter(count)).start();const steps=[];
 try {ref.send({type});steps.push(observe(ref));ref.send({type});steps.push(observe(ref));
  if(count){const other=createActor(counter(type==='INC'?102:100)).start();try{other.send({type});steps.push(observe(other));}finally{other.stop();}}
  expect(steps).toEqual(native[`${type}:${count}`]);
 }finally{ref.stop();}
});
for(const type of ['WIN_PROP','WIN_STATIC','WIN_MIX','WIN'])it(`merges multiple properties using ${type}`,()=>{
 const ref=createActor(counter()).start();try{ref.send({type});expect(ref.getSnapshot().context).toEqual(native[type]);}finally{ref.stop();}
});
it('keeps context for unhandled events',()=>{const ref=createActor(counter()).start();try{ref.send({type:'FAKE_EVENT'});expect(observe(ref)).toEqual(native.unhandled);}finally{ref.stop();}});
it('adds a previously absent property',()=>{const ref=createActor(counter()).start();try{ref.send({type:'SET_MAYBE'});expect(ref.getSnapshot().context).toEqual(native.missing);}finally{ref.stop();}});
it('reads the triggering event',()=>{const ref=createActor(createMachine({context:{count:0},initial:'active',states:{active:{on:{INC:{actions:assign({count:({event})=>event.value})}}}}})).start();try{ref.send({type:'INC',value:30});expect(ref.getSnapshot().context).toEqual(native.event);}finally{ref.stop();}});
it('receives named action parameters in a property resolver',()=>{const ref=createActor(createMachine({context:{count:1},entry:{type:'inc',params:{by:10}}},{actions:{inc:assign({count:({context},params)=>context.count+params.by})}})).start();try{expect(ref.getSnapshot().context).toEqual(native.parameters);}finally{ref.stop();}});
it('reads original properties and preserves snapshots references and action order',()=>{
 const retained={};const original={count:1,other:2,retained};const trace=[];
 const machine=createMachine({context:original,on:{GO:{actions:[({context})=>trace.push(context.count),assign({count:({context})=>context.count+1,other:({context})=>context.count}),({context})=>trace.push(context.count),assign(({context})=>({count:context.count+1,nil:null})),({context})=>trace.push(context.count)]}}});
 const ref=createActor(machine).start();try{const before=ref.getSnapshot();ref.send({type:'GO'});const context=ref.getSnapshot().context;const [pure,effects]=transition(machine,initialTransition(machine)[0],{type:'GO'});
 expect({trace,count:context.count,other:context.other,nil:context.nil,oldCount:before.context.count,oldHasNil:Object.hasOwn(before.context,'nil'),sameRetained:context.retained===retained,sameContext:context===original,pureCount:pure.context.count,pureActions:effects.length}).toEqual(native.ordering);
 }finally{ref.stop();}
});
it('copies original context after property callbacks run',()=>{
 const original={count:1};const machine=createMachine({context:original,entry:assign({count:({context})=>{original.sideEffect='observed';return context.count+1;},sawSideEffect:({context})=>context.sideEffect})});
 const ref=createActor(machine).start();try{expect(ref.getSnapshot().context).toEqual(native.mutation);}finally{ref.stop();}
});
it('clones empty assignment and does not publish a partially resolved failed assignment',()=>{
 const original={count:1};const calls=[];const expected=new Error('property failed');const machine=createMachine({context:original,on:{EMPTY:{actions:assign({})},FAIL:{actions:assign({count:()=>{calls.push('first');return 99;},bad:()=>{calls.push('second');throw expected;},never:()=>{calls.push('third');return 0;}})}}});
 const ref=createActor(machine);const errors=[];const published=[];ref.subscribe({next:s=>published.push(s.context.count),error:e=>errors.push(e)});
 try{ref.start();ref.send({type:'EMPTY'});const before=ref.getSnapshot().context;ref.send({type:'FAIL'});expect(errors).toHaveLength(1);expect({calls,published,count:ref.getSnapshot().context.count,status:ref.getSnapshot().status,sameError:errors[0]===expected,clonedEmpty:before!==original}).toEqual(native.failure);}finally{ref.stop();}
});
it('merges spawned children from property resolvers and owns their cleanup',()=>{
 let starts=0,cleanups=0;const child=fromCallback(()=>{starts++;return()=>cleanups++;});const ref=createActor(createMachine({context:{count:1},entry:assign({child:({spawn})=>spawn(child,{id:'kid'})})})).start();
 try{expect(ref.getSnapshot().context.child).toBe(ref.getSnapshot().children.kid);}finally{ref.stop();}expect({starts,cleanups,count:ref.getSnapshot().context.count}).toEqual(native.spawn);
});
