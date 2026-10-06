import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-initial-actions.json'),'utf8'));
it('runs initial transition actions before target entries',()=>{
 const trace=[];const ref=createActor(createMachine({initial:{target:'a',actions:()=>trace.push('initialA')},states:{a:{entry:()=>trace.push('entryA')}}})).start();expect(trace).toEqual(native.initial);ref.stop();
});
it('runs entry before the nested initial transition and its target entry',()=>{
 const trace=[];const ref=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{entry:()=>trace.push('entryB'),initial:{target:'foo',actions:()=>trace.push('initialFoo')},states:{foo:{entry:()=>trace.push('entryFoo')}}}}})).start();ref.send({type:'NEXT'});expect(trace).toEqual(native.transition);ref.stop();
});
it('runs each nested initial action once when entering a compound target',()=>{
 const trace=[];const ref=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{initial:{target:'b_child',actions:()=>trace.push('initial in b')},states:{b_child:{initial:{target:'b_granchild',actions:()=>trace.push('initial in b_child')},states:{b_granchild:{}}}}}}})).start();ref.send({type:'NEXT'});expect(trace).toEqual(native.nestedTransition);ref.stop();
});
it('runs all initial actions down to the initial leaf',()=>{
 const trace=[];const ref=createActor(createMachine({initial:{target:'a',actions:()=>trace.push('root')},states:{a:{initial:{target:'a1',actions:()=>trace.push('inner')},states:{a1:{}}}}})).start();expect(trace).toEqual(native.nestedInitial);ref.stop();
});
it('runs root initial action once when reentering the root',()=>{
 let calls=0;const ref=createActor(createMachine({id:'root',initial:{target:'a',actions:()=>calls++},states:{a:{on:{NEXT:'b'}},b:{}},on:{REENTER:{target:'#root',reenter:true}}})).start();ref.send({type:'NEXT'});calls=0;ref.send({type:'REENTER'});expect({calls,state:ref.getSnapshot().value}).toEqual(native.reentry);ref.stop();
});
it('does not replay previous transition actions for an unhandled event',()=>{
 let calls=0;const counts=[];const ref=createActor(createMachine({initial:'idle',states:{idle:{on:{STOP:{target:'stop',actions:()=>calls++}}},stop:{}}})).start();ref.send({type:'STOP'});counts.push(calls);ref.send({type:'INVALID'});counts.push(calls);expect(counts).toEqual(native.invalid);ref.stop();
});
