const { test } = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

test('Schedule worker directory is registered before endpoint binding', () => {
  const root = path.resolve(__dirname, '../..');
  const program = fs.readFileSync(path.join(root, 'Aegis.Studio.Web/Program.cs'), 'utf8');
  const endpoints = fs.readFileSync(path.join(root, 'Aegis.Studio.Web/Scheduling/ScheduleEndpoints.cs'), 'utf8');
  assert.match(endpoints, /WorkerDirectory directory\)/);
  const registration = 'builder.Services.AddSingleton<Aegis.Studio.Web.Workers.WorkerDirectory>();';
  assert.equal(program.split(registration).length - 1, 1);
  assert.ok(program.indexOf(registration) < program.indexOf('builder.Build()'));
  assert.match(program, /builder\.Services\.AddSingleton\(services =>\s*new StudioDatabase\(/);
});
