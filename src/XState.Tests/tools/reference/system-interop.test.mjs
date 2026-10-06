import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromCallback,fromPromise,fromTransition,fromObservable,fromEventObservable,assign,sendTo,spawnChild,stopChild,toPromise}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const {of}=await import(pathToFileURL(resolve(root,'data/library-source/xstate-test-tools/node_modules/rxjs/dist/cjs/index.js')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-system-interop.json'),'utf8'));
it('reuses a stopped promise system ID within the same synchronous turn',()=>{
 const promise=fromPromise(()=>Promise.resolve());
 const machine=createMachine({context:({spawn})=>({ref:spawn(promise,{systemId:'test'})}),on:{stop:{actions:stopChild(({context})=>context.ref)},start:{actions:spawnChild(promise,{systemId:'test'})}}});
 const ref=createActor(machine).start(),original=ref.system.get('test');ref.send({type:'stop'});const removed=ref.system.get('test')===undefined;ref.send({type:'start'});
 expect({removed,replaced:original!==ref.system.get('test'),status:ref.getSnapshot().status}).toEqual(native.cleanup);ref.stop();
});
it('makes registered siblings available from custom actions and all actor logic scopes',()=>{
 for(const kind of ['referenced custom actions','sendTo actions','promise logic','transition logic','observable logic','event observable logic','callback logic']){
  const inside=[];const check=system=>inside.push(system.get('test')!==undefined);
  const sources={
   'promise logic':()=>fromPromise(({system})=>{check(system);return Promise.resolve();}),
   'transition logic':()=>fromTransition((_,__,{system})=>{check(system);return 0;},0),
   'observable logic':()=>fromObservable(({system})=>{check(system);return of(0);}),
   'event observable logic':()=>fromEventObservable(({system})=>{check(system);return of({type:'a'});}),
   'callback logic':()=>fromCallback(({system})=>{check(system);})
  };
  const invoke=[{src:createMachine({}),systemId:'test'}];if(sources[kind])invoke.push({src:sources[kind](),...(kind==='transition logic'?{systemId:'reducer'}:{})});
  const config=kind==='sendTo actions'?{invoke,initial:'a',states:{a:{entry:sendTo(({system})=>{check(system);return system.get('test');},{type:'FOO'})}}}:{invoke,...(kind==='referenced custom actions'?{entry:'myAction'}:{})};
  const ref=createActor(createMachine(config,{actions:{myAction:({system})=>check(system)}})).start();const outside=ref.system.get('test')!==undefined;
  if(kind==='transition logic')ref.system.get('reducer').send({type:'a'});expect({inside,outside}).toEqual(native[kind]);ref.stop();
 }
});
it('registers ancestors before initial child sends and unregisters invoked and spawned children',()=>{
 let calls=0;const child=createMachine({entry:sendTo(({system})=>system.get('myRoot'),{type:'EV'})});
 const ancestor=createActor(createMachine({invoke:{src:child},on:{EV:{actions:()=>calls++}}}),{systemId:'myRoot'}).start();expect(calls).toBe(native.ancestor);ancestor.stop();
 const ref=createActor(createMachine({id:'root',initial:'happy path',states:{'happy path':{entry:spawnChild(createMachine({}),{systemId:'child1'}),invoke:{src:createMachine({id:'machine'}),systemId:'child2'},on:{stopChild1:'sad path'}},'sad path':{entry:stopChild(({system})=>system.get('child1'))}}})).start();
 const all=ref.system.getAll();expect(all).toEqual({child1:ref.system.get('child1'),child2:ref.system.get('child2')});const before=Object.keys(all);ref.send({type:'stopChild1'});expect({before,after:Object.keys(ref.system.getAll())}).toEqual(native.running);ref.stop();
});
it('completes through a callback timer and routes a delayed reply between independent roots',async()=>{
 const callback=fromCallback(({receive,sendBack})=>{let timer;receive(event=>{if(event.type==='START')timer=setTimeout(()=>sendBack({type:'SEND_BACK'}),10);});return()=>clearTimeout(timer);});
 const cb=createActor(createMachine({id:'callback',context:{ref:undefined},initial:'idle',states:{idle:{entry:assign({ref:({spawn})=>spawn(callback)}),on:{START_CB:{actions:sendTo(({context})=>context.ref,{type:'START'})},SEND_BACK:'success'}},success:{type:'final'}}}));
 const callbackDone=toPromise(cb);cb.start();cb.send({type:'START_CB'});await callbackDone;expect(cb.getSnapshot().value).toBe(native.callback);cb.stop();
 const existing=createActor(createMachine({initial:'inactive',states:{inactive:{on:{ACTIVATE:'active'}},active:{entry:sendTo(({event})=>event.origin,{type:'EXISTING.DONE'})}}})).start();
 const parent=createActor(createMachine({context:{ref:undefined},initial:'pending',states:{pending:{entry:assign({ref:existing}),on:{'EXISTING.DONE':'success'},after:{100:{actions:sendTo(({context})=>context.ref,({self})=>({type:'ACTIVATE',origin:self}))}}},success:{type:'final'}}}));
 try{const done=toPromise(parent);parent.start();await done;expect({parent:parent.getSnapshot().value,existing:existing.getSnapshot().value,independent:existing._parent===undefined&&existing.system!==parent.system}).toEqual(native.existing);}finally{parent.stop();existing.stop();}
});
it('spawns initial actors only once and starts a promise nested in an invoked child',async()=>{
 let count=0;const snapshots=[];const ref=createActor(createMachine({id:'start',context:{items:[0,1,2,3],refs:[]},initial:'start',states:{start:{entry:assign({refs:({context,spawn})=>{count++;return context.items.map(item=>spawn(fromPromise(()=>Promise.resolve(item))));}})}}}));ref.subscribe(()=>snapshots.push(count));ref.start();await Promise.resolve();expect(count).toBe(native.initialOnce);expect(snapshots.length).toBeGreaterThan(0);expect(snapshots.every(value=>value===1)).toBe(true);ref.stop();
 let starts=0;const child=createMachine({context:{},initial:'bar',states:{bar:{entry:assign({promise:({spawn})=>spawn(fromPromise(()=>{starts++;return Promise.resolve('answer');}))})}}});
 const parent=createActor(createMachine({initial:'foo',states:{foo:{invoke:{src:child,onDone:'end'}},end:{type:'final'}}})).start();expect(starts).toBe(native.nestedInitial);parent.stop();
});
it('forwards promise completion and uses the custom actor parent reference for ping pong',async()=>{
 const promise=fromPromise(()=>new Promise(resolve=>setTimeout(()=>resolve(42),1)));
 const count=createActor(createMachine({context:{count:undefined},entry:assign({count:({spawn})=>spawn(promise,{id:'test'})}),initial:'pending',states:{pending:{on:{'xstate.done.actor.test':{target:'success',guard:({event})=>event.output===42}}},success:{type:'final'}}}));
 const completion=toPromise(count);count.start();await completion;expect(count.getSnapshot().value).toBe(native.fulfill);count.stop();
 const pong={transition:(state,event,{self})=>{if(event.type==='PING')self._parent?.send({type:'PONG'});return state;},getInitialSnapshot:()=>({status:'active',output:undefined,error:undefined}),getPersistedSnapshot:s=>s};
 const ping=createActor(createMachine({context:{ponger:undefined},entry:assign({ponger:({spawn})=>spawn(pong)}),initial:'waiting',states:{waiting:{entry:sendTo(({context})=>context.ponger,{type:'PING'}),invoke:{id:'ponger',src:pong},on:{PONG:'success'}},success:{type:'final'}}}));
 const pongDone=toPromise(ping);ping.start();await pongDone;expect(ping.getSnapshot().value).toBe(native.parentReference);ping.stop();
});
it('propagates the original spawned promise error to its parent',async()=>{
 const error=Error('uh oh');let resolve;const received=new Promise(r=>resolve=r);const ref=createActor(createMachine({on:{event:{actions:assign(({spawn})=>{spawn(fromPromise(async()=>{throw error;}));})}}}));
 ref.subscribe({error:e=>resolve(e)});ref.start();ref.send({type:'event'});const actual=await received;expect(actual).toBe(error);expect(actual.message).toBe(native.spawnError);ref.stop();
});
it('keeps inline actor sources local when machines share an implementations object',async()=>{
 const actors={},calls=[];let resolve;const completed=new Promise(r=>resolve=r);
 const first=createMachine({invoke:{src:fromPromise(async()=>'foo'),onDone:{actions:({event})=>{calls.push(event.output);resolve();}}}},{actors});
 createMachine({invoke:{src:fromPromise(async()=>100)}},{actors});const ref=createActor(first).start();await completed;await new Promise(resolve=>setTimeout(resolve,1));expect(calls).toEqual(native.shared);ref.stop();
 const inline={};const other=createActor(createMachine({invoke:{src:fromPromise(async()=>'foo')}},{actors:inline})).start();expect(Object.keys(inline).length).toBe(native.inline);other.stop();
});
