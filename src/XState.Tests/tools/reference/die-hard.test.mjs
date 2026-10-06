import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const source=part=>pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src',part)).href;
const {createMachine,assign}=await import(source('index.ts'));
const {createTestModel}=await import(source('graph/index.ts'));
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-die-hard.json'),'utf8'));
function model(){
 return createTestModel(createMachine({id:'dieHard',initial:'pending',context:{three:0,five:0},states:{pending:{always:{target:'success',guard:'weHave4Gallons'},on:{
  POUR_3_TO_5:{actions:assign(({context})=>{const poured=Math.min(5-context.five,context.three);return {three:context.three-poured,five:context.five+poured};})},
  POUR_5_TO_3:{actions:assign(({context})=>{const poured=Math.min(3-context.three,context.five);return {three:context.three+poured,five:context.five-poured};})},
  FILL_3:{actions:assign({three:3})},FILL_5:{actions:assign({five:5})},EMPTY_3:{actions:assign({three:0})},EMPTY_5:{actions:assign({five:0})}
 }},success:{type:'final'}}},{guards:{weHave4Gallons:({context})=>context.five===4}}));
}
const sequence=['FILL_5','POUR_5_TO_3','EMPTY_3','POUR_5_TO_3','FILL_5','POUR_5_TO_3'].map(type=>({type}));
const summarize=paths=>paths.map(p=>({value:p.state.value,context:p.state.context,weight:p.weight,description:p.description,steps:p.steps.map(s=>({value:s.state.value,context:s.state.context,event:s.event.type}))}));
for(const kind of ['shortest','simple','emptyThree','sequence']){
 it(`matches every ${kind} jug path including intermediate contexts and ordered events`,()=>{
  const m=model();const options={toState:s=>s.matches('success')&&(kind!=='emptyThree'||s.context.three===0)};
  const paths=kind==='shortest'?m.getShortestPaths(options):kind==='sequence'?m.getPathsFromEvents(sequence,options):m.getSimplePaths(options);
  expect(summarize(paths)).toEqual(native[kind]);
 });
}
it('matches the complete upstream failed-state path message',async()=>{
 const m=createTestModel(createMachine({initial:'first',states:{first:{on:{NEXT_1:'second'}},second:{on:{NEXT_2:'third'}},third:{}}}));
 const path=m.getShortestPaths({toState:s=>s.matches('third')})[0];const original=new Error('test error');let caught;
 try{await m.testPath(path,{states:{third:()=>{throw original;}}});}catch(error){caught=error;}
 expect(caught).toBe(original);expect(caught.message).toBe(native.error);
});
