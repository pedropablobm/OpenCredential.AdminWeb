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
