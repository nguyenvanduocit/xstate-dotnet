import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromPromise,assign}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-promise-invocation.json'),'utf8'));
const key=v=>v?'True':'False';
const finish=(ref,trigger)=>new Promise((resolve,reject)=>{ref.subscribe({error:reject,complete:resolve});ref.start();trigger?.();});
const wrap=promise=>({then(yes,no){return wrap(promise.then(yes,no));}});
for(const thenable of [false,true])describe(thenable?'PromiseLike invocation':'Promise invocation',()=>{
 const producer=executor=>{const promise=new Promise(executor);return thenable?wrap(promise):promise;};
 const logic=executor=>fromPromise(args=>producer((resolve,reject)=>executor(resolve,reject,args)));
 it.each([[false,false],[true,false],[true,true]])('resolves final transitions compound=%s named=%s',async(compound,named)=>{
  const source=logic(resolve=>resolve());let config={initial:'pending',states:{pending:{invoke:{src:named?'somePromise':source,onDone:'success'}},success:{type:'final'}}};
  if(compound)config={id:'promise',initial:'parent',states:{parent:{...config,onDone:'success'},success:{type:'final'}}};
  const ref=createActor(createMachine(config,named?{actors:{somePromise:source}}:undefined));await finish(ref);expect(ref.getSnapshot().value).toEqual(native[`resolve:${key(thenable)}:${key(compound)}:${key(named)}`]);ref.stop();
 });
 it('routes executor rejection using mapped input to the handled final state',async()=>{
  const source=logic((resolve,_reject,{input})=>{if(input.succeed)resolve(input.id);else throw Error('failed on purpose for: '+input.id);});
  const ref=createActor(createMachine({id:'invokePromise',context:({input})=>({id:42,succeed:true,...input}),initial:'pending',states:{pending:{invoke:{src:source,input:({context})=>context,onDone:{target:'success',guard:({context,event})=>event.output===context.id},onError:'failure'}},success:{type:'final'},failure:{type:'final'}}}),{input:{id:31,succeed:false}});
  await finish(ref);expect(ref.getSnapshot().value).toEqual(native[`handled:${key(thenable)}`]);ref.stop();
 });
 it.each([false,true])('reports unhandled rejection without completion (strict=%s)',async strict=>{
  const ref=createActor(createMachine({id:'invokePromise',initial:'pending',states:{pending:{invoke:{src:logic(()=>{throw Error('test');}),onDone:'success'}},success:{type:'final'}}}));let completed=0;
  const received=new Promise(resolve=>ref.subscribe({error:resolve,complete:()=>completed++}));ref.start();const error=await received;expect(error).toBeInstanceOf(Error);expect({error:error.message,completed}).toEqual(native[`unhandled:${key(thenable)}:${key(strict)}`]);ref.stop();
 });
 it.each([[false,false],[true,false],[false,true],[true,true]])('provides output named=%s assign=%s',async(named,useAssign)=>{
  const source=logic(resolve=>resolve({count:1}));let observed=0;
  const action=useAssign?assign({count:({event})=>event.output.count}):({event})=>{observed=event.output.count;};
  const ref=createActor(createMachine({id:'promise',context:{count:0},initial:'pending',states:{pending:{invoke:{src:named?'somePromise':source,onDone:{target:'success',actions:action}}},success:{type:'final'}}},named?{actors:{somePromise:source}}:undefined));
  await finish(ref);expect({context:ref.getSnapshot().context.count,observed}).toEqual(native[`output:${key(thenable)}:${key(named)}:${key(useAssign)}`]);ref.stop();
 });
 it('passes context and the triggering event to a named promise',async()=>{
  const source=logic((resolve,reject,{input})=>{input.foo&&input.event.payload?resolve():reject();});
  const ref=createActor(createMachine({id:'promise',context:{foo:true},initial:'pending',states:{pending:{on:{BEGIN:'first'}},first:{invoke:{src:'somePromise',input:({context,event})=>({foo:context.foo,event}),onDone:'last'}},last:{type:'final'}}},{actors:{somePromise:source}}));
  await finish(ref,()=>ref.send({type:'BEGIN',payload:true}));expect(ref.getSnapshot().value).toEqual(native[`input:${key(thenable)}`]);ref.stop();
 });
 it('reuses one logic with separate promise results in parallel regions',async()=>{
  const source=logic(resolve=>resolve({result:Math.random()}));
  const region=field=>({initial:'active',states:{active:{invoke:{src:'getRandomNumber',onDone:{target:'success',actions:assign({[field]:({event})=>event.output.result})}}},success:{type:'final'}}});
  const ref=createActor(createMachine({context:{result1:null,result2:null},initial:'pending',states:{pending:{type:'parallel',states:{state1:region('result1'),state2:region('result2')},onDone:'done'},done:{type:'final'}}},{actors:{getRandomNumber:source}}));
  await finish(ref);const snapshot=ref.getSnapshot();expect(typeof snapshot.context.result1).toBe('number');expect(typeof snapshot.context.result2).toBe('number');expect(snapshot.context.result1).not.toBe(snapshot.context.result2);expect(snapshot.value).toEqual(native[`reuse:${key(thenable)}`]);ref.stop();
 });
 it('suppresses late snapshots after leaving the invoking state',async()=>{
  const received=[],errors=[];const source=logic(resolve=>setTimeout(()=>resolve(42),5));
  const ref=createActor(createMachine({initial:'active',states:{active:{invoke:{src:source,onSnapshot:{}},on:{deactivate:'inactive'}},inactive:{on:{'*':{actions:({event})=>{received.push(event.type);if(event.snapshot)throw Error('Unexpected snapshot event.');}}}}}}));
  ref.subscribe({error:e=>errors.push(e)});ref.start();ref.send({type:'deactivate'});await new Promise(resolve=>setTimeout(resolve,10));expect(errors).toEqual([]);expect(received).toEqual(native[`stopped:${key(thenable)}`]);expect(ref.getSnapshot().value).toBe('inactive');ref.stop();
 });
});
