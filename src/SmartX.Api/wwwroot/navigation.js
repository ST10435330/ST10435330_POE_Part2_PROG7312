function navigate(view) {
    if (!['landing', 'dashboard', 'stream'].includes(view)) return;
    for (const id of ['landing', 'dashboard', 'stream']) document.getElementById(id).hidden = id !== view;
    for (const button of document.querySelectorAll('[data-view]')) button.setAttribute('aria-current', button.dataset.view === view ? 'page' : 'false');
    sessionStorage.setItem('smartx-view', view);
    window.dispatchEvent(new CustomEvent('smartx:navigate', {detail: view}));
}
document.getElementById('home').onclick = event => { event.preventDefault(); navigate('landing'); };
document.getElementById('enterStream').onclick = () => navigate('stream');
for (const button of document.querySelectorAll('[data-view]')) button.onclick = () => navigate(button.dataset.view);
window.navigate = navigate;
