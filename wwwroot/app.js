const state = {
    snapshot: null,
    dashboard: null,
    session: null
};

async function fetchJson(url, options) {
    const response = await fetch(url, {
        headers: { "Content-Type": "application/json" },
        credentials: "same-origin",
        ...options
    });

    if (response.status === 401) {
        handleUnauthorized();
        throw new Error("AUTH_REQUIRED");
    }

    if (response.status === 403) {
        alert("Tu perfil no tiene permisos para ejecutar esta accion.");
        throw new Error("FORBIDDEN");
    }

    if (!response.ok) {
        throw new Error(`Error ${response.status}`);
    }

    if (response.status === 204) {
        return null;
    }

    return response.json();
}

async function loadAll() {
    const snapshot = await fetchJson("/api/summary");
    state.snapshot = snapshot;
    populateSelects(snapshot);
    renderTables(snapshot);
    renderAudit(snapshot.auditEntries || []);
    await loadDatabaseConfiguration();
    await loadDashboard();
}

async function loadDatabaseConfiguration() {
    try {
        const config = await fetchJson("/api/configuration/database");
        document.getElementById("dbProvider").value = config.provider || "PostgreSql";
        document.getElementById("dbHost").value = config.host || "";
        document.getElementById("dbPort").value = config.port || (config.provider === "MySql" ? 3306 : 5432);
        document.getElementById("dbName").value = config.databaseName || "";
        document.getElementById("dbUsername").value = config.username || "";
        document.getElementById("dbSslMode").value = config.sslMode || "Disable";
        document.getElementById("dbAutoInitialize").checked = config.autoInitialize !== false;
        document.getElementById("databaseConfigResult").textContent = config.sqlEnabled
            ? `Modo actual: SQL (${config.provider}).`
            : "Modo actual: JSON local. Guarda una configuracion SQL y reinicia el contenedor para usar la base externa.";
    } catch (error) {
        if (error.message === "FORBIDDEN") {
            document.getElementById("databaseConfigResult").textContent = "Tu rol no permite ver esta configuracion.";
        }
    }
}

async function loadDashboard() {
    const params = new URLSearchParams({
        rangeDays: document.getElementById("rangeDays").value,
        careerId: document.getElementById("careerFilter").value,
        semesterId: document.getElementById("semesterFilter").value,
        status: document.getElementById("statusFilter").value
    });

    for (const [key, value] of [...params.entries()]) {
        if (!value) {
            params.delete(key);
        }
    }

    state.dashboard = await fetchJson(`/api/dashboard?${params.toString()}`);
    renderDashboard(state.dashboard);
}

function populateSelects(snapshot) {
    fillOptions(document.getElementById("careerFilter"), snapshot.careers, "Todas las carreras");
    fillOptions(document.getElementById("semesterFilter"), snapshot.semesters, "Todos los semestres");
    fillOptions(document.getElementById("userCareer"), snapshot.careers, "Sin carrera");
    fillOptions(document.getElementById("userSemester"), snapshot.semesters, "Sin semestre");
}

function fillOptions(select, items, placeholder) {
    const currentValue = select.value;
    select.innerHTML = "";

    const empty = document.createElement("option");
    empty.value = "";
    empty.textContent = placeholder;
    select.appendChild(empty);

    items.forEach(item => {
        const option = document.createElement("option");
        option.value = item.id;
        option.textContent = item.name;
        select.appendChild(option);
    });

    if ([...select.options].some(option => option.value === currentValue)) {
        select.value = currentValue;
    }
}

function renderDashboard(dashboard) {
    renderKpis(dashboard.kpis);
    renderBars("equipmentStatusChart", dashboard.equipmentStatus, "equipos");
    renderBars("usageByCareerChart", dashboard.usageByCareer, "horas");
    renderTrend("dailyUsageTrend", dashboard.dailyUsageTrend);
    renderComputers(dashboard.computerCards);
}

function renderKpis(kpis) {
    const cards = [
        ["Usuarios activos", `${kpis.activeUsers}/${kpis.totalUsers}`],
        ["Equipos disponibles", kpis.availableComputers],
        ["Equipos en uso", kpis.inUseComputers],
        ["Equipos deshabilitados", kpis.disabledComputers],
        ["Horas en rango", kpis.hoursInRange]
    ];

    document.getElementById("kpiCards").innerHTML = cards.map(([label, value]) => `
        <div class="stat-card">
            <span>${label}</span>
            <strong>${value}</strong>
        </div>
    `).join("");
}

function renderBars(hostId, items, suffix) {
    const host = document.getElementById(hostId);
    const max = Math.max(...items.map(item => item.value), 1);
    host.innerHTML = items.length
        ? items.map(item => `
            <div class="bar-row">
                <strong>${item.label}</strong>
                <div class="bar-track">
                    <div class="bar-fill" style="width:${(item.value / max) * 100}%"></div>
                </div>
                <span>${item.value} ${suffix}</span>
            </div>
        `).join("")
        : `<p class="muted">No hay datos para los filtros seleccionados.</p>`;
}

function renderTrend(hostId, items) {
    const host = document.getElementById(hostId);
    const max = Math.max(...items.map(item => item.hours), 1);
    host.innerHTML = items.map(item => `
        <div class="trend-column">
            <div class="trend-bar-wrap">
                <div class="trend-bar" style="height:${Math.max((item.hours / max) * 100, 4)}%"></div>
            </div>
            <strong>${item.hours}</strong>
            <span class="trend-label">${item.label}</span>
        </div>
    `).join("");
}

function renderComputers(items) {
    const host = document.getElementById("computerGrid");
    host.innerHTML = items.map(item => {
        const statusClass = normalizeStatusClass(item.status);
        return `
            <article class="computer-card ${statusClass}">
                <span class="status-pill ${statusClass}">${item.status}</span>
                <h4>${item.name}</h4>
                <p><strong>Ubicacion:</strong> ${item.location}</p>
                <p><strong>Inventario:</strong> ${item.inventoryTag}</p>
                <p><strong>IP:</strong> ${item.ipAddress || "Sin IP"}</p>
                <p><strong>Usuario actual:</strong> ${item.currentUsername || "Sin asignar"}</p>
                <p><strong>Ultimo reporte:</strong> ${item.lastSeenLabel}</p>
            </article>
        `;
    }).join("");
}

function renderTables(snapshot) {
    const careersTable = createTable(
        ["Nombre", "Estado", "Acciones"],
        snapshot.careers.map(item => [
            item.name,
            item.active ? "Activa" : "Inactiva",
            tableActions(
                () => editCareer(item),
                () => deleteEntity(`/api/careers/${item.id}`)
            )
        ])
    );

    const semestersTable = createTable(
        ["Nombre", "Estado", "Acciones"],
        snapshot.semesters.map(item => [
            item.name,
            item.active ? "Activo" : "Inactivo",
            tableActions(
                () => editSemester(item),
                () => deleteEntity(`/api/semesters/${item.id}`)
            )
        ])
    );

    const usersTable = createTable(
        ["Usuario", "Nombre", "Carrera", "Semestre", "Hash", "Estado", "Acciones"],
        snapshot.users.map(item => [
            item.username,
            `${item.firstName} ${item.lastName}<br><span class="muted">${item.email}</span>`,
            getLookupName(snapshot.careers, item.careerId),
            getLookupName(snapshot.semesters, item.semesterId),
            item.hashMethod || "SIN DEFINIR",
            item.active ? "Activo" : "Inactivo",
            tableActions(
                () => editUser(item),
                () => deleteEntity(`/api/users/${item.id}`)
            )
        ])
    );

    const computersTable = createTable(
        ["Equipo", "Ubicacion", "Inventario", "IP", "Estado", "Acciones"],
        snapshot.computers.map(item => [
            item.name,
            item.location,
            item.inventoryTag,
            item.ipAddress || "Sin IP",
            translateStatus(item.status),
            tableActions(
                () => editComputer(item),
                () => deleteEntity(`/api/computers/${item.id}`)
            )
        ])
    );

    document.getElementById("careersTable").innerHTML = careersTable;
    document.getElementById("semestersTable").innerHTML = semestersTable;
    document.getElementById("usersTable").innerHTML = usersTable;
    document.getElementById("computersTable").innerHTML = computersTable;
}

function renderAudit(entries) {
    document.getElementById("auditTable").innerHTML = entries.length
        ? createTable(
            ["Fecha", "Actor", "Accion", "Entidad", "Detalle", "IP"],
            entries.map(item => [
                item.createdUtc ? formatAuditDate(item.createdUtc) : "",
                item.actorUsername,
                item.action,
                `${item.entityType}<br><span class="muted">${item.entityKey}</span>`,
                item.summary,
                item.remoteIp || "Sin IP"
            ])
        )
        : `<p class="muted">Todavia no hay eventos de auditoria.</p>`;
}

function createTable(headers, rows) {
    const thead = headers.map(header => `<th>${header}</th>`).join("");
    const tbody = rows.map(columns => `
        <tr>${columns.map(value => `<td>${value}</td>`).join("")}</tr>
    `).join("");
    return `<table><thead><tr>${thead}</tr></thead><tbody>${tbody}</tbody></table>`;
}

function tableActions(onEdit, onDelete) {
    const editId = `edit-${crypto.randomUUID()}`;
    const deleteId = `delete-${crypto.randomUUID()}`;
    queueMicrotask(() => {
        document.getElementById(editId)?.addEventListener("click", onEdit);
        document.getElementById(deleteId)?.addEventListener("click", onDelete);
    });
    return `
        <div class="table-actions">
            <button type="button" class="btn-muted" id="${editId}">Editar</button>
            <button type="button" class="btn-danger" id="${deleteId}">Eliminar</button>
        </div>
    `;
}

function editCareer(item) {
    document.getElementById("careerId").value = item.id;
    document.getElementById("careerName").value = item.name;
    document.getElementById("careerActive").checked = item.active;
}

function editSemester(item) {
    document.getElementById("semesterId").value = item.id;
    document.getElementById("semesterName").value = item.name;
    document.getElementById("semesterActive").checked = item.active;
}

function editUser(item) {
    document.getElementById("userId").value = item.id;
    document.getElementById("userUsername").value = item.username;
    document.getElementById("userDocument").value = item.documentId;
    document.getElementById("userFirstName").value = item.firstName;
    document.getElementById("userLastName").value = item.lastName;
    document.getElementById("userEmail").value = item.email;
    document.getElementById("userCareer").value = item.careerId || "";
    document.getElementById("userSemester").value = item.semesterId || "";
    document.getElementById("userHashMethod").value = item.hashMethod || "BCRYPT";
    document.getElementById("userPassword").value = "";
    document.getElementById("passwordActionResult").textContent = "";
    document.getElementById("userActive").checked = item.active;
}

function editComputer(item) {
    document.getElementById("computerId").value = item.id;
    document.getElementById("computerName").value = item.name;
    document.getElementById("computerInventory").value = item.inventoryTag;
    document.getElementById("computerLocation").value = item.location;
    document.getElementById("computerIpAddress").value = item.ipAddress || "";
    document.getElementById("computerStatus").value = item.status;
    document.getElementById("computerCurrentUser").value = item.currentUsername || "";
}

async function deleteEntity(url) {
    await fetchJson(url, { method: "DELETE" });
    await loadAll();
}

function getLookupName(items, id) {
    return items.find(item => item.id === id)?.name || "Sin asignar";
}

function translateStatus(status) {
    return {
        Available: "Disponible",
        InUse: "En uso",
        Disabled: "Deshabilitado"
    }[status] || status;
}

function normalizeStatusClass(status) {
    return status.toLowerCase().replaceAll(" ", "-");
}

function bindForms() {
    document.getElementById("loginForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("loginResult");
        resultHost.textContent = "Validando credenciales...";

        try {
            const session = await fetchJson("/api/auth/login", {
                method: "POST",
                body: JSON.stringify({
                    username: document.getElementById("loginUsername").value,
                    password: document.getElementById("loginPassword").value
                })
            });

            applySession(session);
            document.getElementById("loginPassword").value = "";
            resultHost.textContent = "";
            await loadAll();
        } catch (error) {
            if (error.message === "AUTH_REQUIRED") {
                resultHost.textContent = "Credenciales invalidas. Verifica el usuario y la clave.";
                return;
            }

            resultHost.textContent = "No fue posible iniciar sesion en este momento.";
        }
    });

    document.getElementById("logoutBtn").addEventListener("click", async () => {
        await fetch("/api/auth/logout", {
            method: "POST",
            credentials: "same-origin"
        });
        handleUnauthorized();
    });

    document.getElementById("dbProvider").addEventListener("change", () => {
        document.getElementById("dbPort").value = document.getElementById("dbProvider").value === "MySql" ? 3306 : 5432;
    });

    document.getElementById("testDatabaseBtn").addEventListener("click", async () => {
        const result = await fetchJson("/api/configuration/database/test", {
            method: "POST",
            body: JSON.stringify(getDatabaseConfigPayload())
        });
        document.getElementById("databaseConfigResult").textContent = result.message;
    });

    document.getElementById("databaseConfigForm").addEventListener("submit", async event => {
        event.preventDefault();
        const result = await fetchJson("/api/configuration/database", {
            method: "PUT",
            body: JSON.stringify(getDatabaseConfigPayload())
        });
        document.getElementById("databaseConfigResult").textContent = result.message;
    });

    document.getElementById("generatePasswordBtn").addEventListener("click", () => {
        document.getElementById("userPassword").value = generatePassword();
        document.getElementById("passwordActionResult").textContent = "Clave segura generada localmente. Guarda el usuario para aplicarla.";
    });

    document.getElementById("resetPasswordBtn").addEventListener("click", async () => {
        const id = document.getElementById("userId").value;
        if (!id) {
            document.getElementById("passwordActionResult").textContent = "Selecciona primero un usuario existente para restablecer su clave.";
            return;
        }

        const payload = {
            hashMethod: document.getElementById("userHashMethod").value,
            password: document.getElementById("userPassword").value,
            generate: !document.getElementById("userPassword").value
        };

        const result = await fetchJson(`/api/users/${id}/password`, {
            method: "POST",
            body: JSON.stringify(payload)
        });

        document.getElementById("userPassword").value = result.generatedPassword;
        document.getElementById("passwordActionResult").textContent =
            `Clave actualizada para ${result.username}. Algoritmo: ${result.hashMethod}. Nueva clave: ${result.generatedPassword}`;
        await loadAll();
    });

    document.getElementById("careerForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("careerId").value;
        const payload = {
            name: document.getElementById("careerName").value,
            active: document.getElementById("careerActive").checked
        };
        await fetchJson(id ? `/api/careers/${id}` : "/api/careers", { method: id ? "PUT" : "POST", body: JSON.stringify(payload) });
        event.target.reset();
        document.getElementById("careerId").value = "";
        await loadAll();
    });

    document.getElementById("semesterForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("semesterId").value;
        const payload = {
            name: document.getElementById("semesterName").value,
            active: document.getElementById("semesterActive").checked
        };
        await fetchJson(id ? `/api/semesters/${id}` : "/api/semesters", { method: id ? "PUT" : "POST", body: JSON.stringify(payload) });
        event.target.reset();
        document.getElementById("semesterId").value = "";
        await loadAll();
    });

    document.getElementById("userForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("userId").value;
        const payload = {
            username: document.getElementById("userUsername").value,
            documentId: document.getElementById("userDocument").value,
            firstName: document.getElementById("userFirstName").value,
            lastName: document.getElementById("userLastName").value,
            email: document.getElementById("userEmail").value,
            careerId: parseNullableInt(document.getElementById("userCareer").value),
            semesterId: parseNullableInt(document.getElementById("userSemester").value),
            hashMethod: document.getElementById("userHashMethod").value,
            password: document.getElementById("userPassword").value,
            active: document.getElementById("userActive").checked
        };
        await fetchJson(id ? `/api/users/${id}` : "/api/users", { method: id ? "PUT" : "POST", body: JSON.stringify(payload) });
        event.target.reset();
        document.getElementById("userId").value = "";
        await loadAll();
    });

    document.getElementById("computerForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("computerId").value;
        const payload = {
            name: document.getElementById("computerName").value,
            inventoryTag: document.getElementById("computerInventory").value,
            location: document.getElementById("computerLocation").value,
            ipAddress: document.getElementById("computerIpAddress").value,
            status: document.getElementById("computerStatus").value,
            currentUsername: document.getElementById("computerCurrentUser").value
        };
        await fetchJson(id ? `/api/computers/${id}` : "/api/computers", { method: id ? "PUT" : "POST", body: JSON.stringify(payload) });
        event.target.reset();
        document.getElementById("computerId").value = "";
        await loadAll();
    });

    document.getElementById("importForm").addEventListener("submit", async event => {
        event.preventDefault();
        const fileInput = document.getElementById("userFile");
        if (!fileInput.files.length) {
            return;
        }

        const formData = new FormData();
        formData.append("file", fileInput.files[0]);

        const response = await fetch("/api/import/users", {
            method: "POST",
            body: formData
        });
        const result = await response.json();
        document.getElementById("importResult").textContent =
            `Importados: ${result.imported}. Actualizados: ${result.updated}. ${result.warnings.length ? result.warnings.join(" | ") : "Sin advertencias."}`;
        event.target.reset();
        await loadAll();
    });

    ["rangeDays", "careerFilter", "semesterFilter", "statusFilter"].forEach(id => {
        document.getElementById(id).addEventListener("change", () => loadDashboard());
    });
}

function parseNullableInt(value) {
    return value ? Number(value) : null;
}

function getDatabaseConfigPayload() {
    return {
        provider: document.getElementById("dbProvider").value,
        host: document.getElementById("dbHost").value,
        port: Number(document.getElementById("dbPort").value),
        databaseName: document.getElementById("dbName").value,
        username: document.getElementById("dbUsername").value,
        password: document.getElementById("dbPassword").value,
        sslMode: document.getElementById("dbSslMode").value,
        autoInitialize: document.getElementById("dbAutoInitialize").checked
    };
}

function formatAuditDate(value) {
    return new Date(value).toLocaleString("es-CO", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit"
    });
}

function generatePassword(length = 14) {
    const upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    const lower = "abcdefghijkmnopqrstuvwxyz";
    const digits = "23456789";
    const symbols = "!@#$%*?";
    const all = upper + lower + digits + symbols;
    const required = [
        upper[Math.floor(Math.random() * upper.length)],
        lower[Math.floor(Math.random() * lower.length)],
        digits[Math.floor(Math.random() * digits.length)],
        symbols[Math.floor(Math.random() * symbols.length)]
    ];

    while (required.length < length) {
        required.push(all[Math.floor(Math.random() * all.length)]);
    }

    return required.sort(() => Math.random() - 0.5).join("");
}

function applySession(session) {
    state.session = session;
    const authShell = document.getElementById("authShell");
    const pageShell = document.querySelector(".page-shell");
    const sessionBadge = document.getElementById("sessionBadge");

    document.body.classList.toggle("auth-required", !session?.authenticated);
    authShell.classList.toggle("is-visible", !session?.authenticated);
    pageShell.classList.toggle("is-hidden", !session?.authenticated);
    sessionBadge.textContent = session?.authenticated
        ? `Sesion activa: ${session.username} (${session.role || "Sin rol"})`
        : "Sesion no iniciada";
}

function handleUnauthorized() {
    applySession(null);
    document.getElementById("loginResult").textContent = "Inicia sesion para continuar.";
}

async function initializeApp() {
    bindForms();

    try {
        const session = await fetchJson("/api/auth/me");
        applySession(session);
        await loadAll();
    } catch (error) {
        if (error.message !== "AUTH_REQUIRED") {
            document.getElementById("loginResult").textContent = "No fue posible validar la sesion actual.";
        }
    }
}

initializeApp();
