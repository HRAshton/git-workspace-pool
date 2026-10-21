// ==UserScript==
// @name         Git Workspace Pool - Open PR Locally
// @namespace    https://github.com/HRAshton/git-workspace-pool
// @version      0.1.0
// @description  Open a GitHub pull request in its managed local VS Code worktree.
// @match        https://github.com/*/*
// @grant        none
// @run-at       document-idle
// ==/UserScript==

(() => {
    'use strict';

    const buttonId = 'git-workspace-pool-open-pr';

    function isPullRequestPage() {
        return /^\/[A-Za-z0-9_.-]+\/[A-Za-z0-9_.-]+\/pull\/[1-9][0-9]*(?:\/|$)/.test(
            window.location.pathname
        );
    }

    function updateButton() {
        let button = document.getElementById(buttonId);

        if (!isPullRequestPage()) {
            button?.remove();
            return;
        }

        if (button) {
            return;
        }

        button = document.createElement('button');
        button.id = buttonId;
        button.type = 'button';
        button.textContent = 'Open locally';
        button.setAttribute('aria-label', 'Open pull request in local VS Code');
        button.style.cssText = [
            'position:fixed',
            'right:20px',
            'bottom:20px',
            'z-index:2147483640',
            'padding:10px 16px',
            'border:1px solid #6e7681',
            'border-radius:8px',
            'background:#238636',
            'color:#fff',
            'font:600 14px system-ui',
            'cursor:pointer',
            'box-shadow:0 4px 12px #0004',
        ].join(';');

        button.addEventListener('click', () => {
            const url = 'prworkspace://open?url=' +
                encodeURIComponent(window.location.href) + '&v=1';
            window.location.assign(url);
        });

        document.body.appendChild(button);
    }

    let scheduled = false;

    const observer = new MutationObserver(() => {
        if (scheduled) {
            return;
        }

        scheduled = true;
        requestAnimationFrame(() => {
            scheduled = false;
            updateButton();
        });
    });

    observer.observe(document.documentElement, { childList: true, subtree: true });
    window.addEventListener('popstate', updateButton);
    document.addEventListener('turbo:load', updateButton);
    updateButton();
})();
