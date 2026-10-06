import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromPromise,fromCallback,fromObservable,fromTransition,toPromise,waitFor,emit}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-error-values.json'),'utf8'));
const routing=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-error-routing.json'),'utf8'));
const values=()=>['failure',17,false,null,{code:42,detail:null},Error('exception')];
const flush=()=>new Promise(resolve=>setTimeout(resolve,0));
function unhandled(run){const pending=[];const spy=vi.spyOn(globalThis,'setTimeout').mockImplementation(fn=>{pending.push(fn);return 0;});try{return run(()=>pending.splice(0).map(fn=>{try{fn();}catch(error){return error;}throw Error('Expected unhandled failure');}));}finally{spy.mockRestore();}}
const failed=message=>fromCallback(()=>{throw Error(message);});
function parent(child,{handled=false,done=true}={}){return createMachine({initial:'pending',states:{pending:{invoke:{src:child,...(handled?{onError:'failed'}:done?{onDone:'success'}:{})}},...(handled?{failed:{type:'final'}}:done?{success:{type:'final'}}:{})}});}
it('preserves raw promise rejection values and identity through tasks observers persistence and restoration',async()=>{
 const observations=[];
 for(const reason of values()){
  const received=[],ref=createActor(fromPromise(()=>Promise.reject(reason)));ref.subscribe({error:e=>received.push(e)});
  const promise=toPromise(ref).catch(e=>({e})),waiting=waitFor(ref,()=>false).catch(e=>({e}));ref.start();
  expect((await promise).e).toBe(reason);expect((await waiting).e).toBe(reason);expect(ref.getSnapshot().error).toBe(reason);expect(ref.getSnapshot().status).toBe('error');expect(received).toEqual([reason]);
  ref.subscribe({error:e=>received.push(e)});expect(received[1]).toBe(reason);expect((await toPromise(ref).catch(e=>({e}))).e).toBe(reason);
  expect(ref.getPersistedSnapshot()).toBe(ref.getSnapshot());const json=JSON.parse(JSON.stringify(ref.getPersistedSnapshot()));let calls=0,notified=0,restoredError;
  const restored=createActor(fromPromise(()=>{calls++;return Promise.resolve(1);}),{snapshot:json});restored.subscribe({error:e=>{notified++;restoredError=e;}});restored.start();
  expect(calls).toBe(0);expect(restoredError).toBe(restored.getSnapshot().error);expect(JSON.parse(JSON.stringify(restored.getPersistedSnapshot()))).toEqual(json);
  observations.push({json,observed:received.length,restored:notified});ref.stop();restored.stop();
 }expect(observations).toEqual(native.promise);
});
it('preserves raw thenable rejections including null',async()=>{
 for(const reason of values()){
  const ref=createActor(fromPromise(()=>({then(_resolve,reject){reject(reason);}})));const waiting=toPromise(ref).catch(e=>({e}));ref.start();expect((await waiting).e).toBe(reason);expect(ref.getSnapshot().error).toBe(reason);expect(ref.getSnapshot().status).toBe('error');ref.stop();
 }
});
it('preserves thrown raw values across callback reducer machine and promise creator boundaries',()=>{
 for(const reason of values()){
  const logics=[fromCallback(()=>{throw reason;}),fromTransition(()=>{throw reason;},0),createMachine({context:()=>{throw reason;}}),createMachine({entry:()=>{throw reason;}}),fromPromise(()=>{throw reason;})];
  for(const [index,logic] of logics.entries()){
   const seen=[],ref=createActor(logic);ref.subscribe({error:e=>seen.push(e)});ref.start();if(index===1)ref.send({type:'FAIL'});expect(seen.length).toBe(1);expect(seen[0]).toBe(reason);expect(ref.getSnapshot().error).toBe(reason);expect(ref.getSnapshot().status).toBe('error');ref.stop();
  }
 }
});
it('preserves observable raw errors and their persisted representation',()=>{
 const snapshots=[];for(const reason of values()){
  const seen=[],ref=createActor(fromObservable(()=>({subscribe(observer){observer.error(reason);return {unsubscribe(){}};}})));ref.subscribe({error:e=>seen.push(e)});ref.start();expect(seen[0]).toBe(reason);expect(ref.getSnapshot().error).toBe(reason);snapshots.push(JSON.parse(JSON.stringify(ref.getPersistedSnapshot())));ref.stop();
 }expect(snapshots).toEqual(native.observable);
});
it('reports raw errors for missing observers late observers and throwing listeners',()=>unhandled(drain=>{
 for(const reason of values()){
  const seen=[],logic=fromCallback(()=>{throw reason;}),ref=createActor(logic);ref.subscribe({error:e=>seen.push(e)});ref.subscribe(()=>{});ref.start();expect(seen[0]).toBe(reason);expect(drain()).toEqual([reason]);ref.subscribe(()=>{});expect(drain()).toEqual([reason]);
  const listener=createActor(logic);listener.subscribe({error:()=>{throw reason;}});listener.start();expect(drain()).toEqual([reason]);ref.stop();listener.stop();
 }
}));
it('rejects waitFor with explicit raw cancellation reasons before and after registration',async()=>{
 for(const reason of values())for(const before of [false,true]){
  const controller=new AbortController(),ref=createActor(fromTransition(c=>c,0)).start();if(before)controller.abort(reason);
  const waiting=waitFor(ref,()=>false,{signal:controller.signal}).catch(e=>({e}));if(!before)controller.abort(reason);expect((await waiting).e).toBe(reason);ref.stop();
 }
});
it('matches child observer combinations and duplicate global reporting',()=>unhandled(drain=>{
 for(const [listeners,handled] of [[0,true],[1,true],[2,true],[1,false]]){
  const ref=createActor(parent(failed('handled_sync_error_in_actor_start'),{handled,done:false}));const child=Object.values(ref.getSnapshot().children)[0];
  if(listeners){child.subscribe({error:()=>{}});child.subscribe(listeners===2?{error:()=>{}}:()=>{});}ref.start();
  expect(drain().map(e=>e.message)).toEqual(routing[`child${listeners}:${handled?'True':'False'}`]);ref.stop();
 }
}));
it('matches original and observer-thrown error report ordering',()=>unhandled(drain=>{
 for(const [throws,missing] of [[true,false],[false,true],[true,true]]){
  const message=missing?'error_thrown_when_not_every_observer_comes_with_an_error_listener':'handled_sync_error_in_actor_start';const ref=createActor(parent(failed(message),{done:false}));
  ref.subscribe({error:()=>{if(throws)throw Error('error_thrown_by_error_listener');}});if(missing)ref.subscribe(()=>{});ref.start();expect(drain().map(e=>e.message)).toEqual(routing[`observer${throws?'True':'False'}:${missing?'True':'False'}`]);ref.stop();
 }
}));
it('routes async rejection to root or grandparent listeners with original identity',async()=>{
 for(const depth of [1,2]){
  const reason=Error('unhandled_rejection_in_promise_actor_'+(depth===1?'with_parent_listener':'with_grandparent_listener'));let machine=parent(fromPromise(()=>Promise.reject(reason)));if(depth===2)machine=parent(machine);
  const observed=[],ref=createActor(machine);ref.subscribe({error:e=>observed.push(e)});ref.start();await flush();expect(observed).toEqual([reason]);expect({reported:[],observed:observed.map(e=>e.message)}).toEqual(routing['promise'+depth]);ref.stop();
 }
});
it('keeps an actor active and delivers subsequent emissions after a raw listener error',()=>unhandled(drain=>{
 const ref=createActor(createMachine({on:{GO:{actions:emit({type:'emitted',value:'bar'})}}})).start();let delivered;
 ref.on('emitted',()=>{throw null;});ref.send({type:'GO'});expect(drain()).toEqual([null]);ref.on('emitted',ev=>{delivered=ev.value;});ref.send({type:'GO'});expect(drain()).toEqual([null]);expect(delivered).toBe('bar');expect(ref.getSnapshot().status).toBe('active');ref.stop();
}));
