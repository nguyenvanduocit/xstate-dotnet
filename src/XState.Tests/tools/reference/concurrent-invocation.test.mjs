import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromCallback,fromPromise,assign,raise}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-concurrent-invocation.json'),'utf8'));
const finish=(ref,trigger)=>new Promise((resolve,reject)=>{ref.subscribe({complete:resolve,error:reject});ref.start();trigger?.();});
it.each([false,true])('starts simultaneous services parallel=%s',async parallel=>{
 const invoke=(id,type)=>({id,src:fromCallback(({sendBack})=>sendBack({type}))});
 const leaf=parallel?{type:'parallel',states:{a:{invoke:invoke('child','ONE')},b:{invoke:invoke('child2','TWO')}}}:{invoke:[invoke('child','ONE'),invoke('child2','TWO')]};
 const ref=createActor(createMachine({id:'machine',context:{},initial:'one',on:{ONE:{actions:assign({one:'one'})},TWO:{actions:assign({two:'two'}),...(!parallel?{target:'.three'}:{})}},...(parallel?{after:{10:'.three'}}:{}),states:{one:{initial:'two',states:{two:leaf}},three:{type:'final'}}}));
 await finish(ref);expect(ref.getSnapshot().context).toEqual(native[`multiple:${parallel?'True':'False'}`]);ref.stop();
});
it.each([false,true])('does not start an invocation exited in a microstep subsequent=%s',subsequent=>{
 let started=false;const invoke={id:'doNotInvoke',src:fromCallback(()=>{started=true;})};
 const active=subsequent?{invoke,initial:'first',states:{first:{always:'second'},second:{always:'#inactive'}}}:{invoke,always:'inactive'};
 const ref=createActor(createMachine({id:subsequent?undefined:'transient',initial:subsequent?'withNonLeafInvoke':'active',states:{[subsequent?'withNonLeafInvoke':'active']:active,inactive:{id:subsequent?'inactive':undefined}}})).start();expect(started).toEqual(native[`skipped:${subsequent?'True':'False'}`]);ref.stop();
});
it('starts the other region after stopping one invocation in a raised-event microstep',async()=>{
 const ref=createActor(createMachine({initial:'running',states:{running:{type:'parallel',states:{one:{initial:'active',on:{STOP_ONE:'.idle'},states:{idle:{},active:{invoke:{id:'active',src:fromCallback(()=>{})},on:{NEXT:{actions:raise({type:'STOP_ONE'})}}}}},two:{initial:'idle',on:{NEXT:'.active'},states:{idle:{},active:{invoke:{id:'post',src:fromPromise(()=>Promise.resolve(42)),onDone:'#done'}}}}}},done:{id:'done',type:'final'}}}));
 await finish(ref,()=>ref.send({type:'NEXT'}));expect(ref.getSnapshot().value).toEqual(native.otherRegion);ref.stop();
});
it('starts only the settled invocation when returning to its state in one macrostep',()=>{
 let starts=0;const ref=createActor(createMachine({context:{counter:0},initial:'active',states:{active:{invoke:{src:fromCallback(()=>{starts++;})},always:{target:'inactive',guard:({context})=>context.counter===0}},inactive:{entry:assign({counter:({context})=>++context.counter}),always:'active'}}})).start();expect(starts).toEqual(native.reentry);ref.stop();
});
