import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {assign,createActor,createMachine,enqueueActions,raise,sendTo,emit,fromCallback,spawnChild,stopChild}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-action-diagnostics.json'),'utf8'));
it('warns for builtin action creators called inside a custom action',()=>{const warnings=[];const warn=vi.spyOn(console,'warn').mockImplementation(value=>warnings.push(value));const ref=createActor(createMachine({entry:()=>{assign({});raise({type:''});sendTo('',{type:''});emit({type:''});}}));try{ref.start();expect(warnings).toEqual(native.builtins);}finally{ref.stop();warn.mockRestore();}});
it('restores warning scope across nested execution and an exception',()=>{const warnings=[];let current='outer';const warn=vi.spyOn(console,'warn').mockImplementation(value=>warnings.push(current+':'+value));const inner=createActor(createMachine({entry:()=>{emit({type:'x'});throw new Error('inner');}}));inner.subscribe({error:()=>{}});const outer=createActor(createMachine({entry:()=>{assign({});current='inner';inner.start();current='outer';raise({type:'x'});}}));try{outer.start();inner.stop();outer.stop();assign({});expect(warnings).toEqual(native.scope);}finally{inner.stop();outer.stop();warn.mockRestore();}});
