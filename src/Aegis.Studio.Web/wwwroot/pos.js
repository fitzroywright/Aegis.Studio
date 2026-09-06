let posJobId = null;
let posDraftLines = [];
let posDraftTenders = [];
let posCreatedSale = null;
let posDeviceAgentBaseUrl = null;

const baseActivateView = activateView;
activateView = function(view) {
    if (view === 'pos') {
        showPos();
        return;
    }

    baseActivateView(view);
};

const baseOpenJob = openJob;
openJob = async function(id) {
    await baseOpenJob(id);
    const actions = genericView.querySelector('.job-actions');
    if (actions && !actions.querySelector('[data-pos-job]')) {
        const button = document.createElement('button');
        button.className = 'primary';
        button.dataset.posJob = id;
        button.textContent = 'Take Payment / P O S';
        button.onclick = () => openPos(id);
        actions.prepend(button);
    }
};

async function showPos() {
    activateNavigation('pos');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    const jobs = await json('/api/jobs');
    genericView.innerHTML = `
        <h1>P O S</h1>
        <p>Start a sale against a Studio job. Completed sales post directly to the job's financial history.</p>
        <div class="cards">
            <div class="metric">Jobs available<strong>${jobs.length}</strong></div>
            <div class="metric">Device agent<strong id="posDeviceSummary">Checking...</strong></div>
        </div>
        <h2>Select a job</h2>
        ${jobs.map(job => `
            <button class="job-row job-button" onclick="openPos('${job.id}')">
                <div><b>${esc(job.title)}</b><div class="meta">${esc(job.clientName)}</div></div>
                <span class="status-pill">Open P O S</span>
            </button>`).join('') || '<p class="muted">No jobs are available.</p>'}`;
    await refreshDeviceStatus('posDeviceSummary');
}

async function openPos(jobId) {
    activateNavigation('pos');
    conversationView.classList.add('hidden');
    genericView.classList.remove('hidden');
    posJobId = jobId;
    posDraftLines = [];
    posDraftTenders = [];
    posCreatedSale = null;
    const [job, sales] = await Promise.all([
        json('/api/jobs/' + jobId),
        json(`/api/jobs/${jobId}/pos/sales`)
    ]);

    genericView.innerHTML = `
        <div class="job-detail-header">
            <div>
                <button class="link-button" onclick="openJob('${jobId}')">← Job</button>
                <h1>P O S — ${esc(job.clientName)}</h1>
                <p>${esc(job.title)} · Job ${esc(job.id)}</p>
            </div>
            <button class="action" onclick="showPos()">All P O S Jobs</button>
        </div>
        <div class="pos-layout">
            <section class="pos-panel">
                <h2>Sale</h2>
                <div class="pos-entry">
                    <label>Description<input id="posDescription" placeholder="Wedding deposit, prints, frame..."></label>
                    <label>Unit price<input id="posUnitPrice" type="number" min="0" step="0.01" value="0"></label>
                    <label>Qty<input id="posQuantity" type="number" min="1" step="1" value="1"></label>
                    <button class="action" onclick="addPosLine()">Add</button>
                </div>
                <div class="pos-actions" style="margin-top:10px">
                    <button class="action" onclick="quickPosLine('Wedding Deposit',25000)">Wedding Deposit</button>
                    <button class="action" onclick="quickPosLine('Session Fee',10000)">Session Fee</button>
                    <button class="action" onclick="quickPosLine('Prints',2500)">Prints</button>
                    <button class="action" onclick="quickPosLine('Frame',5000)">Frame</button>
                </div>
                <div id="posCart"></div>
                <div class="pos-summary"><span>Total</span><span id="posTotal">${money(0)}</span></div>
            </section>
            <section class="pos-panel">
                <h2>Tender</h2>
                <div id="posDeviceStatus" class="device-status">Checking device agent...</div>
                <div class="pos-tender-entry">
                    <label>Method<select id="posTenderKind"><option>Cash</option><option>Card</option><option>BankTransfer</option><option>Other</option></select></label>
                    <label>Amount<input id="posTenderAmount" type="number" min="0.01" step="0.01"></label>
                    <label>Reference<input id="posTenderReference" placeholder="POS slip / bank ref"></label>
                    <button class="action" onclick="addPosTender()">Add</button>
                </div>
                <div id="posTenderList"></div>
                <div class="pos-summary"><span>Tendered</span><span id="posTenderTotal">${money(0)}</span></div>
                <button class="primary" style="width:100%" onclick="completePosSale()">Complete Sale</button>
                <p class="pos-muted">A completed sale becomes a Studio job financial event. Cash automatically requests the local drawer after completion.</p>
            </section>
        </div>
        <section class="pos-history">
            <h2>Previous sales for this job</h2>
            <div id="posHistory">${renderPosHistory(sales)}</div>
        </section>`;
    renderPosDraft();
    await refreshDeviceStatus('posDeviceStatus');
}

function quickPosLine(description, amount) {
    posDraftLines.push({ code: '', description, unitPrice: amount, quantity: 1 });
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
    document.getElementById('posUnitPrice').value = '0';
    document.getElementById('posQuantity').value = '1';
    renderPosDraft();
}

function removePosLine(index) {
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
        ${posDraftLines.map((line, index) => `<tr><td>${esc(line.description)}</td><td>${line.quantity}</td><td>${money(line.unitPrice)}</td><td>${money(line.unitPrice * line.quantity)}</td><td><button class="link-button" onclick="removePosLine(${index})">Remove</button></td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">No sale lines yet.</p>';

    tenderList.innerHTML = posDraftTenders.length ? `
        <table class="pos-grid"><thead><tr><th>Method</th><th>Amount</th><th>Reference</th><th></th></tr></thead><tbody>
        ${posDraftTenders.map((tender, index) => `<tr><td>${esc(tender.kind)}</td><td>${money(tender.amount)}</td><td>${esc(tender.reference || '')}</td><td><button class="link-button" onclick="removePosTender(${index})">Remove</button></td></tr>`).join('')}
        </tbody></table>` : '<p class="muted">No tender entered yet.</p>';

    const saleTotal = posDraftLines.reduce((sum, line) => sum + line.unitPrice * line.quantity, 0);
    const tenderTotal = posDraftTenders.reduce((sum, tender) => sum + tender.amount, 0);
    document.getElementById('posTotal').textContent = money(saleTotal);
    document.getElementById('posTenderTotal').textContent = money(tenderTotal);
    const tenderAmount = document.getElementById('posTenderAmount');
    if (tenderAmount && !tenderAmount.value) tenderAmount.value = Math.max(0, saleTotal - tenderTotal).toFixed(2);
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

    try {
        posCreatedSale = await json(`/api/jobs/${posJobId}/pos/sales`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ lines: posDraftLines })
        });
        posCreatedSale = await json(`/api/pos/sales/${posCreatedSale.id}/complete`, {
            method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ tenders: posDraftTenders, terminalId: 'StudioWeb' })
        });

        await sendSaleToDeviceAgent(posCreatedSale);
        alert(`Sale ${posCreatedSale.id} completed.`);
        await openPos(posJobId);
    } catch (error) {
        alert('Sale could not be completed: ' + error.message);
    }
}

function renderPosHistory(sales) {
    if (!sales.length) return '<p class="muted">No P O S sales recorded for this job.</p>';
    return sales.map(sale => `
        <div class="pos-sale">
            <div><b>${esc(sale.status)} · ${money(sale.total)}</b><div class="meta">${new Date(sale.createdAtUtc).toLocaleString()} · ${esc(sale.id)}</div></div>
            <div class="pos-actions">${sale.status === 'Completed' ? `<button class="action" onclick="reprintPosSale('${sale.id}')">Print receipt</button>` : ''}</div>
        </div>`).join('');
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
        if (target) target.textContent = `Ready — ${status.receiptPrinter} / ${status.cashDrawer}`;
    } catch {
        if (target) target.textContent = 'Offline — sale can continue; printing/drawer unavailable';
    }
}

async function sendSaleToDeviceAgent(sale) {
    try {
        const baseUrl = await loadDeviceAgentBaseUrl();
        const receiptLines = [
            "BRYAN'S STUDIO",
            `Sale: ${sale.id}`,
            ...sale.lines.map(line => `${line.quantity} x ${line.description} @ ${line.unitPrice.toFixed(2)} = ${line.total.toFixed(2)}`),
            `TOTAL: ${sale.total.toFixed(2)} JMD`,
            ...sale.tenders.map(tender => `${tender.kind}: ${tender.amount.toFixed(2)}${tender.reference ? ' / ' + tender.reference : ''}`)
        ];
        await fetch(baseUrl + '/api/receipts/print', {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ saleId: sale.id, reference: `StudioJob/${sale.referenceId}`, lines: receiptLines, total: sale.total, currency: 'JMD' })
        });
        if (sale.tenders.some(tender => tender.kind === 'Cash')) {
            await fetch(baseUrl + '/api/cash-drawer/open', {
                method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ saleId: sale.id })
            });
        }
    } catch {
        // Hardware convenience must not roll back a committed sale.
    }
}

async function reprintPosSale(saleId) {
    const sale = await json('/api/pos/sales/' + saleId);
    await sendSaleToDeviceAgent(sale);
    await refreshDeviceStatus('posDeviceStatus');
}
