'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const source = fs.readFileSync(__dirname + '/git-branch-workspace.user.js', 'utf8');

function fixture(answers) {
    let click;
    let button;
    const navigated = [];
    const context = {
        URL,
        window: {
            prompt: () => answers.shift(),
            location: { assign: value => navigated.push(value) },
        },
        document: {
            getElementById: () => button,
            createElement: () => ({ style: {}, addEventListener: (event, handler) => { click = handler; } }),
            body: { appendChild: value => { button = value; } },
        },
    };
    vm.runInNewContext(source, context);
    click();
    return navigated;
}

const repo = 'https://bitbucket.company.test:8443/stash/scm/team/repo.git';
for (const branch of ['feature/task', 'feature/a+b', 'feature/a&b', 'feature/日本語', 'feature/%20']) {
    const links = fixture([repo, branch]);
    assert.equal(links.length, 1);
    const link = new URL(links[0]);
    assert.equal(link.protocol, 'prworkspace:');
    assert.equal(link.hostname, 'open');
    assert.equal(link.searchParams.get('repo'), repo);
    assert.equal(link.searchParams.get('branch'), branch);
    assert.deepEqual([...link.searchParams.keys()], ['repo', 'branch']);
}
assert.deepEqual(fixture([null]), []);
assert.deepEqual(fixture([repo, null]), []);
assert.deepEqual(fixture([repo, '   ']), []);
console.log('Git branch userscript: encoding and cancellation checks passed.');
