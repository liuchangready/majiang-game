// Completion evidence for the active Codex Goal. This file event does not itself wake a chat.
const fs = require('node:fs');
const path = require('node:path');
const workspace = path.resolve(__dirname, '../..');

function publishReviewNotification({ task, run, provider, outcome, resultFile, deliveryFile }) {
  if (!/^[A-Z][A-Z0-9_-]*$/.test(task) || !/^[A-Z0-9_-]+-\d+$/.test(run)) throw new Error('Invalid review event identity');
  if (!['Anti', 'Claude/GLM'].includes(provider) || !['delivered', 'failed'].includes(outcome)) throw new Error('Invalid review event outcome');
  const directory = path.join(workspace, 'build/v4-migration/review-notifications');
  fs.mkdirSync(directory, { recursive: true });
  const target = path.join(directory, `${run}.json`);
  const event = { task, run, provider, outcome, state: 'pending_review', createdAt: new Date().toISOString(),
    resultFile, deliveryFile, accepted: false,
    nextAction: 'Confirm developer has exited, inspect result and delivery, run appropriate tests; fix or advance only after Codex review.' };
  const temp = `${target}.${process.pid}.tmp`;
  fs.writeFileSync(temp, JSON.stringify(event, null, 2));
  fs.renameSync(temp, target);
  return target;
}

module.exports = { publishReviewNotification };
