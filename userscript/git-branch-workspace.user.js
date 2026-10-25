// ==UserScript==
// @name         Git Workspace Pool - Open Git Branch
// @namespace    https://github.com/HRAshton/git-workspace-pool
// @version      0.2.0
// @description  Open a configured Git repository and branch locally, without a PR or API.
// @match        https://bitbucket.example.com/*
// @grant        none
// @run-at       document-idle
// ==/UserScript==

(() => {
    'use strict';

    // Replace @match with your own Bitbucket host. This intentionally uses no
    // provider API or unstable DOM selectors. Your own branch picker can call
    // openBranch(repo, branch) directly, using short names such as feature/task.
    function openBranch(repo, branch) {
        const target = new URL('prworkspace://open/');
        target.search = 'repo=' + encodeURIComponent(repo) +
            '&branch=' + encodeURIComponent(branch);
        window.location.assign(target.href);
    }

    const buttonId = 'git-workspace-pool-open-branch';
    if (document.getElementById(buttonId)) {
        return;
    }
    const button = document.createElement('button');
    button.id = buttonId;
    button.type = 'button';
    button.textContent = 'Open branch locally';
    button.style.cssText = 'position:fixed;right:20px;bottom:20px;z-index:2147483640;' +
        'padding:10px 16px;border:1px solid #6e7681;border-radius:8px;' +
        'background:#238636;color:#fff;font:600 14px system-ui;cursor:pointer';
    button.addEventListener('click', () => {
        const repo = window.prompt('Git clone URL (HTTPS or SSH; unknown repositories open registration):');
        if (!repo?.trim()) {
            return;
        }
        const branch = window.prompt('Source branch (short name, for example feature/my-task):');
        if (branch?.trim()) {
            openBranch(repo.trim(), branch.trim());
        }
    });
    document.body.appendChild(button);
})();
