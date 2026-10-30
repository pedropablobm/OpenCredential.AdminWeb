"""Browser test with mocked data; never contacts a deployed server or database."""
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--browser', help='Optional Chromium executable')
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
html = (root / 'wwwroot/index.html').read_text(encoding='utf-8')
css = (root / 'wwwroot/styles.css').read_text(encoding='utf-8')
script = (root / 'wwwroot/app.js').read_text(encoding='utf-8').replace('\ninitializeApp();', '')
html = html.replace('<link rel="stylesheet" href="/styles.css" />', '<style>' + css + '</style>')
html = html.replace('<script src="/app.js?v=20260504-1"></script>', '')

with sync_playwright() as playwright:
    options = {'headless': True}
    if args.browser:
        options['executable_path'] = args.browser
    browser = playwright.chromium.launch(**options)
    page = browser.new_page(viewport={'width': 1280, 'height': 800})
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.set_content(html, wait_until='load')
    page.add_script_tag(content=script)
    import_copy = page.locator('#importView').inner_text()
    assert 'requieren una clave inicial' in import_copy
    assert 'documento como clave temporal' not in import_copy
    page.evaluate('''() => {
        state.dashboard = { kpis: { activeUsers: 8, totalUsers: 10, availableComputers: 3, occupiedComputers: 2,
            lockedComputers: 1, disconnectedComputers: 4, orphanedComputers: 1, disabledComputers: 2 } };
        getOperationalComputers = () => [];
        renderBars = () => {}; renderTrend = () => {}; renderOverviewEquipmentAlerts = () => {}; renderOverviewAudit = () => {};
        renderOverview();
    }''')
    assert page.locator('#overviewKpiCards .kpi-card').count() == 4
    assert page.locator('#overviewSecondaryCards .kpi-card').count() == 4
    expect(page.locator('#overviewSecondaryCards')).to_be_hidden()
    expect(page.locator('#rangeDays')).to_be_hidden()
    page.locator('#overviewSecondaryDetails > summary').focus()
    page.keyboard.press('Enter')
    expect(page.locator('#overviewSecondaryCards')).to_be_visible()
    page.locator('#overviewTrendsDetails > summary').click()
    expect(page.locator('#rangeDays')).to_be_visible()
    page.evaluate('''() => {
        state.snapshot = { groups: [{ id: 7, name: 'Grupo de prueba' }] };
        loadAll = async () => {};
        bindForms();
        resetUserForm();
        openDrawer('userDrawer');
    }''')
    page.evaluate('''() => {
        const range = document.getElementById('rangeDays');
        range.value = '7'; range.dispatchEvent(new Event('change', {bubbles: true}));
        document.getElementById('overviewTrendsDetails').open = false;
    }''')
    assert page.locator('#overviewFilterCount').inner_text() == '(1 filtro activo)'
    expect(page.locator('#userPassword')).to_be_visible()
    assert page.locator('#userPassword').get_attribute('required') is not None
    expect(page.locator('#resetPasswordBtn')).to_be_hidden()
    expect(page.locator('#userCareer')).to_be_hidden()
    page.locator('#userAcademicSection > summary').click()
    expect(page.locator('#userCareer')).to_be_visible()
    expect(page.locator('#userGroupsSelector input[value="7"]')).to_be_visible()
    page.locator('#userGroupsSelector input[value="7"]').check()
    page.locator('#userAcademicSection > summary').click()
    assert page.locator('#userGroupsSelector input[value="7"]').is_checked()
    page.locator('#userUsername').fill('test')
    page.locator('#userDocument').fill('123')
    page.locator('#userFirstName').fill('Test')
    page.locator('#userLastName').fill('User')
    page.locator('#userEmail').fill('test@example.invalid')
    page.locator('#userPasswordSection > summary').click()
    assert not page.locator('#userPasswordSection').evaluate('(node) => node.open')
    assert not page.evaluate("document.getElementById('userForm').reportValidity()")
    assert page.locator('#userPasswordSection').evaluate('(node) => node.open')
    page.evaluate('''() => editUser({id: 3, username: 'test', documentId: '123', firstName: 'Test', lastName: 'User',
        email: 'test@example.invalid', hashMethod: 'MD5', active: true, groups: [{id: 7}]})''')
    expect(page.locator('#userPassword')).to_be_hidden()
    assert page.locator('#userPassword').get_attribute('required') is None
    page.locator('#userPasswordSection > summary').click()
    expect(page.locator('#resetPasswordBtn')).to_be_visible()
    assert page.locator('#userHashMethod').input_value() == 'MD5'
    assert page.locator('#userGroupsSelector input[value="7"]').is_checked()
    page.evaluate("() => { fetchJson = async () => { throw new Error('synthetic failure'); }; }")
    page.locator('#saveUserBtn').click()
    expect(page.locator('#userFormResult')).to_contain_text('No fue posible guardar')
    expect(page.locator('#userUsername')).to_have_value('test')
    assert page.locator('#userGroupsSelector input[value="7"]').is_checked()
    assert page.locator('#saveUserBtn').is_enabled()
    page.evaluate('''() => {
        window.testPayload = null;
        fetchJson = async (url, options) => { window.testPayload = JSON.parse(options.body); return {}; };
        loadAll = async () => {};
    }''')
    page.locator('#saveUserBtn').click()
    page.wait_for_function('window.testPayload !== null')
    assert page.evaluate('window.testPayload.password') == ''
    assert page.evaluate('window.testPayload.groupIds') == [7]
    assert page.evaluate('window.testPayload.hashMethod') == 'MD5'

    page.evaluate('''() => editComputer({id: 12, name: 'Equipo de prueba', inventoryTag: 'INV-12',
        location: 'Sala A', ipAddress: '192.0.2.10', status: 'Available', currentUsername: 'test'})''')
    expect(page.locator('#computerIpAddress')).to_be_hidden()
    expect(page.locator('#computerExtraSummary')).to_contain_text('configurados')
    page.locator('#computerExtraFields > summary').click()
    expect(page.locator('#computerIpAddress')).to_have_value('192.0.2.10')
    page.locator('#computerExtraFields > summary').click()
    page.evaluate('window.testPayload = null')
    page.locator('#computerForm button[type="submit"]').click()
    page.wait_for_function('window.testPayload !== null')
    assert page.evaluate('window.testPayload.ipAddress') == '192.0.2.10'
    assert page.evaluate('window.testPayload.currentUsername') == 'test'

    page.evaluate('''() => editRoom({id: 8, name: 'Sala A', code: 'A', canvasWidth: 1600, canvasHeight: 900, active: true})''')
    expect(page.locator('#roomCanvasWidth')).to_be_hidden()
    expect(page.locator('#roomSizeSummary')).to_contain_text('1600 × 900')
    page.locator('#roomSizeFields > summary').click()
    page.locator('#roomCanvasWidth').fill('100')
    page.locator('#roomSizeFields > summary').click()
    assert not page.evaluate("document.getElementById('roomForm').reportValidity()")
    assert page.locator('#roomSizeFields').evaluate('(node) => node.open')
    page.locator('#roomCanvasWidth').fill('1600')
    page.locator('#roomSizeFields > summary').click()
    page.evaluate('window.testPayload = null')
    page.locator('#roomForm button[type="submit"]').click()
    page.wait_for_function('window.testPayload !== null')
    assert page.evaluate('window.testPayload.canvasWidth') == 1600
    assert page.evaluate('window.testPayload.canvasHeight') == 900

    page.evaluate('''() => {
        closeDrawers();
        document.querySelectorAll('.module-view').forEach(node => node.classList.remove('is-active'));
        document.getElementById('reportsView').classList.add('is-active');
    }''')
    expect(page.locator('#reportOriginFilter')).to_be_hidden()
    page.locator('#reportAdvancedFilters > summary').click()
    page.locator('#reportOriginFilter').select_option('offline_cache')
    page.locator('#reportUsernameFilter').fill('estudiante')
    expect(page.locator('#reportAdvancedCount')).to_have_text('(2 activos)')
    page.locator('#reportAdvancedFilters > summary').click()
    expect(page.locator('#reportAdvancedCount')).to_have_text('(2 activos)')
    query = page.evaluate('buildReportsQuery()')
    assert 'sessionOrigin=offline_cache' in query and 'username=estudiante' in query
    page.evaluate("state.currentView = 'reports'")
    page.locator('#globalSearch').fill('otro-estudiante')
    expect(page.locator('#reportAdvancedCount')).to_have_text('(2 activos)')
    assert 'username=otro-estudiante' in page.evaluate('buildReportsQuery()')
    page.locator('#globalSearch').fill('')
    expect(page.locator('#reportAdvancedCount')).to_have_text('(1 activo)')

    page.evaluate('''async () => {
        closeDrawers();
        document.querySelectorAll('.module-view').forEach(node => node.classList.remove('is-active'));
        document.getElementById('configurationView').classList.add('is-active');
        fetchJson = async () => ({ sqlEnabled: true, provider: 'PostgreSql', host: 'db.invalid', port: 5432,
            databaseName: 'test', username: 'user', sslMode: 'VerifyFull', rootCertificate: '/certs/ca.crt',
            connectionDetailsAvailable: true, autoInitialize: false, passwordConfigured: false, runtimeConfigurationExists: false });
        await loadDatabaseConfiguration();
    }''')
    assert not page.locator('#databaseEditor').evaluate('(node) => node.open')
    expect(page.locator('#databaseConfigSummary')).to_contain_text('VerifyFull')
    page.locator('#databaseEditor > summary').click()
    expect(page.locator('#dbRootCertificate')).to_be_visible()
    expect(page.locator('#applySchemaBtn')).to_be_hidden()
    page.locator('#databaseEditor .config-sidecard > summary').click()
    expect(page.locator('#applySchemaBtn')).to_be_visible()
    page.locator('#dbHost').fill('edicion-sin-guardar')
    page.evaluate('loadDatabaseConfiguration()')
    expect(page.locator('#dbHost')).to_have_value('edicion-sin-guardar')
    page.locator('#databaseEditor .config-sidecard > summary').focus()
    page.keyboard.press('Enter')
    expect(page.locator('#applySchemaBtn')).to_be_hidden()
    page.set_viewport_size({'width': 375, 'height': 800})
    assert page.evaluate('document.documentElement.scrollWidth <= window.innerWidth')
    expect(page.locator('#dbHost')).to_be_visible()
    page.evaluate('''() => {
        document.querySelectorAll('.module-view').forEach(node => node.classList.remove('is-active'));
        document.getElementById('importView').classList.add('is-active');
        window.importCalls = 0;
        window.fetch = async () => { window.importCalls++; return { ok: false }; };
    }''')
    page.locator('#userFile').set_input_files({'name': 'synthetic.csv', 'mimeType': 'text/csv', 'buffer': b'username\ntest\n'})
    page.once('dialog', lambda dialog: dialog.dismiss())
    page.locator('#importUsersBtn').click()
    assert page.evaluate('window.importCalls') == 0
    assert page.locator('#userFile').evaluate('(node) => node.files.length') == 1
    page.once('dialog', lambda dialog: dialog.accept())
    page.locator('#importUsersBtn').click()
    expect(page.locator('#importResult')).to_contain_text('Algunas filas podrian haberse aplicado')
    assert page.locator('#userFile').evaluate('(node) => node.files.length') == 1
    assert page.locator('#importUsersBtn').is_enabled()
    page.evaluate("() => { window.fetch = async () => ({ ok: true, json: async () => ({ imported: 1, updated: 0, warnings: [] }) }); }")
    page.once('dialog', lambda dialog: dialog.accept())
    page.locator('#importUsersBtn').click()
    expect(page.locator('#importResult')).to_contain_text('Importados: 1')
    assert page.locator('#userFile').evaluate('(node) => node.files.length') == 0
    assert not errors, errors
    browser.close()
print('PASS Browser: formularios progresivos, valores preservados y TLS visible.')
