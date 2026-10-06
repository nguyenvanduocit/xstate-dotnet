import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromCallback,forwardTo,sendParent,assign,cancel,SimulatedClock,initialTransition,transition}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-forwarding.json'),'utf8'));
it.each([false,true])('forwards an event to an invoked or dynamically spawned child dynamic=%s',async dynamic=>{
 const child=createMachine({id:'child',initial:'active',states:{active:{on:{EVENT:{actions:sendParent({type:'SUCCESS'}),guard:({event})=>event.value===42}}}}});
 const first={...(dynamic?{entry:assign({child:({spawn})=>spawn(child,{id:'x'})})}:{invoke:{src:child,id:'myChild'}}),on:{EVENT:{actions:forwardTo(dynamic?({context})=>context.child:'myChild')},SUCCESS:'last'}};
 const ref=createActor(createMachine({id:'parent',context:{child:undefined},initial:'first',states:{first,last:{type:'final'}}}));const done=new Promise((resolve,reject)=>ref.subscribe({complete:resolve,error:reject}));ref.start();ref.send({type:'EVENT',value:42});await done;expect(ref.getSnapshot().value).toEqual(native[`forward:${dynamic?'True':'False'}`]);ref.stop();
});
it.each([undefined,''])('rejects a falsey forwarding target at event resolution target=%s',target=>{
 const errors=[],ref=createActor(createMachine({on:{'*':{guard:()=>true,actions:forwardTo(target)}}}));ref.subscribe({error:e=>errors.push(e.message)});ref.start();ref.send({type:'TEST'});expect(errors).toEqual([native[target===undefined?'undefined':'empty']]);ref.stop();
});
it('retains event identity delay cancellation and pure send metadata',()=>{
 const delivered=[],inspected=[],clock=new SimulatedClock();const machine=createMachine({invoke:{id:'child',src:fromCallback(({receive})=>receive(e=>delivered.push(e)))},on:{GO:{actions:forwardTo('child',{id:'forwarded',delay:10})},CANCEL:{actions:cancel('forwarded')}}});
 const ref=createActor(machine,{clock,inspect:e=>{if(e.type==='@xstate.action'&&e.action.type==='xstate.sendTo')inspected.push(e.action.params);}}).start();const original={type:'GO',payload:{}};ref.send(original);expect(delivered).toEqual([]);clock.increment(9);expect(delivered).toEqual([]);clock.increment(1);expect(delivered.length).toBe(1);expect(delivered[0]).toBe(original);
 const send=inspected[0];expect(send.event).toBe(original);expect(send.to).toBe(ref.getSnapshot().children.child);ref.send(original);ref.send({type:'CANCEL'});clock.increment(10);expect(delivered.length).toBe(1);
 const [initial]=initialTransition(machine);const [,actions]=transition(machine,initial,original);expect(actions.length).toBe(1);const pure=actions[0];expect(pure.params.event).toBe(original);expect(pure.params.to.id).toBe('child');expect(pure.params.delay).toBe(10);expect(pure.params.id).toBe('forwarded');
 expect({delivered:delivered.length,delay:send.delay,id:send.id,sameEvent:delivered[0]===original,actionType:pure.type}).toEqual(native.delayed);ref.stop();
});
