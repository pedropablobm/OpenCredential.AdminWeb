const { test } = require("node:test");
const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");
const path = require("node:path");
const host = { innerHTML: "" };
const context = vm.createContext({ document: { getElementById: () => host }, console, window: {}, crypto: require("node:crypto").webcrypto });
const source = fs.readFileSync(path.join(__dirname, "../wwwroot/app.js"), "utf8").replace("initializeApp();", "");
vm.runInContext(source, context);
const payload = '<img src=x onerror="alert(1)">';
test("Las etiquetas, graficas y auditoria escapan datos persistidos", () => {
    assert.ok(!context.renderStatusTag(payload).includes("<img"));
    context.renderBars("test", [{ label: payload, value: 1 }], "horas");
    assert.ok(!host.innerHTML.includes("<img"));
    context.renderOverviewAudit([{ action: payload, actorUsername: payload, entityType: payload, summary: payload }]);
    assert.ok(!host.innerHTML.includes("<img"));
    assert.ok(host.innerHTML.includes("&lt;img"));
});
test("Exportacion CSV neutraliza formulas y conserva comillas", () => {
    assert.ok(context.formatCsvCell("=HYPERLINK(1)").startsWith("'"));
    assert.ok(context.formatCsvCell(" +SUM(1)").startsWith("'"));
    assert.equal(context.formatCsvCell('a,"b"'), '"a,""b"""');
});
test("Generador seguro y visibilidad por rol", () => {
    const password = context.generatePassword();
    assert.equal(password.length, 14);
    assert.match(password, /[A-Z]/); assert.match(password, /[a-z]/); assert.match(password, /[0-9]/); assert.match(password, /[!@#$%*?]/);
    vm.runInContext('state.session = { authenticated: true, role: "Viewer" }', context);
    assert.equal(context.canView("reports"), false);
    assert.equal(context.canManage("users"), false);
    vm.runInContext('state.session = { authenticated: true, role: "Coordinator" }', context);
    assert.equal(context.canView("reports"), true);
    assert.equal(context.canManage("equipment"), false);
});

function loadContext(fetch) {
    const hosts = new Map();
    const document = { getElementById(id) {
        if (!hosts.has(id)) hosts.set(id, { innerHTML: "", textContent: "", dataset: {}, value: "", disabled: false });
        return hosts.get(id);
    }};
    const sandbox = vm.createContext({ document, fetch, URLSearchParams, console, window: {}, queueMicrotask, crypto: require("node:crypto").webcrypto });
    vm.runInContext(source, sandbox);
    vm.runInContext('populateSelects = () => {}; renderApp = () => {}; state.session = { authenticated: true, role: "Coordinator", username: "test" };', sandbox);
    return sandbox;
}

test("Una caida conserva datos, filtros y la fecha de la ultima carga valida", async () => {
    const sandbox = loadContext(async () => { throw new Error("unavailable"); });
    vm.runInContext('state.snapshot = { marker: "last-good" }; state.dashboard = { kpis: { totalUsers: 42 } }; state.lastRefreshAt = new Date("2026-10-23T10:00:00Z"); state.filters.users.query = "pending";', sandbox);
    await sandbox.loadAll({ background: true });
    assert.equal(vm.runInContext('state.snapshot.marker', sandbox), "last-good");
    assert.equal(vm.runInContext('state.dashboard.kpis.totalUsers', sandbox), 42);
    assert.equal(vm.runInContext('state.lastRefreshAt.toISOString()', sandbox), "2026-10-23T10:00:00.000Z");
    assert.equal(vm.runInContext('state.filters.users.query', sandbox), "pending");
    assert.equal(vm.runInContext('state.dataStale', sandbox), true);
    assert.equal(sandbox.document.getElementById("dataConnectionStatus").dataset.status, "stale");
    assert.equal(sandbox.document.getElementById("retryDataBtn").disabled, false);
});

test("Un fallo de usuarios no impide actualizar el resumen", async () => {
    const sandbox = loadContext(async url => {
        if (url.startsWith('/api/users?')) return { ok: false, status: 500, json: async () => ({ title: 'Synthetic users error' }) };
        return { ok: true, status: 200, json: async () => ({ marker: 'fresh' }) };
    });
    await sandbox.loadAll({ background: true });
    assert.equal(vm.runInContext('state.snapshot.marker', sandbox), 'fresh');
    assert.equal(vm.runInContext('state.dashboard.marker', sandbox), 'fresh');
    assert.equal(vm.runInContext('state.dataStale', sandbox), false);
    assert.equal(vm.runInContext('state.usersLoadFailed', sandbox), true);
    assert.equal(sandbox.document.getElementById('dataConnectionStatus').dataset.status, 'partial');
});

test("Un reporte no disponible conserva resultados compatibles pero bloquea exportacion", async () => {
    const sandbox = loadContext(async url => {
        if (url.startsWith('/api/reports?')) return { ok: false, status: 500, json: async () => ({ title: 'Synthetic report error' }) };
        if (url.startsWith('/api/users?')) return { ok: true, status: 200, json: async () => ({ items: [], page: 1 }) };
        return { ok: true, status: 200, json: async () => ({ marker: 'fresh' }) };
    });
    vm.runInContext('state.currentView = "reports"; state.reports = { marker: "last-good" }; state.reportsQuery = buildReportsQuery();', sandbox);
    await sandbox.loadAll({ background: true });
    assert.equal(vm.runInContext('state.reports.marker', sandbox), 'last-good');
    assert.equal(vm.runInContext('state.reportsLoadFailed', sandbox), true);
    assert.equal(sandbox.document.getElementById('reportResults').hidden, false);
    assert.equal(sandbox.document.getElementById('exportReportsCsvBtn').disabled, true);
    sandbox.document.getElementById('reportOriginFilter').value = 'offline_cache';
    sandbox.updateReportLoadStatus();
    assert.equal(sandbox.document.getElementById('reportResults').hidden, true);
    assert.equal(sandbox.document.getElementById('printReportsBtn').disabled, true);
});

test("El listado solicita pagina/filtros al servidor y el resumen usa modo compacto", async () => {
    const calls = [];
    const sandbox = loadContext(async url => {
        calls.push(url);
        return { ok: true, status: 200, json: async () => url.startsWith("/api/users?") ? { items: [], page: 2, total: 20, totalPages: 3 } : { marker: "new" } };
    });
    vm.runInContext('state.pagination.usersTable = 2; state.filters.users.query = "literal%_!"; state.filters.users.status = "inactive";', sandbox);
    await sandbox.loadAll({ background: true });
    assert.ok(calls.includes("/api/summary?compact=true"));
    const query = new URLSearchParams(calls.find(url => url.startsWith("/api/users?")).split("?")[1]);
    assert.equal(query.get("page"), "2");
    assert.equal(query.get("search"), "literal%_!");
    assert.equal(query.get("status"), "inactive");
    assert.equal(vm.runInContext('state.dataStale', sandbox), false);
});

test("Una respuesta tardia no repuebla datos despues de cerrar sesion", async () => {
    let release;
    const wait = new Promise(resolve => { release = resolve; });
    const sandbox = loadContext(async () => { await wait; return { ok: true, status: 200, json: async () => ({ marker: "must-not-appear" }) }; });
    const pending = sandbox.loadAll({ background: true });
    vm.runInContext('state.sessionEpoch++; state.session = null; state.snapshot = null;', sandbox);
    release();
    await pending;
    assert.equal(vm.runInContext('state.snapshot', sandbox), null);
});

test("Un 401 de una sesion antigua no cierra una sesion nueva", async () => {
    let release;
    const wait = new Promise(resolve => { release = resolve; });
    const sandbox = loadContext(async () => { await wait; return { status: 401 }; });
    vm.runInContext('handleUnauthorized = () => { throw new Error("must-not-log-out-new-session"); };', sandbox);
    const pending = sandbox.fetchJson("/api/users");
    vm.runInContext('state.sessionEpoch++;', sandbox);
    release();
    await assert.rejects(pending, /AUTH_REQUIRED/);
});

test("Inicio muestra cuatro indicadores principales sin eliminar los secundarios", () => {
    const sandbox = loadContext(async () => { throw new Error("unexpected fetch"); });
    vm.runInContext(`
        state.dashboard = { kpis: { availableComputers: 4, occupiedComputers: 2, orphanedComputers: 1,
            activeUsers: 8, totalUsers: 10, lockedComputers: 1, disconnectedComputers: 2, disabledComputers: 3 } };
        state.snapshot = {};
        globalThis.cards = {};
        getOperationalComputers = () => [];
        renderKpiCards = (id, value) => { cards[id] = value; };
        renderBars = () => {}; renderTrend = () => {}; renderOverviewEquipmentAlerts = () => {}; renderOverviewAudit = () => {};
    `, sandbox);
    sandbox.renderOverview();
    assert.equal(sandbox.cards.overviewKpiCards.length, 4);
    assert.equal(sandbox.cards.overviewExtraKpiCards.length, 4);
    assert.equal(sandbox.cards.overviewKpiCards[2].value, 1);
    assert.equal(sandbox.cards.overviewExtraKpiCards[1].value, 2);
});

test("La configuracion del despliegue no se presenta como SSL deshabilitado", async () => {
    const sandbox = loadContext(async () => { throw new Error("unexpected fetch"); });
    vm.runInContext(`
        state.session = { authenticated: true, role: "SuperAdmin" };
        fetchJson = async () => ({ sqlEnabled: true, provider: "PostgreSql", runtimeConfigurationExists: false,
            connectionDetailsAvailable: true, host: "db.example.invalid", port: 5432, databaseName: "synthetic",
            sslMode: "VerifyFull", rootCertificateConfigured: true, rootCertificate: "/certs/test-ca.crt", autoInitialize: false });
    `, sandbox);
    await sandbox.loadDatabaseConfiguration();
    assert.equal(sandbox.document.getElementById("dbSslMode").value, "VerifyFull");
    assert.equal(sandbox.document.getElementById("dbRootCertificate").value, "/certs/test-ca.crt");
    assert.equal(sandbox.document.getElementById("dbRootCertificateField").hidden, false);
    assert.equal(sandbox.document.getElementById("databaseConfigForm").dataset.deploymentManaged, "true");
    assert.match(sandbox.document.getElementById("databaseConfigNotice").textContent, /sustituira esa configuracion/);
    assert.match(sandbox.document.getElementById("databaseConfigSummary").textContent, /TLS configurado: VerifyFull/);
    assert.match(sandbox.document.getElementById("databaseConfigSummary").textContent, /ruta de CA configurada \(sin comprobar\)/);
    assert.equal(sandbox.document.getElementById("dbAutoInitialize").checked, false);
});

test("Filtros adicionales de reportes conservan su consulta y se indican al usuario", () => {
    const sandbox = loadContext(async () => { throw new Error("unexpected fetch"); });
    sandbox.document.getElementById("reportOriginFilter").value = "offline_cache";
    sandbox.document.getElementById("reportUsernameFilter").value = "student";
    sandbox.updateReportAdvancedFilterCount();
    assert.equal(sandbox.document.getElementById("reportAdvancedFilterCount").textContent, "(2 activos)");
    const query = new URLSearchParams(sandbox.buildReportsQuery());
    assert.equal(query.get("sessionOrigin"), "offline_cache");
    assert.equal(query.get("username"), "student");
});

test("Reportes conserva indicadores secundarios sin mostrarlos todos de inicio", () => {
    const sandbox = loadContext(async () => { throw new Error("unexpected fetch"); });
    vm.runInContext(`
        state.reports = { kpis: { sessionCount: 4, totalHours: 3, uniqueUsers: 2, activePrograms: 1,
            activeRooms: 1, offlineRecoveredSessions: 1, supersededSessions: 0, heartbeatTimeoutSessions: 2,
            unexpectedShutdownSessions: 0 }, sessions: [], topUsers: [], topEquipment: [] };
        globalThis.cards = {};
        renderKpiCards = (id, values) => { cards[id] = values; };
        renderBars = () => {}; renderPaginatedTable = () => {};
    `, sandbox);
    sandbox.renderReportsModule();
    assert.equal(sandbox.cards.reportsKpiCards.length, 4);
    assert.equal(sandbox.cards.reportsExtraKpis.length, 5);
    assert.equal(sandbox.cards.reportsExtraKpis[3].value, 2);
});

test("Una respuesta 401 antigua del portal no cierra una sesion nueva", async () => {
    let release;
    const wait = new Promise(resolve => { release = resolve; });
    const portalSource = fs.readFileSync(path.join(__dirname, "../wwwroot/portal.js"), "utf8").replace("initializePortal();", "");
    const sandbox = vm.createContext({ fetch: async () => { await wait; return { status: 401 }; }, document: {}, console });
    vm.runInContext(portalSource, sandbox);
    vm.runInContext('applyPortalSession = () => { throw new Error("must-not-log-out-new-session"); };', sandbox);
    const pending = sandbox.portalFetchJson('/api/portal/me');
    vm.runInContext('portalState.sessionEpoch++;', sandbox);
    release();
    await assert.rejects(pending, /AUTH_REQUIRED/);
});
