import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createMachine,createActor,mapState,assign}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-map-state.json'),'utf8'));
const nested=()=>createMachine({context:{value:0},initial:'a',states:{a:{initial:'one',states:{one:{},two:{}}}}});
it('maps parallel leaves and shared ancestors in order while retaining original node identity',()=>{
 const machine=createMachine({type:'parallel',context:{val:100},states:{region1:{initial:'x',states:{x:{},y:{}}},region2:{initial:'p',states:{p:{},q:{}}}}});
 const snapshot=createActor(machine).getSnapshot();const results=mapState(snapshot,{map:()=> 'root',states:{region1:{map:()=> 'region1',states:{x:{map:()=> 'x'}}},region2:{map:()=> 'region2',states:{p:{map:()=> 'p'}}}}});
 for(const result of results)expect(result.stateNode).toBe(machine.getStateNodeById(result.stateNode.id));
 expect(results.map(r=>({id:r.stateNode.id,path:r.stateNode.path,result:r.result}))).toEqual(native.parallel);
});
it('rereads mutated mapper functions while retaining the original snapshot during actor sends',()=>{
 const machine=createMachine({context:{value:1},initial:'a',states:{a:{initial:'one',states:{one:{on:{NEXT:{target:'two',actions:assign({value:({context})=>context.value+1})}}},two:{}}}}});
 const actor=createActor(machine).start(),snapshot=actor.getSnapshot();const mapper={map:()=> 'root',states:{a:{map:()=> 'a',states:{one:{map:state=>{
  expect(state).toBe(snapshot);actor.send({type:'NEXT'});mapper.states.a.map=s=>'new-parent:'+s.context.value;mapper.map=s=>'new-root:'+s.context.value;return 'leaf:'+state.context.value;
 }}}}}};
 const results=mapState(snapshot,mapper);expect({values:results.map(r=>r.result),current:actor.getSnapshot().context.value,prior:snapshot.context.value}).toEqual(native.mutation);actor.stop();
});
it('preserves null result values and reaches root through missing mapper branches',()=>{
 const snapshot=createActor(nested()).getSnapshot();const results=mapState(snapshot,{map:()=>null,states:{a:null}});
 expect(results.map(r=>({id:r.stateNode.id,result:r.result}))).toEqual(native.sparse);expect(mapState(snapshot,{})).toEqual([]);
});
it('propagates the original mapper exception and stops before later ancestors',()=>{
 const snapshot=createActor(nested()).getSnapshot(),seen=[],error=Error('mapping failed');let caught;
 try{mapState(snapshot,{map:()=>{seen.push('root');return 'root';},states:{a:{map:()=>{seen.push('a');throw error;},states:{one:{map:()=> 'one'}}}}});}catch(e){caught=e;}
 expect(caught).toBe(error);expect(seen).toEqual(native.failure);
});
