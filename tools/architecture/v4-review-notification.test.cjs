const fs = require('node:fs');
const assert = require('node:assert/strict');
const { publishReviewNotification } = require('./v4-review-notification.cjs');
const base = { task: 'NOTIFICATION_SELFTEST', run: `NOTIFICATION_SELFTEST-${Date.now()}`,
  provider: 'Anti', outcome: 'delivered', resultFile: 'self-test', deliveryFile: 'self-test' };
const file = publishReviewNotification(base);
let event = JSON.parse(fs.readFileSync(file, 'utf8'));
assert.equal(event.state, 'pending_review');
assert.equal(event.accepted, false);
publishReviewNotification({ ...base, outcome: 'failed' });
event = JSON.parse(fs.readFileSync(file, 'utf8'));
assert.equal(event.outcome, 'failed');
assert.equal(event.accepted, false);
assert.throws(() => publishReviewNotification({ ...base, run: '../invalid' }));
assert.throws(() => publishReviewNotification({ ...base, outcome: 'accepted' }));
fs.writeFileSync(file, JSON.stringify({ ...event, state: 'self_test_complete' }, null, 2));
console.log('Review notification self-test passed');
