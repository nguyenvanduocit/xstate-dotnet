import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromCallback,fromPromise,assign,raise,sendParent,sendTo,forwardTo}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-remaining-invocation.json'),'utf8'));
const finish=(ref,trigger)=>new Promise((resolve,reject)=>{ref.subscribe({complete:resolve,error:reject});ref.start();trigger?.();});
const sleep=delay=>new Promise(resolve=>setTimeout(resolve,delay));
const key=v=>v?'True':'False';
it.each([false,true])('starts an explicit child machine configured=%s',async configured=>{
 const user={name:'David'};
 const child=configured?createMachine({id:'fetch',context:({input})=>({userId:input.userId}),initial:'pending',states:{pending:{entry:raise({type:'RESOLVE',user}),on:{RESOLVE:{target:'success',guard:({context})=>context.userId!==undefined}}},success:{type:'final',entry:assign({user:({event})=>event.user})},failure:{entry:sendParent({type:'REJECT'})}},output:({context})=>({user:context.user})}):createMachine({initial:'pending',states:{pending:{entry:raise({type:'RESOLVE'}),on:{RESOLVE:'success'}},success:{type:'final'}}});
 const ref=createActor(createMachine({id:configured?'fetcher':undefined,context:{selectedUserId:'42',user:undefined},initial:'idle',states:{idle:{on:{GO_TO_WAITING:'waiting'}},waiting:{invoke:{src:child,...(configured?{input:({context})=>({userId:context.selectedUserId})}:{}),onDone:{target:'received',...(configured?{guard:({event})=>event.output.user.name==='David'}:{})}}},received:{type:'final'}}}));
 await finish(ref,()=>ref.send({type:'GO_TO_WAITING'}));expect(ref.getSnapshot().value).toEqual(native[`start:${key(configured)}`]);ref.stop();
});
it('uses an actor implementation overwritten by provide',async()=>{
 const child=createMachine({id:'child',initial:'init',states:{init:{}}});const machine=createMachine({id:'parent',context:{count:0},initial:'start',states:{start:{invoke:{src:'child',id:'someService'},on:{STOP:'stop'}},stop:{type:'final'}}},{actors:{child}});
 const ref=createActor(machine.provide({actors:{child:createMachine({id:'child',initial:'init',states:{init:{entry:sendParent({type:'STOP'})}}})}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native.provided);ref.stop();
});
const child=final=>createMachine({id:'child',initial:'one',states:{one:{on:{NEXT:'two'}},two:final?{type:'final'}:{entry:sendParent({type:'NEXT'})}}});
it.each([false,true])('communicates with a machine invoked at root=%s',async root=>{
 const invocation={id:'foo-child',src:child(false)};const ref=createActor(createMachine({id:'parent',initial:'one',...(root?{invoke:invocation}:{}),states:{one:{...(!root?{invoke:invocation}:{}),entry:sendTo('foo-child',{type:'NEXT'}),on:{NEXT:'two'}},two:{type:'final'}}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native[`communication:${key(root)}`]);ref.stop();
});
it('processes immediate child completion while publishing the parent state',()=>{
 const ref=createActor(createMachine({id:'parent',initial:'one',states:{one:{invoke:{id:'foo-child',src:child(true),onDone:'two'},entry:sendTo('foo-child',{type:'NEXT'})},two:{on:{NEXT:'three'}},three:{type:'final'}}})).start();expect(ref.getSnapshot().value).toEqual(native.directFinal);ref.stop();
});
it('completes orthogonal invocation using child output',async()=>{
 const pong=createMachine({id:'pong',initial:'active',states:{active:{type:'final'}},output:{secret:'pingpong'}});
 const ref=createActor(createMachine({id:'ping',type:'parallel',states:{one:{initial:'active',states:{active:{invoke:{id:'pong',src:pong,onDone:{target:'success',guard:({event})=>event.output.secret==='pingpong'}}},success:{type:'final'}}}}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native.orthogonal);ref.stop();
});
it('keeps root invocation alive across non-reentering updates',()=>{
 let starts=0,stops=0,entries=0,actions=0;const trace=[];const ref=createActor(createMachine({invoke:{src:fromCallback(()=>{starts++;return ()=>stops++;})},entry:()=>entries++,on:{UPDATE:{actions:()=>actions++}}})).start();
 for(let i=0;i<3;i++){if(i)ref.send({type:'UPDATE'});trace.push({entries,starts,stops,actions});}expect(trace).toEqual(native.nonReentering);ref.stop();
});
it('stops a root invocation on final state',()=>{
 let stopped=false;const ref=createActor(createMachine({id:'machine',invoke:{src:fromCallback(()=>()=>{stopped=true;})},initial:'running',states:{running:{on:{finished:'complete'}},complete:{type:'final'}}})).start();ref.send({type:'finished'});expect(stopped).toEqual(native.stopFinal);ref.stop();
});
it('does not restart child invocation when its parent stops during the child transition',async()=>{
 let starts=0;const child=createMachine({id:'child',initial:'idle',states:{idle:{invoke:{src:fromCallback(({sendBack})=>{starts++;if(starts>1)throw Error('This should be impossible.');setTimeout(()=>sendBack({type:'STARTED'}));})},on:{STARTED:'active'}},active:{invoke:{src:fromCallback(({sendBack})=>sendBack({type:'STOPPED'}))},on:{STOPPED:{target:'idle',actions:forwardTo('#_parent')}}}}});
 const ref=createActor(createMachine({id:'parent',initial:'idle',states:{idle:{on:{START:'active'}},active:{invoke:{src:child},on:{STOPPED:'done'}},done:{type:'final'}}}));const done=new Promise((resolve,reject)=>ref.subscribe({error:reject,complete:()=>{expect(starts).toBe(1);resolve();}}));ref.start();ref.send({type:'START'});await done;expect(starts).toEqual(native.stopDuringTransition);ref.stop();
});
it.each([false,true])('passes named invoke input dynamic=%s',async dynamic=>{
 const ref=createActor(createMachine({context:{url:'example.com'},initial:'searching',states:{searching:{invoke:{src:'search',input:dynamic?({context})=>({endpoint:context.url}):{endpoint:'example.com'},onDone:'success'}},success:{type:'final'}}},{actors:{search:fromPromise(({input})=>{expect(input.endpoint).toBe('example.com');return Promise.resolve(42);})}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native[`input:${key(dynamic)}`]);ref.stop();
});
it('generates done event ID from the invoking state',async()=>{
 let actual;const ref=createActor(createMachine({initial:'a',states:{a:{invoke:{src:'someSrc',onDone:{target:'b',guard:({event})=>{actual=event.type;expect(actual).toBe('xstate.done.actor.0.(machine).a');return actual==='xstate.done.actor.0.(machine).a';}}}},b:{type:'final'}}},{actors:{someSrc:fromPromise(()=>Promise.resolve())}}));await finish(ref);expect(actual).toEqual(native.generatedId);ref.stop();
});
it('selects onDone only for the invoking region despite identical source names',async()=>{
 let counter=0,invoked=false;const region=()=>({initial:'fetch',states:{fetch:{invoke:{src:'fetchSmth',onDone:{actions:'handleSuccess'}}}}});
 const ref=createActor(createMachine({type:'parallel',states:{first:region(),second:region()}},{actions:{handleSuccess:()=>counter++},actors:{fetchSmth:fromPromise(()=>{if(invoked)return new Promise(()=>{});invoked=true;return Promise.resolve(42);})}})).start();await sleep(0);expect(counter).toEqual(native.namedDone);ref.stop();
});
it('keeps generated done event names unique for reused machines with their own ID',async()=>{
 const actual=[];const child=createMachine({id:'child',initial:'a',states:{a:{invoke:{src:fromPromise(()=>Promise.resolve(42)),onDone:'b'}},b:{type:'final'}}});const region=()=>({initial:'fetch',states:{fetch:{invoke:{src:child}}}});
 const ref=createActor(createMachine({type:'parallel',states:{first:region(),second:region()},on:{'*':{actions:({event})=>actual.push(event)}}})).start();await sleep(0);expect(actual.every(e=>e.output===undefined)).toBe(true);expect(actual.map(e=>({...e,output:e.output??null}))).toEqual(native.uniqueDone);ref.stop();
});
it('restarts root invocations on a root reentering transition',()=>{
 let count=0;const ref=createActor(createMachine({id:'root',invoke:{src:fromPromise(()=>{count++;return Promise.resolve(42);})},on:{EVENT:{target:'#two',reenter:true}},initial:'one',states:{one:{},two:{id:'two'}}})).start();ref.send({type:'EVENT'});expect(count).toEqual(native.rootReentry);ref.stop();
});
it('delivers entry delayed sends to the newly invoked actor',async()=>{
 const child=createMachine({on:{PING:{actions:sendTo(({event})=>event.origin,{type:'PONG'})}}});const ref=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{invoke:{id:'foo',src:child},entry:sendTo('foo',({self})=>({type:'PING',origin:self}),{delay:1}),on:{PONG:'c'}},c:{type:'final'}}})).start();ref.send({type:'NEXT'});await sleep(3);expect(ref.getSnapshot().status).toEqual(native.delayed);ref.stop();
});
it('provides computed input to a named actor creator',async()=>{
 const ref=createActor(createMachine({context:{count:42},initial:'pending',states:{pending:{invoke:{src:'stringService',input:({context})=>({staticVal:'hello',newCount:context.count*2}),onDone:'success'}},success:{type:'final'}}},{actors:{stringService:fromPromise(({input})=>{expect(input).toEqual({newCount:84,staticVal:'hello'});return Promise.resolve(true);})}}));await finish(ref);expect(ref.getSnapshot().value).toEqual(native.creatorInput);ref.stop();
});
