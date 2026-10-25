'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const root = path.resolve(__dirname, '..');
const cfg = JSON.parse(fs.readFileSync(path.join(root, 'renovate.json'), 'utf8'));

assert.equal(cfg.timezone, 'Europe/Belgrade');
assert.deepEqual(cfg.schedule, ['* * * * 1'], 'updates must be scheduled on Monday');
assert.equal(cfg.prCreation, 'immediate', 'do not defer creation while awaiting checks');
assert.equal(cfg.automergeType, 'pr', 'automerge must retain CI-visible PRs');
assert.equal(cfg.platformAutomerge, false, 'use Renovate merging, not disabled GitHub native auto-merge');

const rules = cfg.packageRules ?? [];
const nonMajor = rules.find(rule => rule.groupSlug === 'weekly-non-major');
assert.ok(nonMajor, 'weekly non-major group is mandatory');
for (const kind of ['minor', 'patch', 'pin', 'digest']) {
    assert.ok(nonMajor.matchUpdateTypes.includes(kind), kind + ' updates must join the group');
}
assert.equal(typeof nonMajor.groupName, 'string');
assert.ok(nonMajor.groupName.length > 0);
assert.equal(nonMajor.groupSingleUpdates, true, 'even one available upgrade must keep the group');
assert.equal(nonMajor.minimumGroupSize, 1, 'do not wait for multiple updates');
assert.equal(nonMajor.automerge, true, 'non-major updates merge after successful checks');

const majors = rules.find(rule => rule.matchUpdateTypes?.includes('major'));
assert.ok(majors, 'major-upgrade policy is mandatory');
assert.equal(majors.automerge, false, 'major updates require manual review');

for (const removed of ['.github/dependabot.yml', '.github/workflows/codeql.yml']) {
    assert.equal(fs.existsSync(path.join(root, removed)), false, removed + ' is superseded');
}
console.log('Renovate policy verified: Monday groups, singleton PRs, safe automerge, no duplicate updaters or CodeQL workflow.');
