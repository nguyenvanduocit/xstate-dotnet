import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {assign,createActor,createMachine,raise,spawnChild}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-transient.json'),'utf8'));
for(const [data,guardedFallback] of [[false,false],[false,true],[true,false]])it(`selects the first enabled candidate ${data}/${guardedFallback}`,()=>{
 const fallback=guardedFallback?{target:'F',guard:()=>true}:{target:'F'};
 const ref=createActor(createMachine({context:{data},initial:'G',states:{G:{on:{UPDATE_BUTTON_CLICKED:'E'}},E:{always:[{target:'D',guard:({context})=>!context.data},fallback]},D:{},F:{}}})).start();
 try{ref.send({type:'UPDATE_BUTTON_CLICKED'});expect(ref.getSnapshot().value).toEqual(native[`candidate:${data?'True':'False'}:${guardedFallback?'True':'False'}`]);}finally{ref.stop();}
});
it('retains actions across transient steps',()=>{
 const trace=[];const ref=createActor(createMachine({initial:'A',states:{A:{exit:()=>trace.push('exit_A'),on:{TIMER:{target:'T',actions:()=>trace.push('timer')}}},T:{always:'B'},B:{entry:()=>trace.push('enter_B')}}})).start();try{ref.send({type:'TIMER'});expect(trace).toEqual(native.carry);}finally{ref.stop();}
});
it('processes raised events from parallel regions in order',()=>{
 const ref=createActor(createMachine({type:'parallel',states:{A:{initial:'A1',states:{A1:{on:{E:'A2'}},A2:{entry:raise({type:'INT1'})}}},B:{initial:'B1',states:{B1:{on:{E:'B2'}},B2:{entry:raise({type:'INT2'})}}},C:{initial:'C1',states:{C1:{on:{INT1:'C2',INT2:'C3'}},C2:{on:{INT2:'C4'}},C3:{on:{INT1:'C4'}},C4:{}}}}})).start();try{ref.send({type:'E'});expect(ref.getSnapshot().value).toEqual(native.internal);}finally{ref.stop();}
});
const greeting=()=>createMachine({id:'greeting',initial:'pending',context:{hour:10},states:{pending:{always:[{target:'morning',guard:({context})=>context.hour<12},{target:'afternoon',guard:({context})=>context.hour<18},{target:'evening'}]},morning:{},afternoon:{},evening:{}},on:{CHANGE:{actions:assign({hour:20})},RECHECK:'#greeting'}});
it('resolves initial transient transitions before start',()=>{const ref=createActor(greeting());try{expect(ref.getSnapshot().value).toEqual(native.initial);}finally{ref.stop();}});
it('rechecks transient state after root transition',()=>{const ref=createActor(greeting()).start();try{const states=[];ref.send({type:'CHANGE'});states.push(ref.getSnapshot().value);ref.send({type:'RECHECK'});states.push(ref.getSnapshot().value);expect(states).toEqual(native.recheck);}finally{ref.stop();}});
it('selects eventless transitions before raised events',()=>{
 const ref=createActor(createMachine({initial:'a',states:{a:{on:{FOO:'b'}},b:{entry:raise({type:'BAR'}),always:'c',on:{BAR:'d'}},c:{on:{BAR:'e'}},d:{},e:{}}})).start();try{ref.send({type:'FOO'});expect(ref.getSnapshot().value).toEqual(native.beforeRaised);}finally{ref.stop();}
});
it('takes a guarded eventless transition on the root after assign',()=>{
 const ref=createActor(createMachine({id:'machine',context:{count:0},initial:'first',states:{first:{on:{ADD:{actions:assign({count:({context})=>context.count+1})}}},success:{type:'final'}},always:[{target:'.success',guard:({context})=>context.count>0}]})).start();try{ref.send({type:'ADD'});expect(ref.getSnapshot().status).toEqual(native.root);}finally{ref.stop();}
});
it('invokes a machine whose initial transition depends on input',()=>{
 const timer=createMachine({context:({input})=>({duration:input.duration}),initial:'initial',states:{initial:{always:[{target:'finished',guard:({context})=>context.duration<1000},{target:'active'}]},active:{},finished:{type:'final'}}});
 const ref=createActor(createMachine({context:{customDuration:3000},initial:'active',states:{active:{invoke:{src:timer,input:({context})=>({duration:context.customDuration})}}}}));try{ref.start();expect(ref.getSnapshot().status).toEqual(native.invoke);}finally{ref.stop();}
});
for(const chain of [false,true])it(`selects eventless transitions without an event handler ${chain}`,()=>{
 const states={a:{always:{target:'b',guard:({event})=>event.type==='WHATEVER'}},b:chain?{always:{target:'c',guard:()=>true}}:{}};if(chain)states.c={};
 const ref=createActor(createMachine({initial:'a',states})).start();try{ref.send({type:'WHATEVER'});expect(ref.getSnapshot().value).toEqual(native[`unhandled:${chain?'True':'False'}`]);}finally{ref.stop();}
});
it('preserves triggering event in a guard after two transient steps',()=>{
 let checks=0;const ref=createActor(createMachine({initial:'a',states:{a:{on:{EVENT:'b'}},b:{always:'c'},c:{always:{target:'d',guard:({event})=>{expect(event.type).toBe('EVENT');checks++;return event.type==='EVENT';}}},d:{type:'final'}}})).start();try{ref.send({type:'EVENT'});expect({checks,status:ref.getSnapshot().status}).toEqual(native.guardEvent);}finally{ref.stop();}
});
it('preserves triggering event in exit transition and entry actions',()=>{
 const seen=[];const effect=({event})=>{expect(event).toEqual({type:'EVENT',value:42});seen.push(event);};
 const ref=createActor(createMachine({initial:'a',states:{a:{on:{EVENT:'b'}},b:{always:{target:'c',actions:effect},exit:effect},c:{entry:effect}}})).start();try{ref.send({type:'EVENT',value:42});expect(seen).toEqual(native.actionEvent);}finally{ref.stop();}
});
for(const raised of [false,true])it(`reports infinite loops with ${raised?'raised':'eventless'} transitions`,()=>{
 const b=raised?{entry:raise({type:'EVENT'}),on:{EVENT:'c'}}:{always:'c'};const ref=createActor(createMachine({initial:'a',options:{maxIterations:100},states:{a:{always:'b'},b,c:{always:'a'}}}));const errors=[];ref.subscribe({error:error=>errors.push(error)});
 try{ref.start();expect(errors).toHaveLength(1);expect(errors[0].message).toMatch(/infinite loop/i);expect(errors[0].message).toEqual(native[`infinite:${raised?'True':'False'}`]);}finally{ref.stop();}
});
it('does not loop for a fire-and-forget action without a state change',()=>{
 let count=0;const ref=createActor(createMachine({initial:'idle',states:{idle:{on:{event:'active'}},active:{initial:'a',states:{a:{}},always:[{target:'.a',actions:()=>{count++;if(count>5)throw new Error('Infinite loop detected');}}]}}})).start();try{ref.send({type:'event'});expect({value:ref.getSnapshot().value,count}).toEqual(native.singleEffect);}finally{ref.stop();}
});
it('repeats assign actions until the guard becomes false',()=>{
 const ref=createActor(createMachine({context:{count:0},initial:'counting',states:{counting:{always:{guard:({context})=>context.count<5,actions:assign({count:({context})=>context.count+1})}}}})).start();try{expect(ref.getSnapshot().context.count).toEqual(native.assignLoop);}finally{ref.stop();}
});
it('checks always transitions after raised transition even without state change',()=>{
 let counter=0;const calls=[];const ref=createActor(createMachine({always:{actions:()=>calls.push(counter)},on:{EV:{actions:raise({type:'RAISED'})},RAISED:{actions:()=>counter++}}})).start();try{calls.length=0;ref.send({type:'EV'});expect(calls).toEqual(native.afterRaised);}finally{ref.stop();}
});
