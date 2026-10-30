"""Prueba de navegador aislada: todas las respuestas de API son sinteticas."""
import argparse
from pathlib import Path
from playwright.sync_api import sync_playwright, expect

parser = argparse.ArgumentParser()
parser.add_argument('--browser')
args = parser.parse_args()
root = Path(__file__).resolve().parent.parent
html = (root / 'wwwroot/index.html').read_text(encoding='utf-8')
css = (root / 'wwwroot/styles.css').read_text(encoding='utf-8')
script = (root / 'wwwroot/app.js').read_text(encoding='utf-8').replace('\ninitializeApp();', '')
html = html.replace('<link rel="stylesheet" href="/styles.css?v=20261030-1" />', '<style>' + css + '</style>')
html = html.replace('<script src="/app.js?v=20261030-1"></script>', '')

with sync_playwright() as playwright:
    browser = playwright.chromium.launch(headless=True, **({'executable_path': args.browser} if args.browser else {}))
    page = browser.new_page()
    errors = []
    page.on('pageerror', lambda error: errors.append(str(error)))
    page.route('**/*', lambda route: route.abort())
    page.set_content(html)
    page.add_script_tag(content=script)
    page.evaluate('''() => {
        initializeReportFilters();
        state.session = { authenticated: true };
        document.body.classList.remove('auth-required');
        document.querySelector('.page-shell').classList.remove('is-hidden');
        window.failApi = true;
        window.fetch = async url => {
            if (window.failApi) throw new Error('caida simulada');
            let data;
            if (url.startsWith('/api/summary')) data = createEmptySnapshot();
            else if (url.startsWith('/api/dashboard')) {
                data = createEmptyDashboard(); data.kpis.totalUsers = 7;
            } else if (url.startsWith('/api/reports')) data = createEmptyReports();
            else throw new Error('ruta inesperada');
            return {ok: true, status: 200, json: async () => data};
        };
    }''')
    page.evaluate('loadAll({background:true})')
    expect(page.locator('#dataLoadStatus')).to_contain_text('sin datos disponibles')
    expect(page.locator('#overviewKpiCards')).to_be_hidden()
    page.evaluate('''async () => { window.failApi = false; await loadAll({background:true}); }''')
    expect(page.locator('#overviewKpiCards')).to_contain_text('/7')
    page.evaluate('''async () => { window.failApi = true; await loadAll({background:true}); }''')
    expect(page.locator('#overviewKpiCards')).to_contain_text('/7')
    expect(page.locator('#dataLoadStatus')).to_contain_text('desactualizado')
    page.evaluate('''() => { document.getElementById('reportUsernameFilter').value = 'otro'; updateDataLoadStatus(); }''')
    assert page.locator('#reportsResults').evaluate('(node) => node.hidden')
    assert page.evaluate('''() => { window.alert = () => {}; return reportsAreCurrent(); }''') is False
    page.evaluate('''async () => { window.failApi = false; await loadAll({background:true}); }''')
    assert not page.locator('#reportsResults').evaluate('(node) => node.hidden')
    expect(page.locator('#dataLoadStatus')).to_contain_text('actualizado')
    assert not errors, errors
    browser.close()
print('PASS Browser: caida inicial, conservacion, filtros y recuperacion.')
