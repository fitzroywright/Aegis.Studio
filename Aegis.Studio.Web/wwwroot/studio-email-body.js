// Presentation only. Never overwrite the stored provider body.
(function (root) {
    'use strict';
    const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
    function split(value) {
        const original = String(value ?? '').replace(/\r\n?/g, '\n');
        let text = original;
        let collapsed = false;
        // Require an email address and a date-like introducer, including wrapped Gmail headers.
        const gmail = /(?:^|\n|[ \t]{2,})(On\s+(?=[\s\S]{0,700}?\bwrote:)(?=[\s\S]{0,700}?[^\s<>]+@[^\s<>]+)[\s\S]{1,700}?\bwrote:)/i;
        const outlook = /(?:^|\n)(?:[ \t]*[-_]{5,}[ \t]*\n)?[ \t]*From:[^\n]+\n[\s\S]{0,400}?Sent:[^\n]+\n[\s\S]{0,400}?To:[^\n]+\n[\s\S]{0,400}?Subject:[^\n]*/i;
        const originalMarker = /(?:^|\n)[ \t]*-{5,}[ \t]*Original Message[ \t]*-{5,}/i;
        const matches = [gmail.exec(text), outlook.exec(text), originalMarker.exec(text)].filter(Boolean).sort((a,b) => a.index-b.index);
        const header = matches[0];
        if (header) {
            const tail = text.slice(header.index + header[0].length);
            // In plain-text inline replies, preserve every unquoted answer. Never hide the
            // whole suffix when explicit > quoting provides a more precise boundary.
            if (/^\s*>/m.test(tail)) {
                const answers = tail.split('\n').filter(line => !/^\s*>/.test(line)).join('\n');
                text = text.slice(0, header.index) + '\n' + answers;
            } else text = text.slice(0, header.index);
            collapsed = true;
        }
        // Explicit quotation blocks without a recognized mail header are still unambiguous.
        if (/^\s*>/m.test(text)) {
            text = text.split('\n').filter(line => !/^\s*>/.test(line)).join('\n');
            collapsed = true;
        }
        // Handle the transport-appended footer even when Graph supplies a single line.
        const disclaimer = /\bNotice of Confidentiality:\s*The information contained in this communication\b/i.exec(text);
        if (disclaimer) { text = text.slice(0, disclaimer.index); collapsed = true; }
        const lines = text.split('\n');
        for (let i=0; i<lines.length; i++) {
            const line = lines[i].trim();
            const explicit = /^--\s*$/.test(line) || /^(?:sent from my (?:iphone|ipad|android|device)|sent from outlook|get outlook for (?:ios|android))\b/i.test(line);
            const closing = /^(?:best regards|kind regards|warm regards|regards|sincerely|yours sincerely)[,!]?$/i.test(line);
            const trailing = lines.slice(i+1).filter(l => l.trim());
            const contact = trailing.some(l => /@|https?:\/\/|\b(?:tel|phone|mobile):/i.test(l));
            const nameOnly = trailing.length === 1 && /^[\p{L} .'-]{2,70}$/u.test(trailing[0].trim());
            if ((explicit && trailing.length <= 12) || (closing && trailing.length <= 8 && (contact || nameOnly))) {
                text = lines.slice(0,i).join('\n'); collapsed = true; break;
            }
        }
        return { body: text.trim(), collapsed, original };
    }
    function parts(message) {
        return message?.externalMessageId || message?.internetMessageId
            ? split(message.body) : {body:String(message?.body ?? ''),collapsed:false,original:String(message?.body ?? '')};
    }
    function render(message, renderText = escape) {
        const result = parts(message);
        const body = result.body ? renderText(result.body) : result.collapsed ? '<span class="muted">No new reply text</span>' : '';
        return `<div class="studio-chat-body studio-email-body" style="white-space:pre-wrap;overflow-wrap:anywhere">${body}</div>` +
            (result.collapsed ? `<details class="studio-email-signature"><summary>Show quoted text and signature</summary><div class="studio-chat-body" style="white-space:pre-wrap;overflow-wrap:anywhere">${escape(result.original)}</div></details>` : '');
    }
    const api = {split,parts,render};
    if (typeof module !== 'undefined' && module.exports) module.exports = api;
    if (root) root.StudioEmailBody = api;
})(typeof window !== 'undefined' ? window : null);
