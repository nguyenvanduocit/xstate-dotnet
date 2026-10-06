import { test } from 'node:test';
import assert from 'node:assert/strict';
import { runExampleProcess } from './run-example-process.mjs';
test('waits for each prompt, including fragmented output, before sending the next response', async () => {
  const script = `const rl=require('readline').createInterface({input:process.stdin,output:process.stdout}); process.stdout.write('Fir'); setTimeout(()=>rl.question('st?',a=>setTimeout(()=>rl.question('Second?',b=>{console.log(a+':'+b);queueMicrotask(()=>rl.close());}),20)),20);`;
  const result = await runExampleProcess(process.execPath, ['-e', script], { dialogue: [{ prompt: 'First?', reply: 'Ada' }, { prompt: 'Second?', reply: '' }] });
  assert.equal(result.error, null); assert.equal(result.exitCode, 0); assert.equal(result.answered, 2);
  assert.equal(result.stdout, 'First?Second?Ada:\n');
});
test('timeout stops and observes process closure', async () => {
  const result = await runExampleProcess(process.execPath, ['-e', 'setInterval(()=>{},1000)'], { timeout: 150 });
  assert.equal(result.error, 'Process timeout'); assert.ok(result.exitCode !== 0 || result.signal !== null);
});
test('bounds captured output', async () => {
  const result = await runExampleProcess(process.execPath, ['-e', "process.stdout.write('x'.repeat(4096));setInterval(()=>{},1000)"], { maxBytes: 100 });
  assert.equal(result.error, 'Output limit exceeded'); assert.ok(result.stdout.length <= 100);
});
test('early exit does not pretend to have answered pending questions', async () => {
  const result = await runExampleProcess(process.execPath, ['-e', ''], { dialogue: [{ prompt: 'Missing?', reply: 'x' }] });
  assert.equal(result.exitCode, 0); assert.equal(result.answered, 0);
});
