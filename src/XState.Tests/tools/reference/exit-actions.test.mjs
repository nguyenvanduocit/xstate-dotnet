import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,assign,sendParent,sendTo}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const {trackEntries}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/test/utils.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-exit-actions.json'),'utf8'));
const key=v=>v?'True':'False';
it.each([false,true])('does not exit or reenter on a targetless transition delayed=%s',async delayed=>{
 const machine=createMachine({initial:'one',...(!delayed?{on:{WHATEVER:{actions:()=>{}}}}:{}),states:{one:delayed?{after:{10:{actions:()=>{}}}}:{}}});const flush=trackEntries(machine);const ref=createActor(machine).start();flush();if(delayed)await new Promise(resolve=>setTimeout(resolve,50));else ref.send({type:'WHATEVER'});expect(flush()).toEqual(native[`targetless:${key(delayed)}`]);ref.stop();
});
it('executes child and root exit actions before invoked final completion',async()=>{
 let root=false,leaf=false;const child=createMachine({exit:()=>{root=true;},initial:'a',states:{a:{type:'final',exit:()=>{leaf=true;}}}});
 const ref=createActor(createMachine({initial:'active',states:{active:{invoke:{src:child,onDone:'finished'}},finished:{type:'final'}}}));const done=new Promise((resolve,reject)=>ref.subscribe({error:reject,complete:()=>{expect({root,leaf}).toEqual(native.finalExit);resolve();}}));ref.start();await done;ref.stop();
});
it('does not execute state or root exit actions on stop',()=>{
 let root=0,leaf=0;const ref=createActor(createMachine({exit:()=>root++,initial:'a',states:{a:{exit:()=>leaf++}}})).start();ref.stop();expect({root,leaf}).toEqual(native.stopExit);
});
it('passes the last received event into final exit actions',()=>{
 let received;const ref=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{type:'final'}},exit:({event})=>{received=event;}})).start();ref.send({type:'NEXT'});expect(received).toEqual({type:native.lastEvent});ref.stop();
});
it('can stop a parent whose child has an exit sendParent action',()=>{
 const child=createMachine({id:'child',initial:'idle',states:{idle:{exit:sendParent({type:'EXIT'})}}});const ref=createActor(createMachine({id:'parent',invoke:{src:child}})).start();expect(()=>ref.stop()).not.toThrow();
});
it('delivers done child exit events to the parent',()=>{
 let received=false;const child=createMachine({id:'child',initial:'active',states:{active:{on:{FINISH:'done'}},done:{type:'final'}},exit:sendParent({type:'CHILD_DONE'})});
 const ref=createActor(createMachine({id:'parent',context:({spawn})=>({child:spawn(child)}),on:{FINISH_CHILD:{actions:sendTo(({context})=>context.child,{type:'FINISH'})},CHILD_DONE:{actions:()=>{received=true;}}}})).start();ref.send({type:'FINISH_CHILD'});expect(received).toEqual(native.doneChildSend);ref.stop();
});
it.each([false,true])('delivers exit sends to grandchildren only on done (done=%s)',done=>{
 let calls=0;const grandchild=createMachine({id:'grandchild',on:{STOPPED:{actions:()=>calls++}}});
 const child=createMachine({id:'child',invoke:{id:'myChild',src:grandchild},exit:sendTo('myChild',{type:'STOPPED'}),...(done?{initial:'a',states:{a:{on:{FINISH:'b'}},b:{type:'final'}}}:{})});
 const config=done?{id:'parent',invoke:{id:'myChild',src:child},on:{NEXT:{actions:sendTo('myChild',{type:'FINISH'})}}}:{id:'parent',initial:'a',states:{a:{invoke:{src:child},on:{NEXT:'b'}},b:{}}};const ref=createActor(createMachine(config)).start();ref.send({type:'NEXT'});expect(calls).toEqual(native[`grandchild:${key(done)}`]);ref.stop();
});
it('does not spawn actors in exit handlers on stop',()=>{
 const child=createMachine({id:'grandchild',entry:()=>{throw Error('This should not be called.');}});const ref=createActor(createMachine({id:'parent',context:{},exit:assign({actorRef:({spawn})=>spawn(child)})})).start();ref.stop();expect(ref.getSnapshot().context).toEqual({});
});
it('does not execute referenced custom exit actions on stop',()=>{
 let calls=0;const ref=createActor(createMachine({id:'parent',context:{},exit:'referencedAction'},{actions:{referencedAction:()=>calls++}})).start();ref.stop();expect(calls).toEqual(native.referencedExit);
});
it('does not execute inline or referenced builtin exit actions on stop',()=>{
 const ref=createActor(createMachine({context:{executedAssigns:[]},exit:['referencedAction',assign({executedAssigns:({context})=>[...context.executedAssigns,'inline']})]},{actions:{referencedAction:assign({executedAssigns:({context})=>[...context.executedAssigns,'referenced']})}})).start();ref.stop();expect(ref.getSnapshot().context.executedAssigns).toEqual(native.builtinExit);
});
it('clears queued events when stopped during event processing',()=>{
 const ref=createActor(createMachine({on:{INITIALIZE_SYNC_SEQUENCE:{actions:({self})=>{self.send({type:'SOME_EVENT'});self.send({type:'SOME_EVENT'});self.stop();}},SOME_EVENT:{actions:()=>{throw Error('This should not be called.');}}}})).start();ref.send({type:'INITIALIZE_SYNC_SEQUENCE'});expect(ref.getSnapshot().status).toEqual(native.clearQueued);ref.stop();
});
it.each([false,true])('retains microstep action order when stopped by an action transitionAction=%s',transitionAction=>{
 const trace=[];const ref=createActor(createMachine({initial:'foo',states:{foo:{exit:()=>trace.push(transitionAction?'foo exit action':'foo action'),on:{INITIALIZE_SYNC_SEQUENCE:{target:'bar',actions:[({self})=>self.stop(),()=>{if(transitionAction)trace.push('foo transition action');}]}}},bar:{exit:()=>trace.push(transitionAction?'bar exit action':'bar action')}}})).start();ref.send({type:'INITIALIZE_SYNC_SEQUENCE'});expect(trace).toEqual(native[`reentrant:${key(transitionAction)}`]);ref.stop();
});
it('orders parallel exits before transition actions and entries',()=>{
 const trace=[],track=name=>()=>trace.push(name);const region=name=>({initial:name+'1',entry:track('enter_'+name),exit:track('exit_'+name),states:{[name+'1']:{entry:track('enter_'+name+'1'),exit:track('exit_'+name+'1'),on:{CHANGE:{target:name+'2',actions:name==='a'?[track('do_a2'),track('another_do_a2')]:track('do_b2')}}},[name+'2']:{entry:track('enter_'+name+'2'),exit:track('exit_'+name+'2')}}});
 const ref=createActor(createMachine({type:'parallel',states:{a:region('a'),b:region('b')}})).start();trace.length=0;ref.send({type:'CHANGE'});expect(trace).toEqual(native.parallel);ref.stop();
});
it('calls custom entry exit and transition functions without changing tracked state ordering',()=>{
 let entries=0,exits=0,transitions=0;const machine=createMachine({initial:'a',states:{a:{initial:'a1',states:{a1:{on:{NEXT_FN:'a3'}},a2:{},a3:{entry:()=>entries++,exit:()=>exits++,on:{NEXT:{target:'a2',actions:()=>transitions++}}}}}}});const flush=trackEntries(machine);const ref=createActor(machine).start();flush();ref.send({type:'NEXT_FN'});const first=flush();expect(entries).toBeGreaterThan(0);ref.send({type:'NEXT'});const second=flush();expect({first,second,entries,exits,transitions}).toEqual(native.functions);ref.stop();
});
it('runs root entry and exit during a root reentering transition',()=>{
 let entries=0,exits=0;const ref=createActor(createMachine({id:'root',entry:()=>entries++,exit:()=>exits++,on:{EVENT:{target:'#two',reenter:true}},initial:'one',states:{one:{},two:{id:'two'}}})).start();entries=exits=0;ref.send({type:'EVENT'});expect({entries,exits}).toEqual(native.rootReentry);ref.stop();
});
