import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {assign,createActor,createMachine,raise,spawnChild}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-input-factory.json'),'utf8'));
it('starts a machine without declaring or passing input',()=>{const ref=createActor(createMachine({context:{count:42}})).start();try{expect(ref.getSnapshot().context.count).toEqual(native.noInput);}finally{ref.stop();}});
for(const dynamic of [false,true])it(`provides ${dynamic?'computed':'static'} input to a named actor`,()=>{
 const inputs=[];const child=createMachine({context:({input})=>{inputs.push(input);return {};}});
 const config=dynamic?{context:({input})=>({count:input}),invoke:{src:'child',input:({context})=>context.count+100}}:{invoke:{src:'child',input:42}};
 const ref=createActor(createMachine(config,{actors:{child}}),dynamic?{input:42}:{}).start();try{expect(inputs).toEqual(native[dynamic?'dynamic':'static']);}finally{ref.stop();}
});
for(const spawn of [false,true])it(`passes parent self to ${spawn?'spawn':'invoke'} input factory`,()=>{
 const seen=[];const input=({self})=>{seen.push(self);};const child=createMachine({});const config=spawn?{entry:spawnChild('child',{input})}:{invoke:{src:child,input}};
 const ref=createActor(createMachine(config,{actors:{child}})).start();try{expect({calls:seen.length,sameSelf:seen[0]===ref}).toEqual(native[spawn?'spawnSelf':'invokeSelf']);}finally{ref.stop();}
});
