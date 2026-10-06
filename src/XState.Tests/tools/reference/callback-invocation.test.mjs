import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromCallback,assign,sendTo}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-callback-invocation.json'),'utf8'));
const finish=(ref,trigger)=>new Promise((resolve,reject)=>{ref.subscribe({error:reject,complete:resolve});ref.start();trigger?.();});
it('passes callback input and handles multiple synchronous sent-back events',async()=>{
 const source=fromCallback(({input,sendBack})=>{if(input.foo&&input.event.type==='BEGIN')for(const data of [40,41,42])sendBack({type:'CALLBACK',data});});
 const ref=createActor(createMachine({id:'callback',context:{foo:true},initial:'pending',states:{pending:{on:{BEGIN:'first'}},first:{invoke:{src:'someCallback',input:({context,event})=>({foo:context.foo,event})},on:{CALLBACK:{target:'last',guard:({event})=>event.data===42}}},last:{type:'final'}}},{actors:{someCallback:source}}));
 await finish(ref,()=>ref.send({type:'BEGIN',payload:true}));expect(ref.getSnapshot().value).toEqual(native.service);ref.stop();
});
it.each([false,true])('publishes invoking state before callback response (initial=%s)',initial=>{
 const states={ [initial?'idle':'first']:{invoke:{src:'someCallback'},on:{CALLBACK:'intermediate'}},intermediate:{on:{NEXT:'last'}},last:{type:'final'}};
 if(!initial)states.pending={on:{BEGIN:'first'}};
 const ref=createActor(createMachine({id:'callback',context:{foo:true},initial:initial?'idle':'pending',states},{actors:{someCallback:fromCallback(({sendBack})=>sendBack({type:'CALLBACK'}))}}));const values=[];ref.subscribe(s=>values.push(s.value));ref.start().send({type:'BEGIN'});expect(values).toEqual(native[`publication:${initial?'True':'False'}`]);ref.stop();
});
it('cleans up periodic callback delivery on final transition and cleanup on exit',async()=>{
 const ref=createActor(createMachine({id:'interval',context:{count:0},initial:'counting',states:{counting:{invoke:{id:'intervalService',src:fromCallback(({sendBack})=>{const timer=setInterval(()=>sendBack({type:'INC'}),10);return ()=>clearInterval(timer);})},always:{target:'finished',guard:({context})=>context.count===3},on:{INC:{actions:assign({count:({context})=>context.count+1})}}},finished:{type:'final'}}}));
 await finish(ref);expect(ref.getSnapshot().context.count).toEqual(native.stream);ref.stop();
 let disposed=0;const cleanup=createActor(createMachine({id:'interval',initial:'counting',states:{counting:{invoke:{id:'intervalService',src:fromCallback(()=>()=>disposed++)},on:{NEXT:'idle'}},idle:{}}})).start();cleanup.send({type:'NEXT'});expect(disposed).toEqual(native.disposal);cleanup.stop();
});
it('delivers entry sends to an invoked callback before parent completion',async()=>{
 const ref=createActor(createMachine({id:'ping-pong',initial:'active',states:{active:{invoke:{id:'child',src:fromCallback(({receive,sendBack})=>receive(event=>{if(event.type==='PING')sendBack({type:'PONG'});}))},entry:sendTo('child',{type:'PING'}),on:{PONG:'done'}},done:{type:'final'}}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native.ping);ref.stop();
});
it('selects the invoking region for synchronous errors',()=>{
 const region=fails=>({initial:'waiting',states:{waiting:{invoke:{src:fromCallback(()=>{if(fails)throw Error('test');return ()=>{};}),onError:'failed'}},failed:{}}});
 const ref=createActor(createMachine({initial:'start',states:{start:{on:{FETCH:'fetch'}},fetch:{type:'parallel',states:{first:region(true),second:region(false)}}}})).start();ref.send({type:'FETCH'});expect(ref.getSnapshot().value).toEqual(native.parallelError);ref.stop();
 const failures=[],unhandled=createActor(createMachine({initial:'safe',states:{safe:{invoke:{src:fromCallback(()=>{throw Error('test');})}},failed:{type:'final'}}}));unhandled.subscribe({error:e=>failures.push(e.message)});unhandled.start();expect(failures).toEqual(native.unhandled);unhandled.stop();
});
it('processes child completion before returning from parent send',()=>{
 const child=createMachine({id:'child',initial:'start',states:{start:{on:{STOP:'end'}},end:{type:'final'}}});
 const ref=createActor(createMachine({id:'parent',initial:'begin',states:{begin:{invoke:{id:'invoked.child',src:child,onDone:'completed'},on:{STOPCHILD:{actions:sendTo('invoked.child',{type:'STOP'})}}},completed:{type:'final'}}})).start();ref.send({type:'STOPCHILD'});expect(ref.getSnapshot().value).toEqual(native.nested);ref.stop();
});
