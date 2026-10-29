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
    parser.add_argument('--temp-root', default=str(Path(os.environ['LOCALAPPDATA']) / 'Temp' / 'opencode') if os.name == 'nt' else None)
    args = parser.parse_args()
    root = Path(__file__).resolve().parent.parent
    assembly = root / 'bin' / 'Release' / 'net10.0' / 'OpenCredential.AdminWeb.dll'
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
                    headers = {'X-OpenCredential-Request': '1'}
                    for index in range(19):
                        response = context.request.post(url + '/api/users', headers=headers, data={
                            'username': f'page-test-{index:02}', 'firstName': 'Synthetic', 'lastName': 'User',
                            'email': 'test@example.invalid', 'documentId': str(index), 'active': True, 'password': password})
                        assert response.ok, 'Cannot seed synthetic test user.'
                    page.locator('button[data-view="users"]').click()
                    page.locator('#userSearch').fill('page-test-')
                    expect(page.locator('#usersSummary')).to_contain_text('19 coinciden')
                    expect(page.locator('#usersTable tbody tr')).to_have_count(8)
                    page.locator('#usersTable').get_by_role('button', name='Siguiente', exact=True).click()
                    expect(page.locator('#usersTable')).to_contain_text('Pagina 2 de 3')
                    expect(page.locator('#usersTable tbody tr').first).to_contain_text('page-test-08')
                    expect(page.locator('#usersTable').get_by_role('button', name='Siguiente', exact=True)).to_be_focused()
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
                    page.locator('button[data-view="configuration"]').click()
                    page.locator('#dbHost').fill('unsaved-host.example.invalid')
                    page.locator('#retryDataBtn').click()
                    expect(page.locator('#dataConnectionStatus')).to_have_attribute('data-status', 'ready')
                    expect(page.locator('#dbHost')).to_have_value('unsaved-host.example.invalid')
                    page.locator('button[data-view="users"]').click()
                    print('PASS Browser: refreshing does not overwrite database configuration edits.')
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
