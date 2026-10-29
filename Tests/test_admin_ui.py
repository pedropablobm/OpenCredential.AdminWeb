"""Isolated AdminWeb browser regression. Never connect this test to a deployed server."""
import argparse
import json
import os
from pathlib import Path
import secrets
import socket
import subprocess
import tempfile
import time
import urllib.request

from playwright.sync_api import sync_playwright, expect


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--browser', help='Optional Chromium executable; uses a new, isolated headless browser profile')
    parser.add_argument('--assembly', help='Optional AdminWeb assembly built for the isolated test')
    parser.add_argument('--temp-root', default=str(Path(os.environ['LOCALAPPDATA']) / 'Temp' / 'opencode') if os.name == 'nt' else None)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    assembly = Path(args.assembly).resolve() if args.assembly else root / 'bin' / 'Release' / 'net10.0' / 'OpenCredential.AdminWeb.dll'
    if not assembly.exists():
        raise RuntimeError('Build AdminWeb in Release before running this test.')
    data = Path(tempfile.mkdtemp(prefix='adminweb-ui-', dir=args.temp_root))
    with socket.socket() as listener:
        listener.bind(('127.0.0.1', 0))
        port = listener.getsockname()[1]
    url = f'http://127.0.0.1:{port}'
    password = 'Synthetic UI ' + secrets.token_hex(16)
    env = {key: value for key, value in os.environ.items() if not key.lower().startswith(('adminauth__', 'database__', 'recoverydelivery__'))}
    env.update(ASPNETCORE_ENVIRONMENT='Production', ADMINWEB_DATA_DIR=str(data), Database__Mode='Json',
               AdminAuth__Enabled='true', AdminAuth__Accounts__0__Username='ui-test',
               AdminAuth__Accounts__0__Password=password, AdminAuth__Accounts__0__Role='SuperAdmin')
    with (data / 'server.log').open('w', encoding='utf-8') as log:
        server = subprocess.Popen(['dotnet', str(assembly), '--urls', url, '--contentRoot', str(data), '--webroot', str(root / 'wwwroot')],
                                  cwd=data, env=env, stdout=log, stderr=subprocess.STDOUT)
        try:
            deadline = time.monotonic() + 30
            while True:
                try:
                    with urllib.request.urlopen(url + '/health', timeout=1) as response:
                        if response.status == 200:
                            break
                except OSError:
                    if server.poll() is not None or time.monotonic() >= deadline:
                        raise RuntimeError('Isolated test server did not start; inspect its private temporary log.')
                    time.sleep(0.1)
            with sync_playwright() as playwright:
                browser = playwright.chromium.launch(headless=True, executable_path=args.browser)
                try:
                    context = browser.new_context(viewport={'width': 1280, 'height': 900}, reduced_motion='reduce')
                    page = context.new_page()
                    errors = []
                    page.on('pageerror', lambda error: errors.append(str(error)))
                    page.goto(url, wait_until='networkidle')
                    expect(page.get_by_label('Usuario administrador', exact=True)).to_be_visible()
                    page.locator('#loginUsername').fill('ui-test')
                    page.locator('#loginPassword').fill(password)
                    page.get_by_role('button', name='Ingresar', exact=True).click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    expect(page.locator('#globalSearch')).to_be_disabled()
                    expect(page.locator('#overviewKpiCards .kpi-card')).to_have_count(4)
                    details = page.locator('#overviewView details.overview-details')
                    assert not details.evaluate('(node) => node.open')
                    details.locator('summary').click()
                    expect(page.locator('#overviewExtraKpiCards .kpi-card')).to_have_count(4)
                    details.locator('summary').click()
                    print('PASS Browser: compact overview and expandable secondary metrics.')
                    headers = {'X-OpenCredential-Request': '1'}
                    for index in range(19):
                        response = context.request.post(url + '/api/users', headers=headers, data={
                            'username': f'page-test-{index:02}', 'firstName': 'Synthetic', 'lastName': 'User',
                            'email': 'test@example.invalid', 'documentId': str(index), 'active': True, 'password': password})
                        assert response.ok, 'Cannot seed synthetic test user.'
                    page.locator('button[data-view="users"]').click()
                    expect(page.locator('#globalSearch')).to_be_enabled()
                    expect(page.locator('#globalSearch')).to_have_attribute('aria-label', 'Buscar en usuarios: nombre, documento o correo...')
                    page.locator('#userSearch').fill('page-test-')
                    expect(page.locator('#usersSummary')).to_contain_text('19 coinciden')
                    expect(page.locator('#usersTable tbody tr')).to_have_count(8)
                    page.locator('#usersTable').get_by_role('button', name='Siguiente', exact=True).click()
                    expect(page.locator('#usersTable')).to_contain_text('Pagina 2 de 3')
                    expect(page.locator('#usersTable tbody tr').first).to_contain_text('page-test-08')
                    expect(page.locator('#usersTable').get_by_role('button', name='Siguiente', exact=True)).to_be_focused()
                    assert 'Hash' not in page.locator('#usersTable thead').inner_text()
                    page.locator('#usersTable tbody tr').first.get_by_role('button', name='Editar', exact=True).click()
                    expect(page.locator('#userHashMethod')).to_be_hidden()
                    page.locator('#userDrawer .form-options summary').click()
                    expect(page.locator('#userHashMethod')).to_have_value('BCRYPT')
                    page.keyboard.press('Escape')
                    expect(page.locator('#userDrawer')).to_be_hidden()
                    print('PASS Browser: server-side pagination and search.')

                    page.locator('#newUserBtn').click()
                    expect(page.locator('#userUsername')).to_be_focused()
                    assert page.locator('.page-shell').evaluate('(node) => node.inert')
                    controls = page.locator('#userDrawer button, #userDrawer input:not([type="hidden"]), #userDrawer select')
                    controls.last.focus()
                    page.keyboard.press('Tab')
                    expect(controls.first).to_be_focused()
                    page.keyboard.press('Shift+Tab')
                    expect(controls.last).to_be_focused()
                    page.locator('#userUsername').fill('unsaved-test')
                    page.once('dialog', lambda dialog: dialog.dismiss())
                    page.keyboard.press('Escape')
                    expect(page.locator('#userDrawer')).to_be_visible()
                    expect(page.locator('#userUsername')).to_have_value('unsaved-test')
                    page.once('dialog', lambda dialog: dialog.accept())
                    page.keyboard.press('Escape')
                    expect(page.locator('#userDrawer')).to_be_hidden()
                    expect(page.locator('#newUserBtn')).to_be_focused()
                    assert not page.locator('.page-shell').evaluate('(node) => node.inert')
                    print('PASS Browser: modal semantics, Tab trap, Escape, unsaved-change confirmation and restored focus.')

                    page.locator('#newUserBtn').click()
                    for field, value in {'userUsername': 'preserved-draft', 'userDocument': '999', 'userFirstName': 'Preserve',
                                         'userLastName': 'Draft', 'userEmail': 'draft@example.invalid', 'userPassword': password}.items():
                        page.locator('#' + field).fill(value)
                    def reject_write(route):
                        if route.request.method == 'POST':
                            route.fulfill(status=503, content_type='application/problem+json', body=json.dumps({'title': 'Synthetic outage'}))
                        else:
                            route.continue_()
                    page.route('**/api/users', reject_write)
                    page.get_by_role('button', name='Guardar usuario', exact=True).click()
                    expect(page.locator('#userForm .form-feedback')).to_be_visible()
                    expect(page.locator('#userUsername')).to_have_value('preserved-draft')
                    expect(page.get_by_role('button', name='Guardar usuario', exact=True)).to_be_enabled()
                    page.keyboard.press('Tab')
                    expect(page.locator('#userDrawer .icon-close')).to_be_focused()
                    page.unroute('**/api/users', reject_write)
                    page.once('dialog', lambda dialog: dialog.accept())
                    page.keyboard.press('Escape')
                    print('PASS Browser: failed save preserves the form and permits retry.')

                    previous_rows = page.locator('#usersTable tbody').inner_text()
                    last_success = page.locator('#lastRefreshAt').inner_text()
                    def fail_read(route):
                        route.fulfill(status=503, content_type='application/problem+json', body='{"title":"Synthetic outage"}')
                    page.route('**/api/summary?*', fail_read)
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'stale')
                    assert page.locator('#usersTable tbody').inner_text() == previous_rows
                    assert page.locator('#lastRefreshAt').inner_text() == last_success
                    page.unroute('**/api/summary?*', fail_read)
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    print('PASS Browser: outage retains last-good data and recovery refreshes it.')
                    page.route('**/api/users?*', fail_read)
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'partial')
                    expect(page.locator('#usersLoadStatus')).to_contain_text('ultimos datos')
                    assert page.locator('#usersTable tbody').inner_text() == previous_rows
                    page.locator('#userSearch').fill('synthetic-filter-no-match')
                    expect(page.locator('#usersLoadStatus')).to_contain_text('no se muestran resultados anteriores')
                    assert 'page-test-08' not in page.locator('#usersTable').inner_text()
                    page.unroute('**/api/users?*', fail_read)
                    page.locator('#userSearch').fill('page-test-')
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    expect(page.locator('#usersSummary')).to_contain_text('19 coinciden')
                    print('PASS Browser: user list failure preserves the summary and never mixes filter results.')
                    page.locator('button[data-view="configuration"]').click()
                    config_editor = page.locator('#databaseEditor')
                    assert not config_editor.evaluate('(node) => node.open')
                    config_editor.locator(':scope > summary').click()
                    expect(page.locator('#databaseConfigForm')).to_be_visible()
                    config_help = page.locator('#configurationView details.config-sidecard')
                    assert not config_help.evaluate('(node) => node.open')
                    expect(config_help.locator('.config-callout')).to_be_hidden()
                    config_help.locator('summary').click()
                    expect(config_help.locator('.config-callout')).to_be_visible()
                    config_help.locator('summary').click()
                    page.locator('#dbHost').fill('unsaved-host.example.invalid')
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    expect(page.locator('#dbHost')).to_have_value('unsaved-host.example.invalid')
                    page.locator('#dbName').fill('synthetic')
                    page.locator('#dbUsername').fill('synthetic')
                    page.locator('#dbPassword').fill('synthetic')
                    page.locator('#dbSslMode').select_option('VerifyFull')
                    expect(page.locator('#dbRootCertificate')).to_be_visible()
                    page.locator('#dbRootCertificate').fill('/certs/synthetic-ca.crt')
                    assert page.evaluate('getDatabaseConfigPayload().rootCertificate') == '/certs/synthetic-ca.crt'
                    page.locator('#dbSslMode').select_option('Require')
                    expect(page.locator('#dbRootCertificate')).to_be_hidden()
                    assert page.evaluate('getDatabaseConfigPayload().rootCertificate') is None
                    dialogs = []
                    schema_calls = []
                    page.route('**/api/configuration/database/schema', lambda route: (schema_calls.append(True), route.abort()))
                    page.once('dialog', lambda dialog: (dialogs.append(dialog.message), dialog.dismiss()))
                    page.locator('#applySchemaBtn').click()
                    assert dialogs and 'respaldo' in dialogs[0] and not schema_calls
                    print('PASS Browser: cancelling schema warning sends no modification request.')
                    page.locator('button[data-view="users"]').click()
                    print('PASS Browser: refreshing does not overwrite database configuration edits.')
                    page.locator('button[data-view="equipment"]').first.click()
                    expect(page.locator('#equipmentModuleKpis .kpi-card')).to_have_count(3)
                    equipment_details = page.locator('#equipmentView details.equipment-details')
                    assert not equipment_details.evaluate('(node) => node.open')
                    equipment_details.locator('summary').click()
                    expect(page.locator('#equipmentExtraKpis .kpi-card')).to_have_count(4)
                    page.locator('#equipmentTabList').focus()
                    page.keyboard.press('ArrowRight')
                    expect(page.locator('#equipmentTabMap')).to_be_focused()
                    expect(page.locator('#equipmentTabMap')).to_have_attribute('aria-selected', 'true')
                    expect(page.locator('#equipmentListPanel')).to_be_hidden()
                    expect(page.locator('#equipmentMapPanel')).to_be_visible()
                    map_help = page.locator('#equipmentMapPanel details.map-explainer')
                    assert not map_help.evaluate('(node) => node.open')
                    expect(map_help.locator('.map-legend')).to_be_hidden()
                    map_help.locator('summary').click()
                    expect(map_help.locator('.map-legend')).to_be_visible()
                    page.locator('button[data-view="academic"]').first.click()
                    page.locator('#academicTabCareers').focus()
                    page.keyboard.press('ArrowRight')
                    expect(page.locator('#academicTabSemesters')).to_be_focused()
                    expect(page.locator('#semestersPanel')).to_be_visible()
                    expect(page.locator('#careersPanel')).to_be_hidden()
                    expect(page.locator('#academicActionBtn')).to_have_text('Nuevo semestre')
                    print('PASS Browser: equipment and academic tabs support keyboard navigation.')
                    page.locator('button[data-view="reports"]').first.click()
                    expect(page.locator('#reportsKpiCards .kpi-card')).to_have_count(4)
                    report_filters = page.locator('#reportsView details.report-filters')
                    assert not report_filters.evaluate('(node) => node.open')
                    report_filters.locator('summary').click()
                    page.locator('#reportOriginFilter').select_option('offline_cache')
                    expect(page.locator('#reportAdvancedFilterCount')).to_have_text('(1 activo)')
                    report_filters.locator('summary').click()
                    expect(page.locator('#reportAdvancedFilterCount')).to_have_text('(1 activo)')
                    summary_tab = page.get_by_role('tab', name='Resumen', exact=True)
                    summary_tab.focus()
                    page.keyboard.press('ArrowRight')
                    expect(page.get_by_role('tab', name='Uso', exact=True)).to_be_focused()
                    expect(page.locator('#reportUsagePanel')).to_be_visible()
                    expect(page.locator('#reportSummaryPanel')).to_be_hidden()
                    page.keyboard.press('End')
                    expect(page.get_by_role('tab', name='Sesiones', exact=True)).to_be_focused()
                    expect(page.locator('#reportSessionsPanel')).to_be_visible()
                    assert 'sessionOrigin=offline_cache' in page.evaluate('buildReportsQuery()')
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    page.route('**/api/reports?*', fail_read)
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'partial')
                    expect(page.locator('#reportsLoadStatus')).to_contain_text('ultimos datos')
                    expect(page.locator('#reportResults')).to_be_visible()
                    expect(page.locator('#exportReportsCsvBtn')).to_be_disabled()
                    report_filters.locator('summary').click()
                    page.locator('#reportOriginFilter').select_option('online')
                    expect(page.locator('#reportResults')).to_be_hidden()
                    expect(page.locator('#reportsLoadStatus')).to_contain_text('no se muestran resultados anteriores')
                    page.unroute('**/api/reports?*', fail_read)
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    expect(page.locator('#reportResults')).to_be_visible()
                    print('PASS Browser: progressive report filters and keyboard-accessible sections.')
                    page.locator('button[data-view="import"]').first.click()
                    expect(page.locator('#globalSearch')).to_be_disabled()
                    writes = []
                    def track_import(route):
                        if 'preview=true' not in route.request.url:
                            writes.append(route.request.url)
                        route.continue_()
                    page.route('**/api/import/users*', track_import)
                    content = ('username,firstName,lastName,email,documentId,password\n'
                               f'synthetic-import,Synthetic,User,test@example.invalid,991,{password}\n').encode()
                    page.locator('#userFile').set_input_files({'name': 'synthetic.csv', 'mimeType': 'text/csv', 'buffer': content})
                    page.locator('#previewImportBtn').click()
                    expect(page.locator('#importReview')).to_be_visible()
                    expect(page.locator('#importReviewSummary')).to_contain_text('1 nuevos')
                    assert not writes
                    page.locator('#userFile').set_input_files({'name': 'synthetic-new.csv', 'mimeType': 'text/csv', 'buffer': content})
                    expect(page.locator('#importReview')).to_be_hidden()
                    assert not writes
                    page.locator('#previewImportBtn').click()
                    expect(page.locator('#importReview')).to_be_visible()
                    page.locator('#confirmImportBtn').click()
                    expect(page.locator('#importResult')).to_contain_text('Importados: 1')
                    assert len(writes) == 1
                    page.unroute('**/api/import/users*', track_import)
                    print('PASS Browser: import review is read-only, changing file invalidates it, confirmation imports once.')
                    def fail_import(route):
                        if 'preview=true' in route.request.url:
                            route.continue_()
                        else:
                            route.abort()
                    page.route('**/api/import/users*', fail_import)
                    uncertain = ('username,firstName,lastName,email,documentId,password\n'
                                 f'synthetic-uncertain,Synthetic,User,test@example.invalid,992,{password}\n').encode()
                    page.locator('#userFile').set_input_files({'name': 'uncertain.csv', 'mimeType': 'text/csv', 'buffer': uncertain})
                    page.locator('#previewImportBtn').click()
                    expect(page.locator('#importReview')).to_be_visible()
                    page.locator('#confirmImportBtn').click()
                    expect(page.locator('#importReview')).to_be_hidden()
                    expect(page.locator('#importResult')).to_contain_text('podria haberse aplicado')
                    assert page.locator('#userFile').input_value().endswith('uncertain.csv')
                    page.unroute('**/api/import/users*', fail_import)
                    print('PASS Browser: uncertain import result requires verification and preserves the file.')
                    portal_user = context.request.post(url + '/api/users', headers=headers, data={
                        'username': 'synthetic-portal', 'firstName': 'Synthetic', 'lastName': 'Portal',
                        'email': 'portal@example.invalid', 'documentId': '993', 'active': True,
                        'password': password, 'groupIds': [1]})
                    assert portal_user.ok, 'Cannot create synthetic portal user.'
                    portal = context.new_page()
                    portal.route('**/api/portal/me/sessions?*', fail_read)
                    portal.goto(url + '/portal.html', wait_until='networkidle')
                    portal.get_by_label('Usuario', exact=True).first.fill('synthetic-portal')
                    portal.get_by_label('Clave', exact=True).first.fill(password)
                    portal.get_by_role('button', name='Entrar al portal', exact=True).click()
                    expect(portal.locator('#portalShell')).to_be_visible()
                    portal.locator('[data-portal-view="sessions"]').click()
                    expect(portal.locator('#portalSessionsTable')).to_contain_text('No fue posible cargar el historial')
                    assert 'Todavia no hay accesos' not in portal.locator('#portalSessionsTable').inner_text()
                    portal.unroute('**/api/portal/me/sessions?*', fail_read)
                    portal.get_by_role('button', name='Reintentar', exact=True).click()
                    expect(portal.locator('#portalSessionsTable')).to_contain_text('Todavia no hay accesos')
                    portal.close()
                    print('PASS Browser: portal distinguishes a failed history request from an empty history.')
                    page.locator('button[data-view="users"]').first.click()
                    print('PASS Browser: equipment secondary metrics and map legend remain accessible.')
                    page.set_viewport_size({'width': 390, 'height': 844})
                    page.locator('#newUserBtn').click()
                    expect(page.locator('#userUsername')).to_be_focused()
                    assert page.locator('#userDrawer').evaluate('(node) => node.getBoundingClientRect().width') <= 390
                    page.keyboard.press('Escape')
                    assert not errors, 'Browser JavaScript errors were captured.'
                    print('PASS Browser: narrow viewport and reduced-motion mode; no uncaught JavaScript errors.')
                finally:
                    browser.close()
        finally:
            server.terminate()
            try:
                server.wait(timeout=10)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait(timeout=10)


if __name__ == '__main__':
    main()
