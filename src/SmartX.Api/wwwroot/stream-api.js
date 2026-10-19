export async function request(path, body) {
    const response = await fetch('/api' + path, body === undefined ? {} : {
        method: 'POST', headers: {'Content-Type': 'application/json'}, body: JSON.stringify(body)
    });
    const text = await response.text();
    let data;
    try { data = text ? JSON.parse(text) : null; } catch { throw Error('The API returned an unexpected response.'); }
    if (!response.ok) throw Error(data?.error || data?.title || `Request failed (${response.status}).`);
    return data;
}
export const byId = id => document.getElementById(id);
export function element(tag, text, className) {
    const node = document.createElement(tag); node.textContent = text;
    if (className) node.className = className;
    return node;
}
export function notify(text, error = false) {
    byId('streamMessage').textContent = text;
    byId('streamMessage').className = error ? 'error' : '';
}
export function onClick(id, action) {
    byId(id).addEventListener('click', async () => {
        const button = byId(id); button.disabled = true;
        try { await action(); } catch (error) { notify(error.message, true); }
        finally { button.disabled = false; }
    });
}
export function onForm(id, action) {
    byId(id).addEventListener('submit', async event => {
        event.preventDefault();
        const button = event.target.querySelector('button'); button.disabled = true;
        try { await action(); } catch (error) { notify(error.message, true); }
        finally { button.disabled = false; }
    });
}
