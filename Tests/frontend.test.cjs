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
