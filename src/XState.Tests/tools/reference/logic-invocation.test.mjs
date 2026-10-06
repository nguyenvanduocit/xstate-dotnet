import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,fromTransition,forwardTo,sendTo}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-logic-invocation.json'),'utf8'));
const key=v=>v?'True':'False';
it.each([[true,false],[false,false],[false,true]])('observes child counters custom=%s fifo=%s',async(custom,fifo)=>{
 const logic=custom?{transition:(s,e)=>e.type==='INC'?{...s,context:s.context+1}:e.type==='DEC'?{...s,context:s.context-1}:s,getInitialSnapshot:()=>({status:'active',output:undefined,error:undefined,context:0}),getPersistedSnapshot:s=>s}:fromTransition((count,event,{self})=>{if(event.type==='INC'){if(fifo)self.send({type:'DOUBLE'});return count+1;}if(!fifo&&event.type==='DEC')return count-1;if(fifo&&event.type==='DOUBLE')return count*2;return count;},0);
 const ref=createActor(createMachine({invoke:{id:'count',src:logic},on:{INC:{actions:forwardTo('count')}}}));const values=[];
 const done=new Promise(resolve=>ref.subscribe(s=>{const value=s.children.count.getSnapshot().context;values.push(value);if(value===2)resolve();}));ref.start();ref.send({type:'INC'});if(!fifo)ref.send({type:'INC'});await done;expect(values).toEqual(native[`counter:${key(custom)}:${key(fifo)}`]);ref.stop();
});
it('allows custom actor logic to send to its parent',async()=>{
 const logic={transition:(s,e,{self})=>{if(e.type==='PING')self._parent?.send({type:'PONG'});return s;},getInitialSnapshot:()=>({status:'active',output:undefined,error:undefined}),getPersistedSnapshot:s=>s};
 const ref=createActor(createMachine({initial:'waiting',states:{waiting:{entry:sendTo('ponger',{type:'PING'}),invoke:{id:'ponger',src:logic},on:{PONG:'success'}},success:{type:'final'}}}));const done=new Promise((resolve,reject)=>ref.subscribe({complete:resolve,error:reject}));ref.start();await done;expect(ref.getSnapshot().value).toEqual(native.parent);ref.stop();
});
it.each([false,true])('publishes invoked child snapshots machine=%s',async machineChild=>{
 const source=machineChild?createMachine({initial:'a',states:{a:{after:{10:'b'}},b:{}}}):fromTransition((_,e)=>e.value*2,0);const name=machineChild?'childMachine':'doublerLogic';const values=[];let receive;const done=new Promise(resolve=>{receive=resolve;});
 const ref=createActor(createMachine({invoke:{id:machineChild?undefined:'doubler',src:name,onSnapshot:{actions:({event})=>{const value=machineChild?event.snapshot.value:event.snapshot.context;values.push(value);if(value===(machineChild?'b':42))receive();}}},...(!machineChild?{entry:sendTo('doubler',{type:'update',value:21},{delay:10})}:{})},{actors:{[name]:source}})).start();await done;expect(values).toEqual(native[`snapshot:${key(machineChild)}`]);ref.stop();
});
