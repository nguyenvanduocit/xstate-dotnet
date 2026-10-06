import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const source=part=>pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src',part)).href;
const {createMachine,createActor,fromTransition,getInitialSnapshot}=await import(source('index.ts'));
const {createTestModel,getAdjacencyMap}=await import(source('graph/index.ts'));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-function-json.json'),'utf8'));
it('omits functions from properties and dictionaries but keeps array slots as null',()=>{
 let calls=0;const fn=()=>++calls;
 const context={fn,boxed:fn,array:[fn,2,{fn,value:3}],map:{fn,value:10,null:null,nested:{fn,value:42}},untyped:{fn,value:11}};
 expect(JSON.parse(JSON.stringify(getInitialSnapshot(fromTransition(s=>s,context)))).context).toEqual(native.nested);
 expect(calls).toBe(0);expect(context.fn).toBe(fn);expect(context.array[0]).toBe(fn);expect(context.map.fn).toBe(fn);
 expect(JSON.parse(JSON.stringify({2:fn,3:'three'}))).toEqual(native.numeric);
});
it('uses the same omission rules for live and persisted contexts with actor references',()=>{
 const fn=()=>42;
 const machine=createMachine({context:({spawn})=>{const child=spawn('counter',{id:'child'});return {child,fn,array:[child,fn],map:{child,fn}};}},{actors:{counter:fromTransition(s=>s,0)}});
 const actor=createActor(machine).start();
 try{
  const saved=actor.getPersistedSnapshot();expect(JSON.parse(JSON.stringify(saved)).context).toEqual(native.child);expect(JSON.parse(JSON.stringify(actor.getSnapshot())).context).toEqual(native.child);
  const restored=createActor(machine,{snapshot:saved}).start();try{expect(restored.getSnapshot().context.fn).toBe(fn);}finally{restored.stop();}
 }finally{actor.stop();}
});
it('counts enumerable function properties before stringifying graph state keys and descriptions',()=>{
 const fn=()=>42;const machine=createMachine({context:{fn},initial:'a',states:{a:{on:{GO:'b'}},b:{}}});
 const model=createTestModel(machine,{events:[{type:'GO',fn}]});
 expect({description:model.getShortestPaths()[0].description,keys:Object.keys(getAdjacencyMap(machine))}).toEqual(native.graph);
});
it('rejects cyclic context objects during serialization',()=>{
 const cycle={};cycle.self=cycle;
 expect(()=>JSON.stringify(getInitialSnapshot(fromTransition(s=>s,cycle)))).toThrow(TypeError);
});
