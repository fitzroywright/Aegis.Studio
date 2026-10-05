let posJobId = null;
let posApplyToJobBalance=false;
let posDraftLines = [];
let posDraftTenders = [];
let posCreatedSale = null;
let posClientRequestId = null;
let posCatalog = [];
let posDeviceAgentBaseUrl = null;

async function showPos() {
    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');

    const [jobs, catalog, diagnostics, me] = await Promise.all([
        json('/api/jobs'),
        json('/api/pos/catalog'),
        json('/api/pos/diagnostics'),
        json('/api/auth/me')
    ]);
    posCatalog = catalog;
    const sales = diagnostics.sales;

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div>
                <h1>P O S</h1>
                <p>Job-linked sales, tenders, receipts, refunds and audit history.</p>
            </div>
            ${(me.user || me).isAdministrator ? '<button class="action" onclick="showPosCatalogManager()">Manage Catalog</button>' : ''}
        </div>
        <div class="cards">
            <div class="metric">Completed sales<strong>${sales.completedSales}</strong></div>
            <div class="metric">Gross completed<strong>${money(sales.grossCompleted)}</strong></div>
            <div class="metric">Refunded<strong>${money(sales.refunded)}</strong></div>
            <div class="metric">Active catalog items<strong>${catalog.length}</strong></div>
        </div>
        <h2>Select a job</h2>
        ${jobs.map(job => `
            <button class="job-row job-button" onclick="openPos('${job.id}')">
                <div><b>${esc(job.title)}</b><div class="meta">${esc(job.clientName)}${job.eventDate ? ' · ' + esc(job.eventDate) : ''}</div></div>
                <span class="status-pill">Open P O S</span>
            </button>`).join('') || '<p class="muted">No jobs are available.</p>'}`;
}

async function showPosCatalogManager() {
    const [items, me] = await Promise.all([
        json('/api/admin/pos/catalog/'),
        json('/api/auth/me')
    ]);
    if (!(me.user || me).isAdministrator) {
        alert('Administrator access is required.');
        return;
    }

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div><button class="link-button" onclick="showPos()">← P O S</button><h1>P O S Catalog</h1><p>Maintain reusable Studio products and services.</p></div>
        </div>
        <section class="pos-panel">
            <div class="pos-entry">
                <label>Code<input id="catalogCode" placeholder="SESSION"></label>
                <label>Name<input id="catalogName" placeholder="Portrait Session"></label>
                <label>Category<input id="catalogCategory" placeholder="Sessions"></label>
                <label>Price<input id="catalogPrice" type="number" min="0" step="0.01"></label>
                <button class="primary" onclick="savePosCatalogItem()">Add Item</button>
            </div>
        </section>
        <section class="pos-history">
            <table class="pos-grid">
                <thead><tr><th>Code</th><th>Name</th><th>Category</th><th>Price</th><th>Status</th><th></th></tr></thead>
                <tbody>${items.map(item => `<tr>
                    <td>${esc(item.code)}</td><td>${esc(item.name)}</td><td>${esc(item.category)}</td><td>${money(item.unitPrice)}</td>
                    <td>${item.isActive ? 'Active' : 'Inactive'}</td>
                    <td><button class="link-button" onclick='editPosCatalogItem(${JSON.stringify(JSON.stringify(item))})'>Edit</button></td>
                </tr>`).join('') || '<tr><td colspan="6" class="muted">No catalog items yet.</td></tr>'}</tbody>
            </table>
        </section>`;
}

async function savePosCatalogItem(existingId = null) {
    const request = {
        code: document.getElementById('catalogCode').value.trim(),
        name: document.getElementById('catalogName').value.trim(),
        category: document.getElementById('catalogCategory').value.trim(),
        unitPrice: Number(document.getElementById('catalogPrice').value),
        isActive: true,
        sortOrder: 0
    };
    if (!request.name || !Number.isFinite(request.unitPrice) || request.unitPrice < 0) {
        alert('Name and a valid non-negative price are required.');
        return;
    }
    await json(existingId ? `/api/admin/pos/catalog/${existingId}` : '/api/admin/pos/catalog/', {
        method: existingId ? 'PUT' : 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(request)
    });
    await showPosCatalogManager();
}

function editPosCatalogItem(serialized) {
    const item = JSON.parse(serialized);
    document.getElementById('catalogCode').value = item.code || '';
    document.getElementById('catalogName').value = item.name || '';
    document.getElementById('catalogCategory').value = item.category || '';
    document.getElementById('catalogPrice').value = item.unitPrice;
    const button = genericView.querySelector('.pos-entry .primary');
    button.textContent = 'Save Changes';
    button.onclick = () => savePosCatalogItem(item.id);
}

async function openPos(jobId,applyToBalance=false) {
    posApplyToJobBalance=applyToBalance;
    activateNavigation('frontdesk');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    posJobId = jobId;
    posDraftLines = [];
    posDraftTenders = [];
    posCreatedSale = null;
    posClientRequestId = crypto.randomUUID();

    const [job, sales, catalog] = await Promise.all([
        json('/api/jobs/' + jobId),
        json(`/api/jobs/${jobId}/pos/sales`),
        json('/api/pos/catalog')
    ]);
    posCatalog = catalog;
    if(applyToBalance){const balance=studioFinancials(job).balance;if(balance<=0)throw Error('This job has no remaining balance.');posDraftLines=[{code:'BALANCE',description:'Payment toward job balance',unitPrice:balance,quantity:1}];}

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div>
                <button class="link-button" onclick="showFrontDesk()">← Front Desk</button>
                <h1>P O S — ${esc(job.clientName)}</h1>
                <p>${esc(job.title)} · ${job.eventDate ? esc(job.eventDate) + ' · ' : ''}${esc(job.status)}</p>
            </div>
            <button class="action" onclick="openPos('${jobId}',true)">Take balance payment</button><button class="action" onclick="showPos()">All P O S Jobs</button>
        </div>
        <div class="pos-layout">
            <section class="pos-panel">
                <div class="pane-heading"><div><h2>Items</h2><p>Catalog or manual charge</p></div></div>
                <input id="posCatalogSearch" class="pos-search" placeholder="Search catalog" oninput="renderPosCatalog()">
                <div id="posCatalog" class="pos-catalog"></div>
                <details class="pos-manual-charge">
                    <summary>Manual charge</summary>
                    <div class="pos-entry">
                        <label>Description<input id="posDescription" placeholder="Special service or product"></label>
                        <label>Unit price<input id="posUnitPrice" type="number" min="0" step="0.01"></label>
                        <label>Qty<input id="posQuantity" type="number" min="1" step="1" value="1"></label>
                        <button class="action" onclick="addPosLine()">Add</button>
                    </div>
                </details>
                ${applyToBalance?`<label>Payment amount<input id="posBalancePayment" type="number" min="0.01" max="${studioFinancials(job).balance}" step="0.01" value="${posDraftLines[0].unitPrice}"></label>`:''}<h2>Cart</h2>
                <div id="posCart"></div>
                <div class="pos-summary"><span>Total</span><span id="posTotal">${money(0)}</span></div>
            </section>
            <section class="pos-panel">
                <h2>Checkout</h2>
                <div class="pos-tender-entry">
                    <label>Method<select id="posTenderKind"><option>Cash</option><option>Card</option><option>BankTransfer</option><option>Other</option></select></label>
                    <label>Amount<input id="posTenderAmount" type="number" min="0.01" step="0.01"></label>
                    <label>Reference<input id="posTenderReference" placeholder="Slip / bank reference"></label>
                    <button class="action" onclick="addPosTender()">Add</button>
                </div>
                <div id="posTenderList"></div>
                <div class="pos-summary"><span>Tendered</span><span id="posTenderTotal">${money(0)}</span></div>
                <div class="pos-summary pos-balance"><span>Balance</span><span id="posBalance">${money(0)}</span></div>
                <button id="completePosButton" class="primary" style="width:100%" onclick="completePosSale()">Complete Sale</button>
                <p class="pos-muted">Sales are persisted before completion, are idempotent per checkout attempt, and post once to the job financial history.</p>
                <details class="pos-hardware-detail"><summary>Local hardware</summary><div id="posDeviceStatus" class="device-status">Checking optional device agent...</div></details>
            </section>
        </div>
        <section class="pos-history">
            <h2>Sales for this job</h2>
            <div id="posHistory">${renderPosHistory(sales)}</div>
        </section>`;
    renderPosCatalog();
    if(applyToBalance){document.getElementById('posCatalogSearch').disabled=true;document.querySelector('.pos-manual-charge').hidden=true;const input=document.getElementById('posBalancePayment');input.onchange=()=>{const amount=Number(input.value);if(!Number.isFinite(amount)||amount<=0||amount>studioFinancials(job).balance){alert('Enter a positive amount within the outstanding balance.');return;}posDraftLines=[{code:'BALANCE',description:'Payment toward job balance',unitPrice:amount,quantity:1}];renderPosDraft();};}
    renderPosDraft();
    refreshDeviceStatus('posDeviceStatus');
}

function renderPosCatalog() {
    const target = document.getElementById('posCatalog');
    if (!target) return;
    const term = (document.getElementById('posCatalogSearch')?.value || '').trim().toLowerCase();
    const items = posCatalog.filter(item => !term || `${item.code} ${item.name} ${item.category}`.toLowerCase().includes(term));
    target.innerHTML = items.length ? items.map(item => `
        <button class="pos-catalog-item" onclick="addCatalogPosLine('${item.id}')">
            <span><b>${esc(item.name)}</b><small>${esc(item.category)}${item.code ? ' · ' + esc(item.code) : ''}</small></span>
            <strong>${money(item.unitPrice)}</strong>
        </button>`).join('') : '<p class="muted">No matching catalog items. Use Manual charge if needed.</p>';
}

function addCatalogPosLine(itemId) {
    const item = posCatalog.find(value => value.id === itemId);
    if (!item||posApplyToJobBalance) return;
    const existing = posDraftLines.find(line => line.catalogItemId === itemId && line.unitPrice === item.unitPrice);
    if (existing) {
        existing.quantity += 1;
    } else {
        posDraftLines.push({ catalogItemId: itemId, code: item.code || '', description: item.name, unitPrice: item.unitPrice, quantity: 1 });
    }
    renderPosDraft();
}

function addPosLine() {
    const description = document.getElementById('posDescription').value.trim();
    const unitPrice = Number(document.getElementById('posUnitPrice').value);
    const quantity = Number(document.getElementById('posQuantity').value);
    if (!description || !Number.isFinite(unitPrice) || unitPrice < 0 || !Number.isInteger(quantity) || quantity <= 0) {
        alert('Enter a description, valid price and quantity.');
        return;
    }
    posDraftLines.push({ code: '', description, unitPrice, quantity });
    document.getElementById('posDescription').value = '';
    document.getElementById('posUnitPrice').value = '';
    document.getElementById('posQuantity').value = '1';
    renderPosDraft();
}

function changePosLineQuantity(index, delta) {
    const line = posDraftLines[index];
    if (!line||posApplyToJobBalance) return;
    line.quantity += delta;
    if (line.quantity <= 0) posDraftLines.splice(index, 1);
    renderPosDraft();
}

function removePosLine(index) {
    if(posApplyToJobBalance)return;
    posDraftLines.splice(index, 1);
    renderPosDraft();
}

function addPosTender() {
    const kind = document.getElementById('posTenderKind').value;
    const amount = Number(document.getElementById('posTenderAmount').value);
    const reference = document.getElementById('posTenderReference').value.trim();
    if (!Number.isFinite(amount) || amount <= 0) {
        alert('Enter a positive tender amount.');
        return;
    }
    posDraftTenders.push({ kind, amount, reference: reference || null });
    document.getElementById('posTenderAmount').value = '';
    document.getElementById('posTenderReference').value = '';
    renderPosDraft();
}

function removePosTender(index) {
    posDraftTenders.splice(index, 1);
    renderPosDraft();
}

function renderPosDraft() {
    const cart = document.getElementById('posCart');
    const tenderList = document.getElementById('posTenderList');
    if (!cart || !tenderList) return;

    cart.innerHTML = posDraftLines.length ? `
        <table class="pos-grid"><thead><tr><th>Item</th><th>Qty</th><th>Price</th><th>Total</th><th></th></tr></thead><tbody>
        ${posDraftLines.map((line, index) => `<tr><td>${esc(line.description)}</td><td><button class="qty-button" onclick="changePosLineQuantity(${index},-1)">−</button> ${line.quantity} <button class="qty-button" onclick="changePosLineQuantity(${index},1)">+</button></td><td>${money(line.unitPrice)}</td><td>${money(line.unitPrice * line.quantity)}</td><td><button class="link-button" onclick="removePosLine(${index})">Remove</button></td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">Cart is empty.</p>';

    tenderList.innerHTML = posDraftTenders.length ? `
        <table class="pos-grid"><thead><tr><th>Method</th><th>Amount</th><th>Reference</th><th></th></tr></thead><tbody>
        ${posDraftTenders.map((tender, index) => `<tr><td>${esc(tender.kind)}</td><td>${money(tender.amount)}</td><td>${esc(tender.reference || '')}</td><td><button class="link-button" onclick="removePosTender(${index})">Remove</button></td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">No tender entered yet.</p>';

    const saleTotal = posDraftLines.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0);
    const tenderTotal = posDraftTenders.reduce((sum, tender) => sum + tender.amount, 0);
    const balance = saleTotal - tenderTotal;
    document.getElementById('posTotal').textContent = money(saleTotal);
    document.getElementById('posTenderTotal').textContent = money(tenderTotal);
    document.getElementById('posBalance').textContent = money(balance);
    const tenderAmount = document.getElementById('posTenderAmount');
    if (tenderAmount && !tenderAmount.value) tenderAmount.value = Math.max(0, balance).toFixed(2);
    const completeButton = document.getElementById('completePosButton');
    if (completeButton) completeButton.disabled = !posDraftLines.length || !posDraftTenders.length || Math.abs(balance) > 0.001;
}

async function completePosSale() {
    const saleTotal = posDraftLines.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0);
    const tenderTotal = posDraftTenders.reduce((sum, tender) => sum + tender.amount, 0);
    if (!posDraftLines.length) {
        alert('Add at least one sale line.');
        return;
    }
    if (!posDraftTenders.length || Math.abs(saleTotal - tenderTotal) > 0.001) {
        alert('Tender must equal the sale total.');
        return;
    }

    const button = document.getElementById('completePosButton');
    if (button) {
        button.disabled = true;
        button.textContent = 'Completing...';
    }

    try {
        posCreatedSale = await json(`/api/jobs/${posJobId}/pos/sales`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ lines: posDraftLines, clientRequestId: posClientRequestId,applyToJobBalance:posApplyToJobBalance })
        });
        posCreatedSale = await json(`/api/pos/sales/${posCreatedSale.id}/complete`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ tenders: posDraftTenders, terminalId: 'StudioWeb' })
        });

        sendSaleToDeviceAgent(posCreatedSale);
        await showPosReceipt(posCreatedSale.id, true);
        await openPos(posJobId);
    } catch (error) {
        alert('Sale could not be completed: ' + error.message);
        if (button) {
            button.disabled = false;
            button.textContent = 'Complete Sale';
        }
    }
}

function renderPosHistory(sales) {
    if (!sales.length) return '<p class="muted">No P O S sales recorded for this job.</p>';
    return sales.map(sale => {
        const refunded = (sale.refunds || []).reduce((sum, refund) => sum + refund.total, 0);
        return `
        <div class="pos-sale">
            <div>
                <b>${esc(sale.status)} · ${money(sale.total)}</b>
                <div class="meta">${new Date(sale.createdAtUtc).toLocaleString()} · ${esc(sale.id)}</div>
                ${refunded ? `<div class="meta">Refunded ${money(refunded)}</div>` : ''}
            </div>
            <div class="pos-actions">
                ${sale.status === 'Completed' ? `<button class="action" onclick="showPosReceipt('${sale.id}')">Receipt</button><button class="action" onclick="refundPosSale('${sale.id}')">Refund</button>` : ''}
                ${sale.status === 'Draft' ? `<button class="action danger" onclick="voidPosSale('${sale.id}')">Void</button>` : ''}
            </div>
        </div>`;
    }).join('');
}

async function showPosReceipt(saleId, justCompleted = false) {
    const receipt = await json(`/api/pos/sales/${saleId}/receipt`);
    let dialog = document.getElementById('posReceiptDialog');
    if (!dialog) {
        dialog = document.createElement('dialog');
        dialog.id = 'posReceiptDialog';
        document.body.appendChild(dialog);
    }
    dialog.innerHTML = `
        <div class="receipt-sheet">
            <div class="receipt-header"><h2>BRYAN'S STUDIO</h2><div>${esc(receipt.receiptNumber)}</div><div>${new Date(receipt.createdAtUtc).toLocaleString()}</div></div>
            <div class="receipt-meta">Client: ${esc(receipt.clientName)}<br>Job: ${esc(receipt.jobTitle)}</div>
            <table class="receipt-lines"><tbody>${receipt.lines.map(line => `<tr><td>${line.quantity} × ${esc(line.description)}</td><td>${money(line.total)}</td></tr>`).join('')}</tbody></table>
            <div class="receipt-total">TOTAL ${money(receipt.total)}</div>
            <div class="receipt-tenders">${receipt.tenders.map(tender => `${esc(tender.kind)}: ${money(tender.amount)}${tender.reference ? ' · ' + esc(tender.reference) : ''}`).join('<br>')}</div>
            <div class="receipt-footer">Cashier: ${esc(receipt.cashierUserId)}<br>Thank you.</div>
        </div>
        <div class="dialog-actions"><button onclick="document.getElementById('posReceiptDialog').close()">Close</button><button class="primary" onclick="window.print()">Print</button></div>`;
    dialog.showModal();
    if (justCompleted) dialog.dataset.justCompleted = 'true';
}

const posRefundAttempts=new Map();
async function refundPosSale(saleId) {
    if(posRefundAttempts.has(saleId)){await json(`/api/pos/sales/${saleId}/refunds`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(posRefundAttempts.get(saleId))});posRefundAttempts.delete(saleId);await openPos(posJobId);return;}
    const sale = await json('/api/pos/sales/' + saleId);
    const reason = prompt('Refund reason:');
    if (!reason || !reason.trim()) return;

    const refundedByLine = new Map();
    for (const refund of sale.refunds || []) {
        for (const line of refund.lines || []) {
            refundedByLine.set(line.saleLineId, (refundedByLine.get(line.saleLineId) || 0) + line.quantity);
        }
    }
    const lines = sale.lines.map(line => ({
        saleLineId: line.id,
        unitPrice: line.unitPrice,
        quantity: Math.max(0, line.quantity - (refundedByLine.get(line.id) || 0))
    })).filter(line => line.quantity > 0);
    const amount = lines.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0);
    if (!lines.length || amount <= 0) {
        alert('This sale is already fully refunded.');
        return;
    }

    const method = sale.tenders?.[0]?.kind || 'Other';
    if (!confirm(`Refund ${money(amount)} using ${method}?`)) return;
    const request={operationId:crypto.randomUUID(),lines,tenders:[{kind:method,amount,reference:'Refund'}],reason:reason.trim()};posRefundAttempts.set(saleId,request);
    await json(`/api/pos/sales/${saleId}/refunds`,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(request)});posRefundAttempts.delete(saleId);
    await openPos(posJobId);
}

async function voidPosSale(saleId) {
    const reason = prompt('Void reason:');
    if (!reason || !reason.trim()) return;
    await json(`/api/pos/sales/${saleId}/void`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ reason: reason.trim() })
    });
    await openPos(posJobId);
}

async function loadDeviceAgentBaseUrl() {
    if (posDeviceAgentBaseUrl) return posDeviceAgentBaseUrl;
    const config = await json('/api/pos/device-agent');
    posDeviceAgentBaseUrl = config.baseUrl;
    return posDeviceAgentBaseUrl;
}

async function refreshDeviceStatus(targetId) {
    const target = document.getElementById(targetId);
    try {
        const baseUrl = await loadDeviceAgentBaseUrl();
        const response = await fetch(baseUrl + '/api/devices/status');
        if (!response.ok) throw new Error('Unavailable');
        const status = await response.json();
        if (target) target.textContent = `Available — ${status.receiptPrinter} / ${status.cashDrawer}`;
    } catch {
        if (target) target.textContent = 'Not available. Browser receipt and all sale functions remain available.';
    }
}

async function sendSaleToDeviceAgent(sale) {
    try {
        const baseUrl = await loadDeviceAgentBaseUrl();
        const receiptText = await fetch(`/api/pos/sales/${sale.id}/receipt/text`).then(response => response.text());
        await fetch(baseUrl + '/api/receipts/print', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ saleId: sale.id, reference: `StudioJob/${sale.referenceId}`, lines: receiptText.split('\n').filter(Boolean), total: sale.total, currency: 'JMD' })
        });
        if (sale.tenders.some(tender => tender.kind === 'Cash')) {
            await fetch(baseUrl + '/api/cash-drawer/open', {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ saleId: sale.id })
            });
        }
    } catch {
        // Hardware is optional and must never roll back a committed sale.
    }
}

