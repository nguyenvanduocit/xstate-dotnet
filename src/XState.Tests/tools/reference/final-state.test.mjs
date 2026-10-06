import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,createMachine,assign,sendParent}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-final-state.json'),'utf8'));
const final=()=>({type:'final'});
const compound=(name,child)=>({initial:name,states:{[name]:child}});
const parallel=(name,child)=>({type:'parallel',states:{[name]:child}});
it('uses the completed root or parallel ancestor event for each output mapper',()=>{
 const roots=[final(),parallel('a',final()),parallel('a',compound('b',final())),parallel('a',parallel('b',compound('c',final())))];
 for(let depth=0;depth<roots.length;depth++){
  const events=[];const ref=createActor(createMachine({...roots[depth],output:({event})=>{events.push(event);}}),depth===0?{input:42}:undefined).start();
  expect(events).toEqual([{type:'xstate.done.state.(machine)',output:undefined}]);expect(events.map(e=>e.type)).toEqual(native['root'+depth]);ref.stop();
 }
 const events=[];const ref=createActor(createMachine(compound('a',{...parallel('b',parallel('c',compound('d',final()))),onDone:{actions:({event})=>events.push(event)}}))).start();
 expect(events).toEqual([{type:'xstate.done.state.(machine).a',output:undefined}]);expect(events.map(e=>e.type)).toEqual(native.outerDone);ref.stop();
});
it('runs nested final entry actions before ancestor onDone and resolves output after entry assign',()=>{
 const actual=[];const ref=createActor(createMachine({initial:'foo',states:{foo:{initial:'bar',onDone:{actions:()=>actual.push('fooAction')},states:{bar:{initial:'baz',onDone:'barFinal',states:{baz:{type:'final',entry:()=>actual.push('bazAction')}}},barFinal:{type:'final',entry:()=>actual.push('barAction')}}}}})).start();
 expect(actual).toEqual(native.childActions);ref.stop();
 const values=[];const assigned=createActor(createMachine({context:{count:0},initial:'a',states:{a:{initial:'a1',states:{a1:{on:{NEXT:'a2'}},a2:{type:'final',entry:assign({count:1}),output:({context})=>context.count}},onDone:{actions:({event})=>values.push(event.output)}}}})).start();
 assigned.send({type:'NEXT'});expect(values).toEqual(native.assignedOutput);assigned.stop();
});
it('suppresses done actions after root completion and emits each parallel completion only once',()=>{
 let count=0;const done={actions:()=>count++};const stopped=createActor(createMachine({...parallel('a',{...parallel('b',{...compound('c',final()),onDone:done}),onDone:done}),onDone:done})).start();
 expect(count).toBe(native.suppressedDone);stopped.stop();
 count=0;const completed=createActor(createMachine({type:'parallel',states:{a:final(),b:final()},output:()=>count++})).start();expect(count).toBe(native.multipleRoot);completed.stop();
 count=0;const active=createActor(createMachine(compound('a',{type:'parallel',states:{b:final(),c:final()},onDone:done}))).start();expect(count).toBe(native.multipleDone);active.stop();
 count=0;const ignored=createActor(createMachine(compound('A',{type:'parallel',states:{B:{type:'final',output:()=>count++},C:compound('C1',{})}}))).start();expect(count).toBe(native.ignoredOutput);ignored.stop();
});
it('delivers final child events before done.actor without repeating child exits',()=>{
 for(const mode of ['exitCount','outgoingEntry','outgoingExit']){
  let exits=0;const outgoing=sendParent({type:'CHILD_CANCELED'});
  const child=createMachine({initial:'start',exit:mode==='exitCount'?()=>exits++:mode==='outgoingExit'?outgoing:undefined,states:{start:{on:{CANCEL:'canceled'}},canceled:{type:'final',entry:mode==='outgoingExit'?undefined:outgoing}}});
  const ref=createActor(createMachine({initial:'start',states:{start:{invoke:{id:'child',src:child,onDone:'completed'},on:{CHILD_CANCELED:'canceled'}},canceled:{},completed:{}}})).start();
  ref.getSnapshot().children.child.send({type:'CANCEL'});expect(mode==='exitCount'?exits:ref.getSnapshot().value).toEqual(native[mode]);ref.stop();
 }
});
it('preserves absent versus null output across live JSON persisted JSON and restore',()=>{
 for(const expected of native.persistence){
  const machine=createMachine({context:{},initial:'start',states:{start:{on:{NEXT:'end'}},end:final()},...(expected.configured?{output:null}:{})});
  const ref=createActor(machine).start();ref.send({type:'NEXT'});const plain=value=>JSON.parse(JSON.stringify(value));
  expect(plain(ref.getSnapshot())).toEqual(expected.live);expect(plain(ref.getPersistedSnapshot())).toEqual(expected.persisted);
  const restored=createActor(machine,{snapshot:expected.persisted}).start();expect(plain(restored.getSnapshot())).toEqual(expected.restored);ref.stop();restored.stop();
 }
});
it('evaluates child output twice with entry context then resolves root output before exit assign',()=>{
 const trace=[],refs=[];const machine=createMachine({context:{count:0},initial:'start',output:({context,event,self})=>{refs.push(self);trace.push(`root:${context.count}:${event.type}:${event.output}`);return event.output;},exit:assign({count:2}),states:{start:{on:{FINISH:'end'}},end:{type:'final',entry:assign({count:1}),output:({context,event,self})=>{refs.push(self);trace.push(`child:${context.count}:${event.type}`);return context.count;}}}});
 const ref=createActor(machine).start();ref.send({type:'FINISH'});expect(refs.every(self=>self===ref)).toBe(true);expect({trace,context:ref.getSnapshot().context.count,output:ref.getSnapshot().output}).toEqual(native.evaluation);ref.stop();
});
it('preserves the original output exception in actor errors for initialization and transitions',()=>{
 const observations=[];for(const initial of [false,true]){
  const error=Error('output failure'),errors=[];const config=initial?{type:'final'}:{initial:'start',states:{start:{on:{FINISH:'end'}},end:final()}};
  const ref=createActor(createMachine({...config,output:()=>{throw error;}}));ref.subscribe({error:e=>errors.push(e)});ref.start();if(!initial)ref.send({type:'FINISH'});
  expect(ref.getSnapshot().error).toBe(error);expect(errors).toEqual([error]);observations.push({initial,errors:errors.map(e=>e.message),status:ref.getSnapshot().status});ref.stop();
 }expect(observations).toEqual(native.errors);
});
