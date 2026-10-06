import {readFileSync} from 'node:fs';
import {resolve,dirname} from 'node:path';
import {fileURLToPath,pathToFileURL} from 'node:url';
const root=resolve(dirname(fileURLToPath(import.meta.url)),'../../../..');
const pin=JSON.parse(readFileSync(resolve(root,'src/XState/upstream.json'),'utf8'));
const {createActor,fromTransition}=await import(pathToFileURL(resolve(root,pin.sourceDirectory,'packages/core/src/index.ts')).href);
const native=JSON.parse(readFileSync(resolve(root,'tmp/xstate-parity/csharp-selection.json'),'utf8'));
const actor=value=>createActor(fromTransition((current,event)=>event.type==='SET'?event.value:current,value)).start();
const set=(ref,value)=>ref.send({type:'SET',value});
function unhandled(run){const pending=[];const spy=vi.spyOn(globalThis,'setTimeout').mockImplementation(fn=>{pending.push(fn);return 0;});try{return run(()=>pending.splice(0).map(fn=>{try{fn();}catch(error){return error;}throw Error('Expected unhandled failure');}));}finally{spy.mockRestore();}}
it('lazily evaluates selection and stores an independent previous value per subscriber',()=>{
 const ref=actor(0);let calls=0;const selection=ref.select(s=>{calls++;return s.context;});expect(calls).toBe(0);expect(selection.get()).toBe(0);const first=[],second=[];
 const a=selection.subscribe(v=>first.push(v));expect(first).toEqual([]);set(ref,1);const b=selection.subscribe(v=>second.push(v));set(ref,2);
 expect({first,second,calls}).toEqual(native.lazy);a.unsubscribe();b.unsubscribe();ref.stop();
});
it('matches Object.is for NaN signed zero infinities and reference objects',()=>{
 const ref=actor(NaN),values=[];const label=v=>Number.isNaN(v)?'NaN':Object.is(v,-0)?'-0':Object.is(v,0)?'+0':String(v);
 const sub=ref.select(s=>s.context).subscribe(v=>values.push(label(v)));
 for(const v of [NaN,0,-0,-0,0,Infinity,Infinity,NaN])set(ref,v);expect(values).toEqual(native.numbers);sub.unsubscribe();ref.stop();
 const original={x:1,y:2},objects=actor(original),seen=[];const other={x:1,y:2};const osub=objects.select(s=>s.context).subscribe(v=>seen.push(typeof v==='object'?'position':typeof v==='string'?'string':'number'));
 for(const value of [original,other,other,'a',['a'].join(''),1,1.0])set(objects,value);expect(seen).toEqual(native.objects);osub.unsubscribe();objects.stop();
});
it('keeps the last notified comparison value when equality suppresses changes',()=>{
 const ref=actor(0),pairs=[],values=[];const selection=ref.select(s=>s.context,(a,b)=>{pairs.push([a,b]);return b-a<3;});const sub=selection.subscribe(v=>values.push(v));
 for(const v of [1,2,3,4])set(ref,v);expect({pairs,values,current:selection.get()}).toEqual(native.previous);sub.unsubscribe();ref.stop();
});
it('propagates initial failures but reports later selector comparator and observer failures',()=>unhandled(drain=>{
 const ref=actor(0),initial=new Error('initial');const broken=ref.select(()=>{throw initial;});const subscribe=vi.spyOn(ref,'subscribe');expect(()=>broken.subscribe(()=>{})).toThrow(initial);expect(()=>broken.get()).toThrow(initial);expect(subscribe).not.toHaveBeenCalled();subscribe.mockRestore();
 const seen=[],comparisons=[];const sub=ref.select(s=>{if(s.context===2)throw Error('selector');return s.context;},(a,b)=>{comparisons.push([a,b]);if(b===3)throw Error('comparator');return a===b;}).subscribe(v=>{seen.push(v);if(v===1)throw Error('callback');});
 for(const v of [1,1,2,3,4])set(ref,v);const errors=drain().map(e=>e.message);expect({seen,comparisons,errors}).toEqual(native.failures);expect(ref.getSnapshot().status).toBe('active');sub.unsubscribe();ref.stop();
}));
it('ignores selected observer error and completion callbacks including late subscription',()=>unhandled(drain=>{
 const fault=Error('actor failed');const ref=createActor(fromTransition((v,e)=>{if(e.type==='FAIL')throw fault;return v;},0)).start();let errors=0,completions=0;const observer={next:()=>{},error:()=>errors++,complete:()=>completions++};
 const sub=ref.select(s=>s.context).subscribe(observer);ref.send({type:'FAIL'});const failures=drain();const late=ref.select(s=>s.context).subscribe(observer);failures.push(...drain());
 const stopped=actor(0),stopping=stopped.select(s=>s.context).subscribe(observer);stopped.stop();expect(failures.every(e=>e===fault)).toBe(true);expect({errors,completions,reported:failures.length}).toEqual(native.termination);
 sub.unsubscribe();late.unsubscribe();stopping.unsubscribe();
}));
it('preserves notification order with reentrant sends and removed or added subscriptions',()=>{
 const ref=actor(0),selection=ref.select(s=>s.context),trace=[];let second,added;
 const first=selection.subscribe(v=>{trace.push('first:'+v);if(v!==1)return;second.unsubscribe();added=selection.subscribe(n=>trace.push('added:'+n));set(ref,2);});second=selection.subscribe(v=>trace.push('second:'+v));set(ref,1);
 expect(trace).toEqual(native.reentrant);first.unsubscribe();second.unsubscribe();added.unsubscribe();ref.stop();
});
