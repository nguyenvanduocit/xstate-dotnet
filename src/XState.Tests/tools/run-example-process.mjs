import { spawn } from 'node:child_process';

// Send each response only after its prompt appears. Pre-buffering all stdin
// loses answers with Node readline.question when no question is pending yet.
export function runExampleProcess(command, args, { cwd, dialogue = [], timeout = 20000, maxBytes = 1024 * 1024 } = {}) {
  return new Promise(resolve => {
    const child = spawn(command, args, { cwd, windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] });
    let stdout = '', stderr = '', bytes = 0, answered = 0, cursor = 0, failure, executed = false;
    const stop = message => { failure ??= message; child.kill(); };
    const timer = setTimeout(() => stop('Process timeout'), timeout);
    child.on('spawn', () => { executed = true; });
    child.on('error', error => { failure ??= String(error); });
    child.stdin.on('error', error => stop('stdin: ' + error));
    child.stdout.setEncoding('utf8'); child.stderr.setEncoding('utf8');
    function collect(kind, chunk) {
      bytes += Buffer.byteLength(chunk);
      if (bytes > maxBytes) { stop('Output limit exceeded'); return; }
      if (kind === 'stderr') { stderr += chunk; return; }
      stdout += chunk;
      while (answered < dialogue.length) {
        const step = dialogue[answered];
        const found = stdout.indexOf(step.prompt, cursor);
        if (found < 0) break;
        cursor = found + step.prompt.length; answered++;
        child.stdin.write(step.reply + '\n');
      }
    }
    child.stdout.on('data', chunk => collect('stdout', chunk));
    child.stderr.on('data', chunk => collect('stderr', chunk));
    child.on('close', (exitCode, signal) => {
      clearTimeout(timer); child.stdin.destroy();
      resolve({ executed, exitCode, signal, stdout, stderr, answered, error: failure ?? null });
    });
  });
}
