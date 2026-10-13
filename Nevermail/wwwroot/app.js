// Nevermail web UI. Plain ES module, no build step, no dependencies.

const PROVIDERS = { sendgrid: 'SendGrid', brevo: 'Brevo', mandrill: 'Mandrill', smtp: 'SMTP' };
const PREVIEW_WIDTHS = { desktop: '100%', tablet: '768px', mobile: '375px' };
const SEEN_KEY = 'nevermail.seen';
const MAX_SEEN = 2000;

const icons = {
    paperclip: 'M16.5 6.5v9a4.5 4.5 0 0 1-9 0V6a3 3 0 0 1 6 0v9a1.5 1.5 0 0 1-3 0V7h-1.5v8a3 3 0 0 0 6 0V6a4.5 4.5 0 0 0-9 0v9.5a6 6 0 0 0 12 0v-9h-1.5Z',
    trash: 'M9 3h6l1 2h4v2H4V5h4l1-2Zm-3 6h12l-1 11a2 2 0 0 1-2 2H9a2 2 0 0 1-2-2L6 9Zm4 2v8h1.5v-8H10Zm2.5 0v8H14v-8h-1.5Z',
    download: 'M11 4h2v8.6l3.3-3.3 1.4 1.4L12 16.4l-5.7-5.7 1.4-1.4 3.3 3.3V4ZM5 18h14v2H5v-2Z',
    back: 'M10.8 5.3 4.1 12l6.7 6.7 1.4-1.4L7.9 13H20v-2H7.9l4.3-4.3-1.4-1.4Z',
    inbox: 'M5 4h14l2 9v5a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2v-5l2-9Zm1.6 2-1.5 7H9a3 3 0 0 0 6 0h3.9l-1.5-7H6.6Z',
    copy: 'M8 3h10a2 2 0 0 1 2 2v12h-2V5H8V3Zm-3 4h10a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H5a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Zm0 2v10h10V9H5Z',
    link: 'M10.6 13.4a1 1 0 0 1 0-1.4l3.5-3.5a1 1 0 1 1 1.4 1.4L12 13.4a1 1 0 0 1-1.4 0ZM7.8 18.4a3.2 3.2 0 0 1-2.2-5.5l2.5-2.5 1.4 1.4-2.5 2.5a1.2 1.2 0 0 0 1.7 1.7l2.5-2.5 1.4 1.4-2.5 2.5a3.2 3.2 0 0 1-2.3 1Zm7.5-4.4-1.4-1.4 2.5-2.5a1.2 1.2 0 0 0-1.7-1.7l-2.5 2.5-1.4-1.4 2.5-2.5a3.2 3.2 0 0 1 4.5 4.5L15.3 14Z',
};

const state = {
    mails: [],
    selectedId: null,
    detail: null,
    related: [],
    search: '',
    provider: '',
    requestId: null,
    tab: null,
    // Tab the user picked explicitly; kept when moving between mails if the next mail has it.
    preferredTab: null,
    previewWidth: 'desktop',
    info: null,
    seen: loadSeen(),
    connected: false,
};

const $ = (selector) => document.querySelector(selector);
const listEl = $('#list');
const detailEl = $('#detail');

// ---------------------------------------------------------------------------------------------
// DOM helpers

/** h('div', { class: 'x', onclick: fn }, 'text', child, [more]) — text is always inserted as text. */
function h(tag, props, ...children) {
    const el = document.createElement(tag);
    for (const [key, value] of Object.entries(props ?? {})) {
        if (value === null || value === undefined || value === false) continue;
        if (key.startsWith('on')) el.addEventListener(key.slice(2), value);
        else if (key === 'class') el.className = value;
        else if (key === 'dataset') Object.assign(el.dataset, value);
        else if (key === 'style') Object.assign(el.style, value);
        else if (key in el && typeof value !== 'string') el[key] = value;
        else el.setAttribute(key, value === true ? '' : value);
    }
    append(el, children);
    return el;
}

function append(el, children) {
    for (const child of children.flat(Infinity)) {
        if (child === null || child === undefined || child === false) continue;
        el.append(child instanceof Node ? child : document.createTextNode(String(child)));
    }
}

function icon(name) {
    const svg = document.createElementNS('http://www.w3.org/2000/svg', 'svg');
    svg.setAttribute('viewBox', '0 0 24 24');
    svg.setAttribute('aria-hidden', 'true');
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.setAttribute('d', icons[name]);
    svg.append(path);
    return svg;
}

let toastTimer;
function toast(message) {
    const el = $('#toast');
    el.textContent = message;
    el.hidden = false;
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => (el.hidden = true), 2200);
}

async function copy(text) {
    try {
        await navigator.clipboard.writeText(text);
        toast('Copied');
    } catch {
        toast('Copy failed: clipboard not available');
    }
}

// ---------------------------------------------------------------------------------------------
// Formatting

const providerName = (key) => PROVIDERS[key] ?? key;
const isMobile = () => matchMedia('(max-width: 800px)').matches;

function formatListTime(iso) {
    const date = new Date(iso);
    const now = new Date();
    if (date.toDateString() === now.toDateString()) {
        return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
    }
    const sameYear = date.getFullYear() === now.getFullYear();
    return date.toLocaleDateString([], sameYear ? { month: 'short', day: 'numeric' } : { year: 'numeric', month: 'short', day: 'numeric' });
}

const relativeFormat = new Intl.RelativeTimeFormat(undefined, { numeric: 'auto' });
function formatRelative(iso) {
    const seconds = Math.round((new Date(iso) - Date.now()) / 1000);
    const units = [['year', 31536000], ['month', 2592000], ['day', 86400], ['hour', 3600], ['minute', 60]];
    for (const [unit, size] of units) {
        if (Math.abs(seconds) >= size) return relativeFormat.format(Math.round(seconds / size), unit);
    }
    return Math.abs(seconds) < 10 ? 'just now' : relativeFormat.format(seconds, 'second');
}

function formatSize(bytes) {
    if (bytes < 1024) return `${bytes} B`;
    if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`;
    return `${(bytes / 1024 / 1024).toFixed(1)} MB`;
}

function addressNode(address) {
    if (!address) return null;
    return h('span', { class: 'address', title: address.email },
        address.name ? [address.name, ' ', h('span', { class: 'email' }, `<${address.email}>`)] : address.email);
}

const displayName = (address) => (address ? address.name || address.email : '(no sender)');

function prettyJson(text) {
    try {
        return JSON.stringify(JSON.parse(text), null, 2);
    } catch {
        return text;
    }
}

// ---------------------------------------------------------------------------------------------
// Seen/unread tracking (per browser)

function loadSeen() {
    try {
        return new Set(JSON.parse(localStorage.getItem(SEEN_KEY) ?? '[]'));
    } catch {
        return new Set();
    }
}

function markSeen(id) {
    if (state.seen.has(id)) return;
    state.seen.add(id);
    try {
        localStorage.setItem(SEEN_KEY, JSON.stringify([...state.seen].slice(-MAX_SEEN)));
    } catch { }
}

// ---------------------------------------------------------------------------------------------
// Data

async function api(path, options) {
    const response = await fetch(path, options);
    if (!response.ok) throw new Error(`${response.status} ${response.statusText}`);
    return response.status === 204 ? null : response.json();
}

async function loadList() {
    const query = new URLSearchParams();
    if (state.search) query.set('search', state.search);
    if (state.provider) query.set('provider', state.provider);
    if (state.requestId) query.set('requestId', state.requestId);
    try {
        state.mails = await api(`/api/mails?${query}`);
    } catch (error) {
        console.error('Failed to load mails', error);
        return;
    }
    renderList();
    if (!state.selectedId) renderEmptyDetail();
}

let reloadTimer;
function scheduleReload() {
    clearTimeout(reloadTimer);
    reloadTimer = setTimeout(loadList, 150);
}

async function openMail(id) {
    state.selectedId = id;
    renderList();
    let detail;
    try {
        detail = await api(`/api/mails/${encodeURIComponent(id)}`);
    } catch {
        if (state.selectedId === id) closeMail();
        toast('That mail no longer exists');
        return;
    }
    if (state.selectedId !== id) return;

    state.detail = detail;
    state.related = [];
    state.tab = state.preferredTab && isTabAvailable(state.preferredTab, detail) ? state.preferredTab : defaultTab(detail);
    markSeen(id);
    updateLocation(id);
    document.body.classList.toggle('show-detail', true);
    renderList();
    renderDetail();

    if (detail.requestId) {
        api(`/api/mails?requestId=${encodeURIComponent(detail.requestId)}`).then((related) => {
            if (state.detail?.id === id && related.length > 1) {
                state.related = related;
                renderDetail();
            }
        }).catch(() => { });
    }
}

function closeMail() {
    state.selectedId = null;
    state.detail = null;
    document.body.classList.remove('show-detail');
    updateLocation(null);
    renderList();
    renderEmptyDetail();
}

/** Keeps a shareable #/mail/<id> link in the address bar without piling up history entries. */
function updateLocation(id) {
    const url = id ? `#/mail/${id}` : location.pathname + location.search;
    if ((id ? `#/mail/${id}` : '') !== location.hash) history.replaceState(null, '', url);
}

async function deleteMail(id) {
    const index = state.mails.findIndex((m) => m.id === id);
    const next = state.mails[index + 1] ?? state.mails[index - 1];
    try {
        await api(`/api/mails/${encodeURIComponent(id)}`, { method: 'DELETE' });
    } catch {
        toast('Delete failed');
        return;
    }
    state.mails = state.mails.filter((m) => m.id !== id);
    toast('Mail deleted');
    if (next && !isMobile()) openMail(next.id);
    else closeMail();
}

async function deleteAll() {
    if (!confirm('Delete all mails? This cannot be undone.')) return;
    await api('/api/mails', { method: 'DELETE' });
    state.mails = [];
    closeMail();
}

// ---------------------------------------------------------------------------------------------
// List

function renderList() {
    const unread = state.mails.filter((m) => !state.seen.has(m.id)).length;
    document.title = unread ? `(${unread}) Nevermail` : 'Nevermail';

    const filterBar = $('#filter-bar');
    filterBar.hidden = !state.requestId;
    filterBar.replaceChildren(...(state.requestId
        ? [h('span', null, 'Mails from request ', h('code', null, state.requestId)),
           h('button', { class: 'button small', type: 'button', onclick: () => setRequestFilter(null) }, 'Show all')]
        : []));

    if (state.mails.length === 0) {
        const filtered = state.search || state.provider || state.requestId;
        listEl.replaceChildren(h('div', { class: 'empty-list' }, filtered ? 'No mails match your filter.' : 'No mails yet. Send one!'));
    } else {
        listEl.replaceChildren(...state.mails.map(renderListItem));
    }

    const count = state.mails.length;
    $('#list-footer').textContent = `${count} mail${count === 1 ? '' : 's'}${unread ? ` · ${unread} unread` : ''}`;
}

function renderListItem(mail) {
    const classes = ['mail-item'];
    if (mail.id === state.selectedId) classes.push('selected');
    if (!state.seen.has(mail.id)) classes.push('unread');
    const recipients = mail.to.length
        ? `To: ${displayName(mail.to[0])}${mail.recipientCount > 1 ? ` +${mail.recipientCount - 1}` : ''}`
        : mail.recipientCount ? `${mail.recipientCount} hidden recipient${mail.recipientCount > 1 ? 's' : ''}` : 'No recipients';

    return h('div', {
        class: classes.join(' '),
        role: 'option',
        'aria-selected': String(mail.id === state.selectedId),
        dataset: { id: mail.id },
        onclick: () => openMail(mail.id),
    },
        h('div', { class: 'mail-from', title: mail.from?.email ?? '' }, displayName(mail.from)),
        h('div', { class: 'mail-time', title: new Date(mail.receivedAt).toLocaleString() }, formatListTime(mail.receivedAt)),
        h('div', { class: 'mail-subject' }, mail.subject || '(no subject)'),
        h('div', { class: 'mail-meta' },
            h('span', { class: 'mail-to' }, recipients),
            mail.sandbox && h('span', { class: 'badge warn' }, 'sandbox'),
            mail.attachmentCount > 0 && h('span', { class: 'badge', title: `${mail.attachmentCount} attachment(s)` }, icon('paperclip'), mail.attachmentCount),
            h('span', { class: 'badge provider', dataset: { provider: mail.provider } }, providerName(mail.provider))));
}

function moveSelection(delta) {
    if (state.mails.length === 0) return;
    const index = state.mails.findIndex((m) => m.id === state.selectedId);
    const next = index < 0 ? 0 : Math.min(Math.max(index + delta, 0), state.mails.length - 1);
    if (state.mails[next].id !== state.selectedId) {
        openMail(state.mails[next].id);
        listEl.querySelector(`[data-id="${state.mails[next].id}"]`)?.scrollIntoView({ block: 'nearest' });
    }
}

function setRequestFilter(requestId) {
    state.requestId = requestId;
    loadList();
}

// ---------------------------------------------------------------------------------------------
// Detail

const TABS = [
    ['preview', 'Preview'],
    ['text', 'Text'],
    ['source', 'HTML source'],
    ['headers', 'Headers'],
    ['details', 'Details'],
    ['raw', 'Raw'],
];

function isTabAvailable(tab, detail) {
    const m = detail.message;
    if (tab === 'preview' || tab === 'source') return Boolean(m.html);
    if (tab === 'text') return Boolean(m.text);
    if (tab === 'raw') return Boolean(detail.rawRequest);
    return true;
}

function defaultTab(detail) {
    return ['preview', 'text', 'details'].find((tab) => isTabAvailable(tab, detail));
}

function renderEmptyDetail() {
    detailEl.replaceChildren(state.mails.length === 0 && !state.search && !state.provider && !state.requestId
        ? renderWelcome()
        : h('div', { class: 'placeholder' }, icon('inbox'), 'Select a mail to read it'));
}

function renderDetail() {
    const detail = state.detail;
    if (!detail) return renderEmptyDetail();
    const m = detail.message;

    const header = h('div', { class: 'detail-header' },
        h('div', { class: 'detail-title' },
            h('button', { class: 'icon-button back-button', type: 'button', title: 'Back', 'aria-label': 'Back', onclick: closeMail }, icon('back')),
            h('h1', null, m.subject || '(no subject)'),
            h('div', { class: 'detail-actions' },
                h('a', { class: 'button', href: `/api/mails/${detail.id}/eml`, title: 'Download as .eml (opens in any mail client)' }, icon('download'), '.eml'),
                h('button', { class: 'icon-button', type: 'button', title: 'Copy link to this mail', 'aria-label': 'Copy link', onclick: () => copy(location.href) }, icon('link')),
                h('button', { class: 'icon-button', type: 'button', title: 'Delete (Del)', 'aria-label': 'Delete', onclick: () => deleteMail(detail.id) }, icon('trash')))),
        renderMeta(detail),
        renderNotices(detail),
        renderAttachments(detail));

    const tabs = h('div', { class: 'tabs', role: 'tablist' }, TABS.map(([key, label]) => {
        const available = isTabAvailable(key, detail);
        const count = key === 'headers' ? Object.keys(m.headers).length : 0;
        return h('button', {
            class: 'tab',
            type: 'button',
            role: 'tab',
            'aria-selected': String(state.tab === key),
            disabled: !available,
            title: available ? '' : 'Not part of this mail',
            onclick: () => {
                state.tab = state.preferredTab = key;
                renderDetail();
            },
        }, label, count ? h('span', { class: 'count' }, count) : null);
    }));

    detailEl.replaceChildren(header, tabs, h('div', { class: 'tab-body', role: 'tabpanel' }, renderTab(detail)));
}

function renderMeta(detail) {
    const m = detail.message;
    const rows = [
        ['From', addressNode(m.from) ?? h('span', { class: 'email' }, '(none)')],
        ['To', m.to.map(addressNode)],
        ['Cc', m.cc.map(addressNode)],
        ['Bcc', m.bcc.map(addressNode)],
        ['Reply-To', m.replyTo.map(addressNode)],
        ['Date', h('span', { title: detail.receivedAt }, `${new Date(detail.receivedAt).toLocaleString()} (${formatRelative(detail.receivedAt)})`)],
        ['Via', h('span', { class: 'badge provider', dataset: { provider: detail.provider } }, providerName(detail.provider))],
        ['Template', m.templateId && h('code', null, m.templateId)],
        ['Tags', m.tags.length > 0 && h('span', { class: 'chip-row' }, m.tags.map((tag) => h('span', { class: 'badge' }, tag)))],
    ];
    return h('dl', { class: 'meta-grid' }, rows
        .filter(([, value]) => value && (!Array.isArray(value) || value.length > 0))
        .map(([label, value]) => [h('dt', null, label), h('dd', null, value)]));
}

function renderNotices(detail) {
    const notices = [];
    if (detail.sandbox) {
        notices.push(h('div', { class: 'notice warn' }, 'Sent in sandbox mode: the real provider would have validated this mail but not delivered it.'));
    }
    if (detail.message.templateId && !detail.message.html && !detail.message.text) {
        notices.push(h('div', { class: 'notice' }, "Sent with a provider template. Nevermail can't render the template itself; its data is under Details."));
    }
    const sendAt = detail.message.sendAt;
    if (sendAt && new Date(sendAt) > new Date(detail.receivedAt)) {
        notices.push(h('div', { class: 'notice' }, `Scheduled: the provider would deliver this ${new Date(sendAt).toLocaleString()}.`));
    }
    if (state.related.length > 1) {
        const position = state.related.findIndex((r) => r.id === detail.id);
        notices.push(h('div', { class: 'notice' },
            `One request produced ${state.related.length} mails${position >= 0 ? ` (this is #${state.related.length - position})` : ''}.`,
            state.requestId !== detail.requestId && h('button', { class: 'button small', type: 'button', onclick: () => setRequestFilter(detail.requestId) }, 'Show them')));
    }
    return notices;
}

function attachmentUrl(detail, index) {
    return `/api/mails/${encodeURIComponent(detail.id)}/attachments/${index}`;
}

function renderAttachments(detail) {
    const attachments = detail.message.attachments;
    if (attachments.length === 0) return null;
    return h('div', { class: 'attachments' }, attachments.map((a, index) => {
        const name = a.filename || a.contentId || `attachment-${index + 1}`;
        const details = [a.contentType, a.url ? 'remote, not fetched' : formatSize(a.size), a.inline ? 'inline' : null].filter(Boolean).join(' · ');
        return h('a', {
            class: 'attachment',
            href: a.url && !a.content ? a.url : attachmentUrl(detail, index),
            target: a.url && !a.content ? '_blank' : null,
            rel: 'noopener',
            download: a.url && !a.content ? null : name,
            title: `${name}\n${details}`,
        }, icon('paperclip'), h('span', { class: 'name' }, name), h('span', { class: 'size' }, a.url && !a.content ? 'URL' : formatSize(a.size)));
    }));
}

function renderTab(detail) {
    const m = detail.message;
    switch (state.tab) {
        case 'preview': return renderPreview(detail);
        case 'text': return h('div', { class: 'text-body' }, m.text);
        case 'source': return h('pre', { class: 'code' }, m.html);
        case 'headers': return renderKeyValues(m.headers, 'No headers.');
        case 'details': return renderDetails(detail);
        case 'raw': return renderRaw(detail);
        default: return h('div', { class: 'empty-tab' }, 'Nothing to show.');
    }
}

function renderPreview(detail) {
    const frame = h('iframe', {
        title: 'Mail preview',
        // No scripts, no same-origin access; links may open in a new tab.
        sandbox: 'allow-popups allow-popups-to-escape-sandbox',
        referrerpolicy: 'no-referrer',
        style: { maxWidth: PREVIEW_WIDTHS[state.previewWidth] },
    });
    frame.srcdoc = previewHtml(detail);

    const widths = h('div', { class: 'segmented', role: 'group', 'aria-label': 'Preview width' },
        Object.keys(PREVIEW_WIDTHS).map((key) => h('button', {
            type: 'button',
            'aria-pressed': String(state.previewWidth === key),
            onclick: (event) => {
                state.previewWidth = key;
                frame.style.maxWidth = PREVIEW_WIDTHS[key];
                widths.querySelectorAll('button').forEach((b) => b.setAttribute('aria-pressed', String(b === event.currentTarget)));
            },
        }, key[0].toUpperCase() + key.slice(1))));

    return h('div', { class: 'preview' },
        h('div', { class: 'preview-toolbar' }, widths),
        h('div', { class: 'preview-frame-wrap' }, frame));
}

/**
 * The mail's HTML with cid: references replaced by data URIs, and links opening in a new tab.
 * Data URIs because the sandboxed (opaque origin) preview frame may not load images from this server.
 */
function previewHtml(detail) {
    let html = detail.message.html ?? '';
    // Longest content ids first, so "cid:logo" doesn't clobber "cid:logo-dark".
    const inline = detail.message.attachments
        .map((a) => ({ ...a, cid: a.contentId?.replace(/^<|>$/g, '') }))
        .filter((a) => a.cid && a.content)
        .sort((a, b) => b.cid.length - a.cid.length);
    for (const a of inline) {
        html = html.split(`cid:${a.cid}`).join(`data:${a.contentType || 'application/octet-stream'};base64,${a.content}`);
    }
    const base = '<base target="_blank">';
    return /<head[\s>]/i.test(html) ? html.replace(/<head([^>]*)>/i, `<head$1>${base}`) : base + html;
}

function renderKeyValues(entries, emptyText) {
    const pairs = Object.entries(entries ?? {});
    if (pairs.length === 0) return h('div', { class: 'empty-tab' }, emptyText);
    return h('table', { class: 'kv-table' }, h('tbody', null, pairs.map(([key, value]) => h('tr', null, h('th', null, key), h('td', null, value)))));
}

function renderDetails(detail) {
    const m = detail.message;
    const general = {
        'Mail id': detail.id,
        'Request id': detail.requestId,
        'Provider': providerName(detail.provider),
        'Received': detail.receivedAt,
    };
    if (m.sendAt) general['Send at'] = m.sendAt;
    if (detail.sandbox) general['Sandbox'] = 'yes';
    if (m.templateId) general['Template'] = m.templateId;

    return [
        h('div', { class: 'section-title' }, 'General'),
        renderKeyValues(general),
        h('div', { class: 'section-title' }, detail.provider === 'smtp' ? 'Metadata & SMTP envelope' : 'Metadata (custom args)'),
        renderKeyValues(m.metadata, 'No metadata.'),
        m.templateData && [
            h('div', { class: 'section-title' }, 'Template data'),
            h('pre', { class: 'code' }, JSON.stringify(m.templateData, null, 2)),
        ],
    ];
}

function renderRaw(detail) {
    const raw = detail.provider === 'smtp' ? detail.rawRequest : prettyJson(detail.rawRequest);
    return [
        h('div', { class: 'preview-toolbar' },
            h('button', { class: 'button small', type: 'button', onclick: () => copy(detail.rawRequest) }, icon('copy'), 'Copy')),
        h('pre', { class: 'code' }, raw),
    ];
}

// ---------------------------------------------------------------------------------------------
// Getting started

function renderWelcome() {
    const origin = location.origin;
    const enabled = state.info?.providers ?? Object.keys(PROVIDERS).filter((p) => p !== 'smtp');
    const endpoints = [
        ['sendgrid', origin, 'SendGridClientOptions.Host · Authorization: Bearer <any key>'],
        ['brevo', `${origin}/v3`, 'Configuration.BasePath · api-key header'],
        ['mandrill', `${origin}/api/1.3/`, 'HttpClient.BaseAddress for Mandrill.net · any 1.x version works'],
    ].filter(([provider]) => enabled.includes(provider));
    if (state.info?.smtpPort) {
        endpoints.push(['smtp', `${location.hostname}:${state.info.smtpPort}`, 'Plain SMTP, no TLS, any credentials. Inside Docker this is the container port.']);
    }

    const curl = `curl -i ${origin}/v3/mail/send \\
  -H 'Authorization: Bearer anything' \\
  -H 'Content-Type: application/json' \\
  -d '{"personalizations":[{"to":[{"email":"you@example.com"}]}],"from":{"email":"app@example.com"},"subject":"Hello from Nevermail","content":[{"type":"text/html","value":"<h1>It works!</h1>"}]}'`;

    return h('div', { class: 'welcome' }, h('div', { class: 'welcome-inner' },
        h('h2', null, 'No mails yet'),
        h('p', { class: 'lead' }, 'Point your app at one of these endpoints. Everything it sends shows up here, live, and never leaves your machine.'),
        endpoints.map(([provider, value, hint]) => h('div', { class: 'endpoint-card' },
            h('span', { class: 'badge provider', dataset: { provider } }, providerName(provider)),
            h('code', null, value),
            h('button', { class: 'icon-button', type: 'button', title: 'Copy', 'aria-label': `Copy ${providerName(provider)} endpoint`, onclick: () => copy(value) }, icon('copy')),
            h('span', { class: 'hint' }, hint))),
        enabled.includes('sendgrid') && [
            h('div', { class: 'section-title', style: { margin: '20px 0 0' } }, 'Try it'),
            h('pre', { class: 'code' }, curl),
            h('button', { class: 'button small', type: 'button', style: { marginTop: '6px' }, onclick: () => copy(curl) }, icon('copy'), 'Copy command'),
        ]));
}

// ---------------------------------------------------------------------------------------------
// Live updates

function connectEvents() {
    const live = $('#live');
    const setConnected = (connected) => {
        state.connected = connected;
        live.classList.toggle('connected', connected);
        live.title = connected ? 'Live: new mails appear automatically' : 'Reconnecting…';
    };

    const events = new EventSource('/api/events');
    events.addEventListener('ready', () => {
        setConnected(true);
        // We may have missed changes while disconnected.
        scheduleReload();
    });
    events.addEventListener('added', scheduleReload);
    events.addEventListener('cleared', () => {
        state.mails = [];
        closeMail();
        scheduleReload();
    });
    events.addEventListener('deleted', (event) => {
        const ids = event.data.split(',');
        if (ids.includes(state.selectedId)) closeMail();
        scheduleReload();
    });
    events.onerror = () => setConnected(false);

    // Safety net if the event stream is blocked (e.g. by a proxy).
    setInterval(() => {
        if (!state.connected) loadList();
    }, 10000);
}

// ---------------------------------------------------------------------------------------------
// Controls

function setupControls() {
    let searchTimer;
    const search = $('#search');
    search.addEventListener('input', () => {
        clearTimeout(searchTimer);
        searchTimer = setTimeout(() => {
            state.search = search.value.trim();
            loadList();
        }, 200);
    });

    const providerFilter = $('#provider-filter');
    for (const [key, name] of Object.entries(PROVIDERS)) {
        providerFilter.append(h('option', { value: key }, name));
    }
    providerFilter.addEventListener('change', () => {
        state.provider = providerFilter.value;
        loadList();
    });

    $('#clear-all').addEventListener('click', deleteAll);
    $('.brand').addEventListener('click', (event) => {
        event.preventDefault();
        search.value = '';
        providerFilter.value = '';
        Object.assign(state, { search: '', provider: '', requestId: null });
        closeMail();
        loadList();
    });

    setupTheme();

    document.addEventListener('keydown', (event) => {
        const typing = event.target.closest('input, select, textarea');
        if (event.key === 'Escape') {
            if (typing && search.value) {
                search.value = '';
                search.dispatchEvent(new Event('input'));
            } else if (state.selectedId && isMobile()) {
                closeMail();
            }
            return;
        }
        if (typing || event.ctrlKey || event.metaKey || event.altKey) return;
        if (event.key === '/') {
            event.preventDefault();
            search.focus();
        } else if (event.key === 'ArrowDown' || event.key === 'j') {
            event.preventDefault();
            moveSelection(1);
        } else if (event.key === 'ArrowUp' || event.key === 'k') {
            event.preventDefault();
            moveSelection(-1);
        } else if (event.key === 'Delete' && state.selectedId) {
            deleteMail(state.selectedId);
        }
    });

    window.addEventListener('hashchange', () => {
        const id = mailIdFromHash();
        if (id && id !== state.selectedId) openMail(id);
    });

    // Keep list times fresh (e.g. "14:02" becoming "Oct 13" after midnight).
    setInterval(renderList, 60000);
}

function setupTheme() {
    const button = $('#theme-toggle');
    const order = ['system', 'light', 'dark'];
    const read = () => {
        try {
            return localStorage.getItem('nevermail.theme') ?? 'system';
        } catch {
            return 'system';
        }
    };
    const apply = (theme) => {
        if (theme === 'system') delete document.documentElement.dataset.theme;
        else document.documentElement.dataset.theme = theme;
        button.title = `Theme: ${theme}`;
    };
    apply(read());
    button.addEventListener('click', () => {
        const next = order[(order.indexOf(read()) + 1) % order.length];
        try {
            localStorage.setItem('nevermail.theme', next);
        } catch { }
        apply(next);
        toast(`Theme: ${next}`);
    });
}

function mailIdFromHash() {
    const match = location.hash.match(/^#\/mail\/([A-Za-z0-9]+)$/);
    return match ? match[1] : null;
}

// ---------------------------------------------------------------------------------------------

async function start() {
    setupControls();
    try {
        state.info = await api('/api/info');
        const providerFilter = $('#provider-filter');
        // Only offer providers that are actually enabled.
        for (const option of [...providerFilter.options]) {
            if (option.value && option.value !== 'smtp' && !state.info.providers.includes(option.value)) option.remove();
            if (option.value === 'smtp' && !state.info.smtpPort) option.remove();
        }
    } catch { }
    await loadList();
    const id = mailIdFromHash();
    if (id) openMail(id);
    connectEvents();
}

start();
