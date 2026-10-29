'use strict';
const readline = require('node:readline');
if (process.argv.includes('--version')) { console.log('codex-cli fixture'); process.exit(0); }
const mode = process.argv[2];
let initialized = false, pending;
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
readline.createInterface({ input: process.stdin }).on('line', line => {
  const message = JSON.parse(line);
  if (message.id === 'approval' && message.error?.code === -32601) { send({ id: pending, result: { rateLimits: { primary: { usedPercent: 28, windowDurationMins: 300 } } } }); return; }
  if (message.method === 'initialize') {
    if (mode === 'hang') return;
    send({ id: message.id, result: {} });
  } else if (message.method === 'initialized') initialized = !Object.hasOwn(message, 'params');
  else if (message.method === 'account/rateLimits/read' && initialized) {
    if (mode === 'error') { send({ id: message.id, error: { message: 'PRIVATE_FIXTURE_TOKEN' } }); return; }
    pending = message.id;
    send({ id: 'approval', method: 'item/commandExecution/requestApproval', params: {} });
  } else { send({ id: message.id, error: { code: -32601 } }); }
});
