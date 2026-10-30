const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');

test('TLS verificado conserva la CA y MySQL no muestra controles PostgreSQL', async () => {
    const elements = new Map();
    const document = { getElementById(id) {
        if (!elements.has(id)) elements.set(id, { value: '', checked: false, hidden: false, disabled: false, textContent: '', dataset: {} });
        return elements.get(id);
    }};
    const context = vm.createContext({ document, console });
    const source = fs.readFileSync(path.join(__dirname, '../wwwroot/app.js'), 'utf8').replace(/\ninitializeApp\(\);/, '');
    vm.runInContext(source, context);
    vm.runInContext(`fetchJson = async () => ({ sqlEnabled: true, provider: 'PostgreSql', host: 'db.invalid', port: 5432,
        databaseName: 'test', username: 'user', sslMode: 'VerifyFull', rootCertificate: '/certs/ca.crt',
        connectionDetailsAvailable: true, autoInitialize: false, passwordConfigured: false, runtimeConfigurationExists: false });`, context);
    await vm.runInContext('loadDatabaseConfiguration()', context);
    assert.equal(document.getElementById('dbSslMode').value, 'VerifyFull');
    assert.equal(document.getElementById('dbRootCertificateField').hidden, false);
    assert.equal(vm.runInContext('getDatabaseConfigPayload().rootCertificate', context), '/certs/ca.crt');
    assert.match(document.getElementById('databaseConfigResult').textContent, /TLS VerifyFull/);
    assert.match(document.getElementById('databaseConfigSummary').textContent, /TLS configurado: VerifyFull/);
    document.getElementById('dbHost').value = 'edicion-sin-guardar';
    document.getElementById('databaseConfigForm').dataset.dirty = 'true';
    await vm.runInContext('loadDatabaseConfiguration()', context);
    assert.equal(document.getElementById('dbHost').value, 'edicion-sin-guardar');
    document.getElementById('dbProvider').value = 'MySql';
    vm.runInContext('syncDatabaseTlsFields()', context);
    assert.equal(document.getElementById('dbSslMode').disabled, true);
    assert.equal(document.getElementById('dbRootCertificateField').hidden, true);
    assert.equal(vm.runInContext('getDatabaseConfigPayload().rootCertificate', context), null);
});
