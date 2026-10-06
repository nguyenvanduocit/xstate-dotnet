import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromPromise,assign,getInitialSnapshot}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-thenable.json'),'utf8'));
const flush=()=>new Promise(resolve=>setTimeout(resolve,0));
it('reads then synchronously and invokes it before a separate actor reaction',async()=>{
 const trace=[];const ref=createActor(fromPromise(()=>{trace.push('creator');return {get then(){trace.push('get');return resolve=>{trace.push('then');resolve(42);trace.push('after-resolve');};}};}));
 ref.subscribe({next:s=>trace.push('snapshot:'+s.status),complete:()=>trace.push('complete')});ref.start();trace.push('after-start');expect(trace).toEqual(['creator','get','snapshot:active','after-start']);await flush();expect(trace).toEqual(native.ordering);expect(ref.getSnapshot().output).toBe(42);ref.stop();
});
it('keeps the first resolution or rejection despite duplicate calls and a later throw',async()=>{
 const outcomes=[];for(const rejectFirst of [false,true]){
  const original=Error('first'),errors=[],seen=[];
  const ref=createActor(fromPromise(()=>({then(resolve,reject){if(rejectFirst)reject(original);else resolve(42);resolve(99);reject(Error('late'));throw Error('after');}})));
  ref.subscribe({next:s=>seen.push(s.status),error:e=>errors.push(e)});ref.start();await flush();
  if(rejectFirst){expect(errors).toEqual([original]);expect(ref.getSnapshot().output).toBeUndefined();}else expect(ref.getSnapshot().output).toBe(42);
  outcomes.push({rejectFirst,seen,error:errors[0]?.message??null,output:ref.getSnapshot().output??null});ref.stop();
 }expect(outcomes).toEqual(native.settlement);
});
it('adopts nested thenables in separate jobs while locking the outer resolver',async()=>{
 const trace=[];const inner={get then(){trace.push('inner-get');return resolve=>{trace.push('inner-then');resolve(7);};}};
 const ref=createActor(fromPromise(()=>({get then(){trace.push('outer-get');return resolve=>{trace.push('outer-then');resolve(inner);trace.push('after-adopt');resolve(99);throw Error('ignored');};}})));
 ref.subscribe(s=>trace.push(s.status));ref.start();await flush();expect(trace).toEqual(native.nested);expect(ref.getSnapshot().output).toBe(7);ref.stop();
});
it('distinguishes creator errors from then getter and invocation rejection',async()=>{
 const results=[];for(const site of ['creator','getter','then']){
  const original=Error(site),seen=[],errors=[];const ref=createActor(fromPromise(()=>{if(site==='creator')throw original;return {get then(){if(site==='getter')throw original;return ()=>{throw original;};}};}));
  ref.subscribe({next:s=>seen.push(s.status),error:e=>errors.push(e)});ref.start();expect(errors.length).toBe(site==='creator'?1:0);await flush();expect(errors).toEqual([original]);expect(ref.getSnapshot().error).toBe(original);results.push({site,seen,error:errors[0].message});ref.stop();
 }expect(results).toEqual(native.failures);
});
it('still invokes the then producer after stop but suppresses late actor output',async()=>{
 const trace=[];const ref=createActor(fromPromise(({signal})=>({then(resolve){trace.push(signal.aborted?'aborted':'live');resolve(42);}})));
 ref.subscribe(s=>trace.push(s.status));ref.start();ref.stop();trace.push('stopped');await flush();expect(trace).toEqual(native.stop);expect(ref.getSnapshot().status).toBe('stopped');expect(ref.getSnapshot().output).toBeUndefined();
});
it('supports synchronous child then resolution and leaves pure initial calculation inert',async()=>{
 const logic=fromPromise(()=>({then(resolve){resolve(null);}}));const ref=createActor(createMachine({context:{child:null},entry:assign({child:({spawn})=>spawn(logic)})}));
 expect(()=>ref.start()).not.toThrow();await flush();expect(ref.getSnapshot().context.child.getSnapshot().status).toBe('done');expect(ref.getSnapshot().context.child.getSnapshot().output).toBeNull();ref.stop();
 let calls=0;const lazy=fromPromise(()=>{calls++;return {then(resolve){resolve(1);}};});const unstarted=createActor(lazy);unstarted.stop();expect(getInitialSnapshot(lazy).status).toBe('active');await flush();expect(calls).toBe(0);
});
