const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

function setup() {
    const context = vm.createContext({ console, URLSearchParams, Date });
    const source = fs.readFileSync(path.join(__dirname, '../wwwroot/app.js'), 'utf8')
        .replace(/\ninitializeApp\(\);/, '');
    vm.runInContext(source, context);
    vm.runInContext(`
        state.session = { authenticated: true };
        loadDatabaseConfiguration = async () => {};
        buildDashboardQuery = () => '';
        buildReportsQuery = () => '';
        populateSelects = () => {};
        renderApp = () => {};
        showDataLoadError = () => {};
    `, context);
    return context;
}

test('un fallo parcial conserva los ultimos datos validos y permite recuperacion', async () => {
    const context = setup();
    vm.runInContext(`
        state.snapshot = { marker: 'anterior' };
        state.dashboard = { marker: 'anterior' };
        state.reports = { marker: 'anterior' };
        fetchJson = async url => {
            if (url.startsWith('/api/summary')) throw new Error('sin conexion');
            return { marker: 'actual' };
        };
    `, context);
    await vm.runInContext('loadAll({ background: true })', context);
    assert.equal(vm.runInContext('state.snapshot.marker', context), 'anterior');
    assert.equal(vm.runInContext('state.dashboard.marker', context), 'actual');
    assert.equal(vm.runInContext('state.reports.marker', context), 'actual');
    vm.runInContext(`fetchJson = async () => ({ marker: 'recuperado' })`, context);
    await vm.runInContext('loadAll({ background: true })', context);
    assert.equal(vm.runInContext('state.snapshot.marker', context), 'recuperado');
});

test('un fallo inicial no inventa un resumen vacio', async () => {
    const context = setup();
    vm.runInContext(`fetchJson = async () => { throw new Error('sin conexion'); }`, context);
    await vm.runInContext('loadAll({ background: true })', context);
    assert.equal(vm.runInContext('state.snapshot', context), null);
    assert.equal(vm.runInContext('state.dashboard', context), null);
    assert.equal(vm.runInContext('state.reports', context), null);
});

test('una respuesta tardia de otra sesion no restaura datos', async () => {
    const context = setup();
    vm.runInContext(`
        fetchJson = () => new Promise(resolve => { globalThis.resolveRequest = resolve; });
        globalThis.loading = loadAll({ background: true });
    `, context);
    vm.runInContext(`state.sessionEpoch++; state.session = null; state.snapshot = null; resolveRequest({ marker: 'privado' });`, context);
    await vm.runInContext('loading', context);
    assert.equal(vm.runInContext('state.snapshot', context), null);
});

test('un cambio de filtro durante la carga solicita otra consulta', async () => {
    const context = setup();
    vm.runInContext(`
        globalThis.query = 'primero'; globalThis.calls = [];
        buildReportsQuery = () => query;
        fetchJson = async url => {
            calls.push(url);
            if (url === '/api/reports?primero') return await new Promise(resolve => { globalThis.resolveReport = resolve; });
            return { marker: url };
        };
        globalThis.loading = loadAll({ background: true });
    `, context);
    await new Promise(resolve => setImmediate(resolve));
    vm.runInContext(`query = 'segundo'; loadAll({ background: true }); resolveReport({ marker: 'primero' });`, context);
    await vm.runInContext('loading', context);
    assert.equal(vm.runInContext('state.reportsQuery', context), 'segundo');
    assert.equal(vm.runInContext('state.reports.marker', context), '/api/reports?segundo');
});

test('la exportacion rechaza datos obsoletos o de otro filtro', () => {
    const context = setup();
    context.window = { alert() {} };
    vm.runInContext(`state.reports = { sessions: [] }; state.reportsQuery = 'anterior'; state.reportsLoadFailed = false;`, context);
    assert.equal(vm.runInContext('reportsAreCurrent()', context), false);
    vm.runInContext(`state.reportsQuery = ''; state.reportsLoadFailed = true;`, context);
    assert.equal(vm.runInContext('reportsAreCurrent()', context), false);
    vm.runInContext(`state.reportsLoadFailed = false`, context);
    assert.equal(vm.runInContext('reportsAreCurrent()', context), true);
});

test('una lectura iniciada antes de escribir no habilita acciones con datos antiguos', async () => {
    const context = setup();
    vm.runInContext(`
        state.snapshot = { marker: 'inicial' }; state.dataErrors.snapshot = false;
        globalThis.summaryCalls = 0;
        fetch = async (url, options) => {
            if (url === '/api/summary' && ++summaryCalls === 1)
                return await new Promise(resolve => { globalThis.finishOld = () => resolve({ok:true, status:200, json:async () => ({marker:'antiguo'})}); });
            if (url === '/api/users/1')
                return await new Promise(resolve => { globalThis.finishWrite = () => resolve({ok:true, status:204}); });
            return {ok:true, status:200, json:async () => ({marker:'nuevo'})};
        };
        globalThis.loading = loadAll({background:true});
        globalThis.writing = fetchJson('/api/users/1', {method:'PUT'});
    `, context);
    vm.runInContext('finishOld()', context);
    await vm.runInContext('loading', context);
    assert.equal(vm.runInContext('state.dataErrors.snapshot', context), true);
    assert.equal(vm.runInContext('state.snapshot.marker', context), 'inicial');
    vm.runInContext('finishWrite()', context);
    await vm.runInContext('writing', context);
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(vm.runInContext('state.snapshot.marker', context), 'nuevo');
    assert.equal(vm.runInContext('state.dataErrors.snapshot', context), false);
});
