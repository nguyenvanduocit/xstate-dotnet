import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromObservable,fromEventObservable,assign}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const {interval,of,map,take}=await import(pathToFileURL(resolve(root,'data/library-source/xstate-test-tools/node_modules/rxjs/dist/cjs/index.js')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-observable-invocation.json'),'utf8'));
const key=v=>v?'True':'False';
for(const events of [false,true])describe(events?'event observable invocation':'observable invocation',()=>{
 it.each(['infinite','finite','error'])('preserves counts publication and termination for %s',async mode=>{
  const source=()=>{let stream=interval(10);if(mode==='finite')stream=stream.pipe(take(5));return stream.pipe(map(value=>{if(mode==='error'&&value===5)throw Error('some error');return events?{type:'COUNT',value}:value;}));};
  const counts=[],errors=[];const invocation={src:events?fromEventObservable(source):fromObservable(source)};
  if(!events)invocation.onSnapshot={actions:assign({count:({event})=>event.snapshot.context})};
  if(mode==='finite')invocation.onDone={target:'counted',guard:({context})=>context.count===4};
  if(mode==='error')invocation.onError={target:'success',guard:({context,event})=>{expect(event.error.message).toBe('some error');errors.push(event.error.message);return context.count===4&&event.error.message==='some error';}};
  const counting={invoke:invocation};if(events)counting.on={COUNT:{actions:assign({count:({event})=>event.value})}};if(mode==='infinite')counting.always={target:'counted',guard:({context})=>context.count===5};
  const ref=createActor(createMachine({id:events||mode!=='infinite'?'obs':'infiniteObs',context:{count:undefined},initial:'counting',states:{counting,[mode==='error'?'success':'counted']:{type:'final'}}}));
  const done=new Promise((resolve,reject)=>ref.subscribe({next:s=>counts.push(s.context.count??null),error:reject,complete:resolve}));ref.start();await done;
  expect({state:ref.getSnapshot().value,count:ref.getSnapshot().context.count,counts,errors}).toEqual(native[`stream:${key(events)}:${mode}`]);ref.stop();
 });
 it('passes invoke input to the producer and receives its synchronous result',async()=>{
  let received;const done=new Promise(resolve=>{received=resolve;});const source=events?fromEventObservable(({input})=>of({type:'obs.event',value:input})):fromObservable(({input})=>of(input));
  const invocation={src:events?source:'childLogic',input:42};if(!events)invocation.onSnapshot={actions:({event})=>{if(event.snapshot.status==='active'&&event.snapshot.context===42)received(event.snapshot.context);}};
  const ref=createActor(createMachine({context:{received:undefined},invoke:invocation,...(events?{on:{'obs.event':{actions:({event})=>received(event.value)}}}:{})},events?undefined:{actors:{childLogic:source}})).start();
  expect(await done).toEqual(native[`input:${key(events)}`]);ref.stop();
 });
});
