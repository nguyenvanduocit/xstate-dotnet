import { readFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath, pathToFileURL } from 'node:url';
const root = resolve(dirname(fileURLToPath(import.meta.url)), '../../../..');
const pin = JSON.parse(readFileSync(resolve(root, 'src/XState/upstream.json'), 'utf8'));
const {createMachine, createActor, fromCallback, assign} = await import(pathToFileURL(resolve(root, pin.sourceDirectory, 'packages/core/src/index.ts')).href);
const native = JSON.parse(readFileSync(resolve(root, 'tmp/xstate-parity/csharp-history-transitions.json'), 'utf8'));
const snapshot = value => ({ value: value.value, history: Object.fromEntries(Object.entries(value.historyValue).map(([key,nodes]) => [key,nodes.map(node=>node.id)])) });
const boolean = value => value ? 'True' : 'False';
it('reenters the persisted configuration and repeats exit/entry in order', () => {
  const actions=[];
  const machine=createMachine({initial:'a',states:{a:{on:{REENTER:{target:'#b_hist',reenter:true}},initial:'a1',states:{a1:{on:{NEXT:'a2'}},a2:{entry:()=>actions.push('a2 entered'),exit:()=>actions.push('a2 exited')},a3:{type:'history',id:'b_hist'}}}}});
  const actor=createActor(machine).start();try{actor.send({type:'NEXT'});actions.length=0;actor.send({type:'REENTER'});
    expect(actions).toEqual(['a2 exited','a2 entered']); expect({snapshot:snapshot(actor.getSnapshot()),actions}).toEqual(native.reenter);
  }finally{actor.stop();}
});
for(const visited of [false,true]) for(const kind of [0,1,2]) {
  it(`history initial/entry actions kind=${kind}, visited=${visited}`,()=>{
    let calls=0;const spy=()=>calls++;
    const children={b1:{},b2:{id:'hist',type:'history',...(kind===0?{}:{target:'b3'})},...(kind===0?{}:{b3:{}})};
    const b={initial:kind===2?'b1':{target:'b1',actions:spy},...(kind===2?{entry:spy}:{}),states:children,...(visited?{on:{NEXT:'a'}}:{})};
    const actor=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'#hist'}},b}})).start();
    try{actor.send({type:'NEXT'});if(visited){calls=0;actor.send({type:'NEXT'});actor.send({type:'NEXT'});}
      expect(calls).toBe(kind===2||(kind===0&&!visited)?1:0);
      expect({calls,snapshot:snapshot(actor.getSnapshot())}).toEqual(native[`historyActions:${kind}:${boolean(visited)}`]);
    }finally{actor.stop();}
  });
}
it('executes initial actions when the initial target is itself a history state',()=>{
  let calls=0;const actor=createActor(createMachine({initial:'a',states:{a:{on:{NEXT:'b'}},b:{initial:{target:'b1',actions:()=>calls++},states:{b1:{id:'hist',type:'history',target:'b2'},b2:{}}}}})).start();
  try{actor.send({type:'NEXT'});expect(calls).toBe(1);expect({calls,snapshot:snapshot(actor.getSnapshot())}).toEqual(native.initialHistory);}finally{actor.stop();}
});
it('restarts an invoked actor through history',()=>{
  let calls=0;const actor=createActor(createMachine({initial:'running',states:{running:{on:{PING:'refresh'},invoke:{src:fromCallback(()=>{calls++;})}},refresh:{type:'history'}}})).start();
  try{calls=0;actor.send({type:'PING'});expect(calls).toBe(1);expect({calls,snapshot:snapshot(actor.getSnapshot())}).toEqual(native.restartInvocation);}finally{actor.stop();}
});
it('revives an in-memory snapshot retaining instantiated StateNode history',()=>{
  const machine=createMachine({initial:'on',states:{on:{initial:'first',states:{first:{on:{SWITCH:'second'}},second:{},hist:{type:'history'}},on:{POWER:'off'}},off:{on:{POWER:'on.hist'}}}});
  const source=createActor(machine).start();source.send({type:'SWITCH'});source.send({type:'POWER'});const saved=source.getSnapshot();source.stop();
  expect(saved.value).toBe('off');const node=saved.historyValue['(machine).on.hist'][0];expect(node).toBe(machine.states.on.states.second);
  const actor=createActor(machine,{snapshot:saved}).start();try{actor.send({type:'POWER'});expect(actor.getSnapshot().value).toEqual({on:'second'});
    expect({before:snapshot(saved),after:snapshot(actor.getSnapshot()),sameNode:node===actor.getSnapshot().historyValue['(machine).on.hist'][0]}).toEqual(native.revive);
  }finally{actor.stop();}
});
for(const parent of [false,true]) for(const array of [false,true]) {
  it(`targetless action parent=${parent}, conditionalArray=${array}`,()=>{
    let calls=0;const eventType=array?'TARGETLESS_ARRAY':'TARGETLESS_OBJECT';const transition={actions:[()=>calls++]};const on={[eventType]:array?[transition]:transition};
    const actor=createActor(createMachine({initial:'foo',...(parent?{on}:{}),states:{foo:parent?{}:{on}}})).start();
    try{actor.send({type:eventType});expect(calls).toBe(1);expect({calls,snapshot:snapshot(actor.getSnapshot())}).toEqual(native[`targetless:${boolean(parent)}:${boolean(array)}`]);}finally{actor.stop();}
  });
}
it('retains the child during a parent targetless transition',()=>{
  const actor=createActor(createMachine({initial:'foo',on:{PARENT_EVENT:{actions:()=>{}}},states:{foo:{}}})).start();
  try{actor.send({type:'PARENT_EVENT'});expect(actor.getSnapshot().value).toBe('foo');expect(snapshot(actor.getSnapshot())).toEqual(native.keepChild);}finally{actor.stop();}
});
for(const exit of [false,true]) {
  it(`internal transition repeats only proper descendant ${exit?'exits':'entries'}`,()=>{
    const fields=['source','direct','deep'];
    const node=depth=>({[exit?'exit':'entry']:assign({[fields[depth]]:({context})=>context[fields[depth]]+1}),
      ...(depth<2?{initial:depth===0?'a11':'a111',states:{[depth===0?'a11':'a111']:node(depth+1)}}:{}),...(depth===0?{on:{REENTER:'.a11.a111'}}:{})});
    const actor=createActor(createMachine({initial:'a1',context:{source:0,direct:0,deep:0},states:{a1:node(0)}})).start();
    try{actor.send({type:'REENTER'});const context=actor.getSnapshot().context;expect(context).toEqual(exit?{source:0,direct:1,deep:1}:{source:1,direct:2,deep:2});
      expect({...context,snapshot:snapshot(actor.getSnapshot())}).toEqual(native[exit?'descendantExits':'descendantEntries']);
    }finally{actor.stop();}
  });
}
