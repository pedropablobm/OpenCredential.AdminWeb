const state = {
    snapshot: null,
    dashboard: null,
    session: null,
    currentView: "overview",
    currentEquipmentTab: "list",
    currentAcademicTab: "careers",
    selectedRoomId: null,
    selectedRoomPositionId: null,
    layoutDraft: null,
    pagination: {
        careersTable: 1,
        semestersTable: 1,
        usersTable: 1,
        computersTable: 1,
        auditTable: 1
    },
    filters: {
        users: { query: "", status: "", careerId: "" },
        equipment: { query: "", status: "", location: "" },
        academic: { query: "" },
        audit: { query: "" }
    }
};

const pageSizeByTable = {
    careersTable: 8,
    semestersTable: 8,
    usersTable: 8,
    computersTable: 8,
    auditTable: 10
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
        throw new Error("FORBIDDEN");
    }

    if (!response.ok) {
        let detail = "";
        try {
            const errorPayload = await response.json();
            detail = errorPayload.detail || errorPayload.message || errorPayload.title || JSON.stringify(errorPayload);
        } catch {
            detail = await response.text();
        }
        throw new Error(detail || `Error ${response.status}`);
    }

    if (response.status === 204) {
        return null;
    }

    return response.json();
}

async function loadAll() {
    await loadDatabaseConfiguration();

    try {
        state.snapshot = await fetchJson("/api/summary");
    } catch (error) {
        showDataLoadError(error);
        state.snapshot = createEmptySnapshot();
    }

    try {
        state.dashboard = await fetchJson(`/api/dashboard?${buildDashboardQuery()}`);
    } catch (error) {
        showDataLoadError(error);
        state.dashboard = createEmptyDashboard();
    }

    populateSelects(state.snapshot);
    renderApp();
}

async function loadDatabaseConfiguration() {
    try {
        const config = await fetchJson("/api/configuration/database");
        document.getElementById("dbProvider").value = config.provider || "PostgreSql";
        document.getElementById("dbHost").value = config.host || "";
        document.getElementById("dbPort").value = config.port || (config.provider === "MySql" ? 3306 : 5432);
        document.getElementById("dbName").value = config.databaseName || "";
        document.getElementById("dbUsername").value = config.username || "";
        document.getElementById("dbPassword").value = "";
        document.getElementById("dbPassword").placeholder = config.passwordConfigured
            ? "Clave guardada. Escribela solo si quieres cambiarla"
            : "Clave de base de datos";
        document.getElementById("dbPassword").required = !config.passwordConfigured;
        document.getElementById("dbSslMode").value = config.sslMode || "Disable";
        document.getElementById("dbAutoInitialize").checked = config.autoInitialize !== false;
        document.getElementById("databaseConfigResult").textContent = config.sqlEnabled
            ? `Modo actual: SQL (${config.provider}).`
            : "Modo actual: JSON local. Guarda una configuracion SQL y reinicia el contenedor para usar la base externa.";
        document.getElementById("sidebarDatabaseMode").textContent = config.sqlEnabled
            ? `SQL ${config.provider}`
            : "JSON local";
    } catch (error) {
        if (error.message === "FORBIDDEN") {
            document.getElementById("databaseConfigResult").textContent = "Tu rol no permite ver esta configuracion.";
            return;
        }

        document.getElementById("databaseConfigResult").textContent = `No fue posible cargar la configuracion: ${error.message}`;
    }
}

function buildDashboardQuery() {
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

    return params.toString();
}

function populateSelects(snapshot) {
    fillOptions(document.getElementById("careerFilter"), snapshot.careers, "Todas las carreras");
    fillOptions(document.getElementById("semesterFilter"), snapshot.semesters, "Todos los semestres");
    fillOptions(document.getElementById("userCareer"), snapshot.careers, "Sin carrera");
    fillOptions(document.getElementById("userSemester"), snapshot.semesters, "Sin semestre");
    fillOptions(document.getElementById("userCareerFilter"), snapshot.careers, "Todas las carreras");
    fillOptions(document.getElementById("roomSelector"), snapshot.rooms, "Selecciona una sala");
    ensureSelectedRoom(snapshot.rooms);
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

function renderApp() {
    renderOverview();
    renderUsersModule();
    renderEquipmentModule();
    renderAcademicModule();
    renderAuditModule();
    updateSessionChrome();
    updateTopbarDate();
}

function renderOverview() {
    const kpis = state.dashboard?.kpis ?? createEmptyDashboard().kpis;
    renderKpiCards("overviewKpiCards", [
        { label: "Usuarios activos", value: `${kpis.activeUsers}/${kpis.totalUsers}`, note: "Estado de cuentas registradas", tone: "neutral" },
        { label: "Equipos disponibles", value: kpis.availableComputers, note: "Listos para uso en sala", tone: "success" },
        { label: "Ocupados", value: kpis.occupiedComputers, note: "Sesion activa confirmada", tone: "warning" },
        { label: "Bloqueados", value: kpis.lockedComputers, note: "Sesion bloqueada con presencia", tone: "info" },
        { label: "Desconectados", value: kpis.disconnectedComputers, note: "Sesion separada del cliente", tone: "slate" },
        { label: "Huerfanas", value: kpis.orphanedComputers, note: "Requieren revision operativa", tone: "danger" },
        { label: "Deshabilitados", value: kpis.disabledComputers, note: "Fuera de servicio", tone: "danger" }
    ]);

    renderBars("usageByCareerChart", state.dashboard?.usageByCareer ?? [], "horas");
    renderBars("equipmentStatusChart", state.dashboard?.operationalStatus ?? [], "equipos");
    renderTrend("dailyUsageTrend", state.dashboard?.dailyUsageTrend ?? []);
    renderOverviewEquipmentAlerts(
        state.dashboard?.sessionAlerts?.length
            ? state.dashboard.sessionAlerts.map(normalizeComputedComputer)
            : getOperationalComputers(state.snapshot)
    );
    renderOverviewAudit(state.snapshot?.auditEntries ?? []);
}

function renderUsersModule() {
    const snapshot = state.snapshot ?? createEmptySnapshot();
    const filters = state.filters.users;
    const users = snapshot.users
        .filter(user => !filters.query || user.username.toLowerCase().includes(filters.query) || user.documentId.toLowerCase().includes(filters.query) || user.email.toLowerCase().includes(filters.query))
        .filter(user => !filters.status || (filters.status === "active" ? user.active : !user.active))
        .filter(user => !filters.careerId || String(user.careerId ?? "") === filters.careerId);

    document.getElementById("usersSummary").textContent = `${snapshot.users.length} usuarios registrados | ${snapshot.users.filter(user => user.active).length} activos`;

    renderPaginatedTable(
        "usersTable",
        ["Usuario", "Nombre", "Carrera", "Semestre", "Hash", "Estado", "Acciones"],
        users.map(item => [
            item.username,
            `${item.firstName} ${item.lastName}<br><span class="support-text">${item.email}</span>`,
            getLookupName(snapshot.careers, item.careerId),
            getLookupName(snapshot.semesters, item.semesterId),
            item.hashMethod || "SIN DEFINIR",
            renderStatusTag(item.active ? "Activo" : "Inactivo"),
            tableActions(
                () => editUser(item),
                () => deleteEntity(`/api/users/${item.id}`)
            )
        ]),
        "No hay usuarios para los filtros seleccionados."
    );
}

function renderEquipmentModule() {
    const dashboard = state.dashboard ?? createEmptyDashboard();
    const snapshot = state.snapshot ?? createEmptySnapshot();
    const filters = state.filters.equipment;
    const operationalComputers = getOperationalComputers(snapshot);
    const inventoryById = new Map(snapshot.computers.map(item => [item.id, item]));
    const filteredComputers = operationalComputers
        .filter(computer => {
            if (!filters.query && !filters.location) {
                return true;
            }

            const haystack = [computer.name, computer.inventoryTag, computer.ipAddress || "", computer.location]
                .join(" ")
                .toLowerCase();
            return haystack.includes(filters.query) && haystack.includes(filters.location);
        })
        .filter(computer => matchesStatusFilter(computer, filters.status));

    renderKpiCards("equipmentModuleKpis", [
        { label: "Disponibles", value: dashboard.kpis.availableComputers, note: "Equipos listos", tone: "success" },
        { label: "Ocupados", value: dashboard.kpis.occupiedComputers, note: "Sesion activa", tone: "warning" },
        { label: "Bloqueados", value: dashboard.kpis.lockedComputers, note: "Sesion bloqueada", tone: "info" },
        { label: "Desconectados", value: dashboard.kpis.disconnectedComputers, note: "Sesion en pausa", tone: "slate" },
        { label: "Huerfanas", value: dashboard.kpis.orphanedComputers, note: "Sin heartbeat reciente", tone: "danger" },
        { label: "Deshabilitados", value: dashboard.kpis.disabledComputers, note: "No operativos", tone: "danger" }
    ]);

    renderPaginatedTable(
        "computersTable",
        ["Equipo", "Ubicacion", "Inventario", "IP", "Estado operativo", "Acciones"],
        filteredComputers.map(item => [
            item.name,
            item.location,
            item.inventoryTag,
            item.ipAddress || "Sin IP",
            renderStatusTag(item.statusLabel, item.statusKey),
            tableActions(
                () => editComputer(inventoryById.get(item.id) ?? item),
                () => deleteEntity(`/api/computers/${item.id}`)
            )
        ]),
        "No hay equipos para los filtros seleccionados."
    );

    renderRoomMap(snapshot, filteredComputers);
    renderEquipmentActivity(filteredComputers);
    syncEquipmentTabs();
}

function renderAcademicModule() {
    const snapshot = state.snapshot ?? createEmptySnapshot();
    const query = state.filters.academic.query;

    const careers = snapshot.careers.filter(item => !query || item.name.toLowerCase().includes(query));
    const semesters = snapshot.semesters.filter(item => !query || item.name.toLowerCase().includes(query));

    document.getElementById("academicActionBtn").textContent = state.currentAcademicTab === "careers"
        ? "Nueva carrera"
        : "Nuevo semestre";

    renderPaginatedTable(
        "careersTable",
        ["Nombre", "Estado", "Acciones"],
        careers.map(item => [
            item.name,
            renderStatusTag(item.active ? "Activa" : "Inactiva"),
            tableActions(
                () => editCareer(item),
                () => deleteEntity(`/api/careers/${item.id}`)
            )
        ]),
        "No hay carreras para mostrar."
    );

    renderPaginatedTable(
        "semestersTable",
        ["Nombre", "Estado", "Acciones"],
        semesters.map(item => [
            item.name,
            renderStatusTag(item.active ? "Activo" : "Inactivo"),
            tableActions(
                () => editSemester(item),
                () => deleteEntity(`/api/semesters/${item.id}`)
            )
        ]),
        "No hay semestres para mostrar."
    );

    syncAcademicTabs();
}

function renderAuditModule() {
    const query = state.filters.audit.query;
    const entries = (state.snapshot?.auditEntries ?? []).filter(item => {
        if (!query) {
            return true;
        }

        return [
            item.actorUsername,
            item.action,
            item.entityType,
            item.entityKey,
            item.summary,
            item.remoteIp || ""
        ].join(" ").toLowerCase().includes(query);
    });

    renderPaginatedTable(
        "auditTable",
        ["Fecha", "Actor", "Accion", "Entidad", "Detalle", "IP"],
        entries.map(item => [
            item.createdUtc ? formatAuditDate(item.createdUtc) : "",
            item.actorUsername,
            item.action,
            `${item.entityType}<br><span class="support-text">${item.entityKey}</span>`,
            item.summary,
            item.remoteIp || "Sin IP"
        ]),
        "Todavia no hay eventos de auditoria."
    );
}

function renderKpiCards(hostId, cards) {
    const host = document.getElementById(hostId);
    host.innerHTML = cards.map(card => `
        <article class="card kpi-card ${card.tone ? `kpi-${card.tone}` : ""}">
            <p class="kpi-title">${card.label}</p>
            <p class="kpi-value">${card.value}</p>
            <p class="kpi-note">${card.note}</p>
        </article>
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
        : `<p class="support-text">No hay datos para los filtros seleccionados.</p>`;
}

function renderTrend(hostId, items) {
    const host = document.getElementById(hostId);
    if (!items.length) {
        host.innerHTML = `<p class="support-text">No hay consumo en el rango seleccionado.</p>`;
        return;
    }

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

function renderOverviewEquipmentAlerts(computers) {
    const host = document.getElementById("overviewEquipmentAlerts");
    const active = [...computers]
        .filter(item => item.statusKey !== "Available")
        .sort((a, b) => new Date(b.lastSeenUtc || 0) - new Date(a.lastSeenUtc || 0))
        .slice(0, 5);

    host.innerHTML = active.length
        ? active.map(item => `
            <article class="activity-item">
                <div class="activity-main">
                    <span class="status-dot ${getStatusToneClass(item.statusKey)}"></span>
                    <div>
                        <strong>${item.name}</strong>
                        <div class="support-text">${item.location}</div>
                        <div class="support-text">${item.currentUsername || "Sin usuario actual"} · ${item.lastSeenUtc ? formatAuditDate(item.lastSeenUtc) : "Sin reporte"}</div>
                    </div>
                </div>
                ${renderStatusTag(item.statusLabel, item.statusKey)}
            </article>
        `).join("")
        : `<p class="support-text">No hay alertas ni actividad destacada en este momento.</p>`;
}

function renderOverviewAudit(entries) {
    const host = document.getElementById("overviewAuditPreview");
    const latest = [...entries].slice(0, 5);
    host.innerHTML = latest.length
        ? latest.map(item => `
            <article class="activity-item">
                <div class="activity-main">
                    <span class="status-dot success"></span>
                    <div>
                        <strong>${item.action}</strong>
                        <div class="support-text">${item.actorUsername} · ${item.entityType}</div>
                        <div class="support-text">${item.summary}</div>
                    </div>
                </div>
                <span class="support-text">${item.createdUtc ? formatAuditDate(item.createdUtc) : ""}</span>
            </article>
        `).join("")
        : `<p class="support-text">Todavia no hay eventos de auditoria.</p>`;
}

function renderEquipmentActivity(computers) {
    const host = document.getElementById("equipmentActivityList");
    const recent = [...computers]
        .sort((a, b) => new Date(b.lastSeenUtc || 0) - new Date(a.lastSeenUtc || 0))
        .slice(0, 8);

    host.innerHTML = recent.length
        ? recent.map(item => `
            <article class="activity-item">
                <div class="activity-main">
                    <span class="status-dot ${getStatusToneClass(item.statusKey)}"></span>
                    <div>
                        <strong>${item.name}</strong>
                        <div class="support-text">${item.location} · ${item.ipAddress || "Sin IP"}</div>
                        <div class="support-text">${item.currentUsername || "Sin usuario actual"}${item.sessionState ? ` · ${translateStatus(item.sessionState)}` : ""}</div>
                    </div>
                </div>
                <div class="activity-side">
                    ${renderStatusTag(item.statusLabel, item.statusKey)}
                    <span class="support-text">${item.lastSeenUtc ? formatAuditDate(item.lastSeenUtc) : ""}</span>
                </div>
            </article>
        `).join("")
        : `<p class="support-text">No hay actividad reciente de equipos.</p>`;
}

function renderRoomMap(snapshot, filteredComputers) {
    const selector = document.getElementById("roomSelector");
    const tabsHost = document.getElementById("roomTabs");
    const layoutHost = document.getElementById("roomLayoutGrid");
    const detailHost = document.getElementById("roomDetailPanel");
    const rooms = snapshot.rooms ?? [];
    const items = snapshot.roomLayoutItems ?? [];
    const visibleComputerIds = new Set(filteredComputers.map(item => item.id));

    selector.disabled = rooms.length === 0;
    document.getElementById("editRoomBtn").disabled = !state.selectedRoomId;
    document.getElementById("editLayoutBtn").disabled = !state.selectedRoomId;

    if (!rooms.length) {
        tabsHost.innerHTML = "";
        layoutHost.innerHTML = `<p class="support-text">Todavia no hay salas configuradas. Crea una sala para construir el mapa visual.</p>`;
        detailHost.innerHTML = `<p class="support-text">Cuando registres una sala, aqui podras ver cada puesto y su estado operativo.</p>`;
        return;
    }

    const room = rooms.find(item => item.id === state.selectedRoomId) ?? rooms[0];
    if (!room) {
        return;
    }

    state.selectedRoomId = room.id;
    selector.value = String(room.id);
    tabsHost.innerHTML = rooms.map(item => `
        <button
            type="button"
            class="room-tab ${item.id === room.id ? "is-active" : ""}"
            data-room-tab="${item.id}">
            ${escapeHtml(item.code || item.name)}
        </button>
    `).join("");

    tabsHost.querySelectorAll("[data-room-tab]").forEach(button => {
        button.addEventListener("click", () => {
            state.selectedRoomId = Number(button.dataset.roomTab);
            state.selectedRoomPositionId = null;
            renderEquipmentModule();
        });
    });

    const roomItems = items
        .filter(item => item.roomId === room.id)
        .filter(item => item.itemType !== "Table")
        .filter(item => !item.computerId || visibleComputerIds.has(item.computerId));
    const computerById = new Map(getOperationalComputers(snapshot).map(item => [item.id, item]));
    const activeItem = roomItems.find(item => item.id === state.selectedRoomPositionId)
        ?? roomItems.find(item => item.computerId && visibleComputerIds.has(item.computerId))
        ?? roomItems[0]
        ?? null;
    state.selectedRoomPositionId = activeItem?.id ?? null;

    layoutHost.style.width = `${room.canvasWidth}px`;
    layoutHost.style.height = `${room.canvasHeight}px`;
    layoutHost.innerHTML = roomItems
        .map(item => {
        const computer = item.computerId ? computerById.get(item.computerId) : null;
        const typeClass = getRoomItemTypeClass(item.itemType);
        const statusClass = deriveLayoutItemStatusClass(item, computer);
        return `
            <button
                type="button"
                class="room-cell ${typeClass} ${statusClass} ${state.selectedRoomPositionId === item.id ? "is-selected" : ""}"
                data-room-cell="${item.id}"
                style="left:${item.x}px; top:${item.y}px; width:${item.width}px; height:${item.height}px; z-index:${item.id + 1};">
                ${renderRoomVisualContent(item, computer, false)}
            </button>
        `;
    }).join("");

    layoutHost.querySelectorAll("[data-room-cell]").forEach(button => {
        button.addEventListener("click", () => {
            state.selectedRoomPositionId = Number(button.dataset.roomCell);
            renderRoomMap(snapshot, filteredComputers);
        });
    });

    if (!activeItem) {
        detailHost.innerHTML = `
            <div class="detail-card">
                <p class="section-label">Sala ${room.code}</p>
                <h4>${room.name}</h4>
                <p class="support-text">Esta sala no tiene elementos configurados todavia. Usa "Configurar mapa visual" para definir la distribucion flotante.</p>
            </div>
        `;
        return;
    }

    const computer = activeItem.computerId ? computerById.get(activeItem.computerId) : null;
    detailHost.innerHTML = `
        <div class="detail-card">
            <p class="section-label">Elemento seleccionado</p>
            <h4>${escapeHtml(activeItem.label)}</h4>
            <p><strong>Sala:</strong> ${room.name}</p>
            <p><strong>Tipo:</strong> ${getRoomItemTypeLabel(activeItem.itemType)}</p>
            <p><strong>Posicion:</strong> X ${activeItem.x}, Y ${activeItem.y}</p>
            <p><strong>Tamano:</strong> ${activeItem.width} × ${activeItem.height}</p>
            <p><strong>Equipo:</strong> ${computer?.name || "Sin equipo asignado"}</p>
            <p><strong>Inventario:</strong> ${computer?.inventoryTag || "No aplica"}</p>
            <p><strong>IP:</strong> ${computer?.ipAddress || "Sin IP"}</p>
            <p><strong>Usuario actual:</strong> ${computer?.currentUsername || "Sin asignar"}</p>
            <p><strong>Estado:</strong> ${computer ? renderStatusTag(computer.statusLabel, computer.statusKey) : renderStatusTag(deriveLayoutItemStatusLabel(activeItem, computer))}</p>
            <p><strong>Sesion:</strong> ${computer?.sessionState ? translateStatus(computer.sessionState) : "Sin sesion"}</p>
            <p><strong>Heartbeat:</strong> ${computer?.lastHeartbeatAt ? formatAuditDate(computer.lastHeartbeatAt) : "Sin heartbeat"}</p>
            <p><strong>Ultimo reporte:</strong> ${computer?.lastSeenUtc ? formatAuditDate(computer.lastSeenUtc) : "Sin dato"}</p>
        </div>
    `;
}

function renderLayoutEditor() {
    const host = document.getElementById("layoutEditorCanvas");
    const snapshot = state.snapshot ?? createEmptySnapshot();
    if (!state.layoutDraft) {
        host.innerHTML = "";
        return;
    }

    host.style.width = `${state.layoutDraft.canvasWidth}px`;
    host.style.height = `${state.layoutDraft.canvasHeight}px`;
    host.innerHTML = state.layoutDraft.items.map(item => {
        const computer = item.computerId ? getOperationalComputers(snapshot).find(entry => entry.id === Number(item.computerId)) : null;
        return `
            <button
                type="button"
                class="layout-canvas-item ${getRoomItemTypeClass(item.itemType)} ${deriveLayoutItemStatusClass(item, computer)} ${state.selectedRoomPositionId === item.id ? "is-selected" : ""}"
                data-layout-item="${item.id}"
                style="left:${item.x}px; top:${item.y}px; width:${item.width}px; height:${item.height}px; z-index:${item.id + 10};">
                ${renderRoomVisualContent(item, computer, true)}
            </button>
        `;
    }).join("");

    host.querySelectorAll("[data-layout-item]").forEach(button => {
        button.addEventListener("click", () => setSelectedLayoutItem(Number(button.dataset.layoutItem)));
        bindLayoutDrag(button);
    });

    syncLayoutInspector();
}

function syncLayoutInspector() {
    const emptyState = document.getElementById("layoutInspectorEmpty");
    const form = document.getElementById("layoutInspectorForm");
    if (!state.layoutDraft) {
        emptyState.hidden = false;
        form.hidden = true;
        return;
    }

    const item = state.layoutDraft.items.find(entry => entry.id === state.selectedRoomPositionId);
    if (!item) {
        emptyState.hidden = false;
        form.hidden = true;
        return;
    }

    emptyState.hidden = true;
    form.hidden = false;
    document.getElementById("layoutItemLabel").value = item.label;
    document.getElementById("layoutItemType").value = item.itemType;
    document.getElementById("layoutItemComputer").innerHTML = buildComputerOptions(state.snapshot?.computers ?? [], item.computerId, item.id);
    document.getElementById("layoutItemComputer").disabled = item.itemType !== "Computer" && item.itemType !== "TeacherDesk";
    document.getElementById("layoutItemX").value = item.x;
    document.getElementById("layoutItemY").value = item.y;
    document.getElementById("layoutItemWidth").value = item.width;
    document.getElementById("layoutItemHeight").value = item.height;
}

function bindLayoutDrag(element) {
    element.addEventListener("pointerdown", event => {
        event.preventDefault();
        const id = Number(element.dataset.layoutItem);
        setSelectedLayoutItem(id);
        const item = state.layoutDraft?.items.find(entry => entry.id === id);
        if (!item) {
            return;
        }

        const startX = event.clientX;
        const startY = event.clientY;
        const originX = item.x;
        const originY = item.y;
        const canvasRect = document.getElementById("layoutEditorCanvas").getBoundingClientRect();

        const move = moveEvent => {
            const deltaX = snapCoordinate(moveEvent.clientX - startX);
            const deltaY = snapCoordinate(moveEvent.clientY - startY);
            const nextX = clamp(originX + deltaX, 0, Math.max(0, state.layoutDraft.canvasWidth - item.width));
            const nextY = clamp(originY + deltaY, 0, Math.max(0, state.layoutDraft.canvasHeight - item.height));
            item.x = nextX;
            item.y = nextY;
            element.style.left = `${nextX}px`;
            element.style.top = `${nextY}px`;
            document.getElementById("layoutItemX").value = nextX;
            document.getElementById("layoutItemY").value = nextY;
            element.classList.add("is-dragging");
        };

        const up = () => {
            document.removeEventListener("pointermove", move);
            document.removeEventListener("pointerup", up);
            element.classList.remove("is-dragging");
            renderLayoutEditor();
        };

        if (event.clientX < canvasRect.left || event.clientX > canvasRect.right || event.clientY < canvasRect.top || event.clientY > canvasRect.bottom) {
            return;
        }

        document.addEventListener("pointermove", move);
        document.addEventListener("pointerup", up, { once: true });
    });
}

function setSelectedLayoutItem(id) {
    state.selectedRoomPositionId = id;
    renderLayoutEditor();
}

function updateDraftItem(id, changes, rerender = true) {
    const item = state.layoutDraft?.items.find(entry => entry.id === id);
    if (!item) {
        return;
    }

    Object.assign(item, changes);
    if (item.itemType !== "Computer" && item.itemType !== "TeacherDesk") {
        item.computerId = "";
    }

    if (rerender) {
        renderLayoutEditor();
    }
}

function addLayoutItem(itemType) {
    if (!state.layoutDraft) {
        return;
    }

    const nextId = Math.max(0, ...state.layoutDraft.items.map(item => item.id || 0)) + 1;
    const itemCount = state.layoutDraft.items.length;
    const offsetX = 40 + (itemCount % 5) * 40;
    const offsetY = 40 + Math.floor(itemCount / 5) * 30;
    const defaults = {
        Computer: { label: `Equipo ${nextId}`, width: 120, height: 110 },
        EmptySpace: { label: `Espacio ${nextId}`, width: 160, height: 100 },
        TeacherDesk: { label: `Docente ${nextId}`, width: 160, height: 120 },
        Reference: { label: `Referencia ${nextId}`, width: 140, height: 90 }
    }[itemType];
    const nextX = clamp(snapCoordinate(offsetX), 0, Math.max(0, state.layoutDraft.canvasWidth - defaults.width));
    const nextY = clamp(snapCoordinate(offsetY), 0, Math.max(0, state.layoutDraft.canvasHeight - defaults.height));
    setLayoutResult("");

    state.layoutDraft.items.push({
        id: nextId,
        label: defaults.label,
        itemType,
        x: nextX,
        y: nextY,
        width: defaults.width,
        height: defaults.height,
        orientation: "Horizontal",
        capacity: 1,
        computerId: ""
    });
    state.selectedRoomPositionId = nextId;
    renderLayoutEditor();
}

function deleteSelectedLayoutItem() {
    if (!state.layoutDraft || !state.selectedRoomPositionId) {
        return;
    }

    state.layoutDraft.items = state.layoutDraft.items.filter(item => item.id !== state.selectedRoomPositionId);
    state.selectedRoomPositionId = state.layoutDraft.items[0]?.id ?? null;
    renderLayoutEditor();
}

function getRoomItemTypeClass(itemType) {
    return {
        Computer: "computer",
        EmptySpace: "empty-space",
        TeacherDesk: "teacher-desk",
        Reference: "reference"
    }[itemType] || "reference";
}

function getRoomItemTypeLabel(itemType) {
    return {
        Computer: "Puesto libre",
        EmptySpace: "Espacio sin equipo",
        TeacherDesk: "Puesto docente",
        Reference: "Referencia"
    }[itemType] || "Referencia";
}

function deriveLayoutItemStatusClass(item, computer) {
    if (item.itemType === "EmptySpace") {
        return "empty-space";
    }

    if (item.itemType === "TeacherDesk" && !computer) {
        return "teacher-desk";
    }

    if (!computer) {
        return item.itemType === "Reference" ? "reference" : "available";
    }

    return normalizeStatusClass(computer.statusKey);
}

function deriveLayoutItemStatusLabel(item, computer) {
    if (computer) {
        return computer.statusLabel;
    }

    return {
        EmptySpace: "Sin computador",
        TeacherDesk: "Sin equipo docente",
        Reference: "Elemento de referencia",
        Computer: "Disponible para asignacion"
    }[item.itemType] || "Sin estado";
}

function renderRoomVisualContent(item, computer, isEditor = false) {
    const statusClass = deriveLayoutItemStatusClass(item, computer);
    const statusLabel = deriveLayoutItemStatusLabel(item, computer);
    const title = computer?.name || item.label || getRoomItemTypeLabel(item.itemType);
    const subtitle = item.itemType === "Computer"
        ? (computer?.inventoryTag || statusLabel)
        : item.label;
    const shellClass = getVisualShellClass(item.itemType, isEditor);

    return `
        <div class="visual-shell ${shellClass}">
            <div class="visual-icon ${getRoomItemTypeClass(item.itemType)} ${statusClass}">
                <span class="visual-state-badge ${statusClass}" aria-hidden="true"></span>
                ${renderRoomItemGlyph(item.itemType)}
            </div>
            <div class="visual-copy">
                <strong title="${escapeHtml(title)}">${escapeHtml(compactVisualText(title, isEditor ? 18 : 22))}</strong>
                <span title="${escapeHtml(subtitle)}">${escapeHtml(compactVisualText(formatVisualSubtitle(item, subtitle), isEditor ? 18 : 20))}</span>
            </div>
        </div>
    `;
}

function renderRoomItemGlyph(itemType) {
    switch (itemType) {
        case "TeacherDesk":
            return `<span class="glyph glyph-desk"></span>`;
        case "EmptySpace":
            return `<span class="glyph glyph-space"></span>`;
        case "Reference":
            return `<span class="glyph glyph-reference"></span>`;
        default:
            return `<span class="glyph glyph-monitor"></span>`;
    }
}

function compactVisualText(value, maxLength) {
    const normalized = String(value || "").trim();
    if (normalized.length <= maxLength) {
        return normalized;
    }

    return `${normalized.slice(0, Math.max(0, maxLength - 1))}…`;
}

function formatVisualSubtitle(item, fallback) {
    if (item.itemType === "EmptySpace") {
        return "Pasillo / zona libre";
    }

    if (item.itemType === "TeacherDesk") {
        return "Frente del aula";
    }

    return fallback;
}

function getVisualShellClass(itemType, isEditor) {
    if (itemType === "EmptySpace") {
        return isEditor ? "visual-shell-space-editor" : "visual-shell-space";
    }

    if (itemType === "TeacherDesk") {
        return "visual-shell-desk";
    }

    return isEditor ? "visual-shell-editor" : "visual-shell-computer";
}

function buildLayoutDraft(room, items) {
    return {
        roomId: room.id,
        canvasWidth: room.canvasWidth || 1200,
        canvasHeight: room.canvasHeight || 720,
        items: items
            .filter(item => item.itemType !== "Table")
            .map(item => ({
            id: item.id,
            label: item.label,
            itemType: item.itemType,
            x: item.x,
            y: item.y,
            width: item.width,
            height: item.height,
            orientation: "Horizontal",
            capacity: 1,
            computerId: item.computerId ? String(item.computerId) : ""
        }))
    };
}

function findDuplicateAssignedComputerIds(items) {
    const seen = new Set();
    const duplicates = new Set();
    items.forEach(item => {
        if (!item.computerId) {
            return;
        }

        const key = String(item.computerId);
        if (seen.has(key)) {
            duplicates.add(key);
            return;
        }

        seen.add(key);
    });

    return duplicates;
}

function normalizeLayoutOrientation(value) {
    return String(value || "").toLowerCase() === "vertical" ? "Vertical" : "Horizontal";
}

function normalizeLayoutCapacity(value) {
    const parsed = Number(value);
    if (Number.isNaN(parsed)) {
        return 1;
    }

    return Math.min(6, Math.max(1, parsed));
}

function setLayoutResult(message) {
    const host = document.getElementById("layoutFormResult");
    if (host) {
        host.textContent = message;
    }
}

function resizeLayoutCanvas() {
    if (!state.layoutDraft) {
        return;
    }

    state.layoutDraft.canvasWidth = Math.max(640, Number(document.getElementById("layoutCanvasWidth").value));
    state.layoutDraft.canvasHeight = Math.max(360, Number(document.getElementById("layoutCanvasHeight").value));
    state.layoutDraft.items = state.layoutDraft.items.map(item => ({
        ...item,
        x: clamp(item.x, 0, Math.max(0, state.layoutDraft.canvasWidth - item.width)),
        y: clamp(item.y, 0, Math.max(0, state.layoutDraft.canvasHeight - item.height))
    }));
    renderLayoutEditor();
}

function bindLayoutInspector() {
    [
        ["layoutItemLabel", "label", value => value],
        ["layoutItemType", "itemType", value => value],
        ["layoutItemComputer", "computerId", value => value],
        ["layoutItemX", "x", value => snapCoordinate(Math.max(0, Number(value)))],
        ["layoutItemY", "y", value => snapCoordinate(Math.max(0, Number(value)))],
        ["layoutItemWidth", "width", value => snapCoordinate(Math.max(40, Number(value)))],
        ["layoutItemHeight", "height", value => snapCoordinate(Math.max(40, Number(value)))]
    ].forEach(([id, field, mapValue]) => {
        document.getElementById(id).addEventListener("input", event => {
            if (!state.selectedRoomPositionId) {
                return;
            }

            const value = mapValue(event.target.value);
            updateDraftItem(state.selectedRoomPositionId, { [field]: value });
        });
        document.getElementById(id).addEventListener("change", event => {
            if (!state.selectedRoomPositionId) {
                return;
            }

            const value = mapValue(event.target.value);
            updateDraftItem(state.selectedRoomPositionId, { [field]: value });
        });
    });
}

function snapCoordinate(value) {
    return Math.round(value / 20) * 20;
}

function clamp(value, min, max) {
    return Math.min(Math.max(value, min), max);
}

function renderPaginatedTable(hostId, headers, rows, emptyMessage = "No hay registros para mostrar.") {
    const host = document.getElementById(hostId);
    const pageSize = pageSizeByTable[hostId] || 8;
    const totalPages = Math.max(1, Math.ceil(rows.length / pageSize));
    const currentPage = Math.min(state.pagination[hostId] || 1, totalPages);
    state.pagination[hostId] = currentPage;

    if (!rows.length) {
        host.innerHTML = `<p class="support-text">${emptyMessage}</p>`;
        return;
    }

    const start = (currentPage - 1) * pageSize;
    const pageRows = rows.slice(start, start + pageSize);
    host.innerHTML = `
        ${createTable(headers, pageRows)}
        ${createPagination(hostId, rows.length, currentPage, totalPages, start, pageRows.length)}
    `;
    bindPagination(hostId);
}

function createPagination(hostId, totalRows, currentPage, totalPages, start, visibleRows) {
    const first = start + 1;
    const last = start + visibleRows;
    return `
        <div class="pagination" data-table="${hostId}">
            <span>Mostrando ${first}-${last} de ${totalRows}</span>
            <div class="pagination-actions">
                <button type="button" class="btn btn-secondary" data-page="prev" ${currentPage === 1 ? "disabled" : ""}>Anterior</button>
                <strong>Pagina ${currentPage} de ${totalPages}</strong>
                <button type="button" class="btn btn-secondary" data-page="next" ${currentPage === totalPages ? "disabled" : ""}>Siguiente</button>
            </div>
        </div>
    `;
}

function bindPagination(hostId) {
    document.querySelectorAll(`[data-table="${hostId}"] button[data-page]`).forEach(button => {
        button.addEventListener("click", () => {
            state.pagination[hostId] += button.dataset.page === "next" ? 1 : -1;
            renderApp();
        });
    });
}

function createTable(headers, rows) {
    const thead = headers.map(header => `<th>${header}</th>`).join("");
    const tbody = rows.map(columns => `
        <tr>${columns.map(value => `<td>${value}</td>`).join("")}</tr>
    `).join("");
    return `<table><thead><tr>${thead}</tr></thead><tbody>${tbody}</tbody></table>`;
}

function createClientId(prefix) {
    if (globalThis.crypto?.randomUUID) {
        return `${prefix}-${globalThis.crypto.randomUUID()}`;
    }

    return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(36).slice(2)}`;
}

function tableActions(onEdit, onDelete) {
    const editId = createClientId("edit");
    const deleteId = createClientId("delete");
    queueMicrotask(() => {
        document.getElementById(editId)?.addEventListener("click", onEdit);
        document.getElementById(deleteId)?.addEventListener("click", onDelete);
    });
    return `
        <div class="table-actions">
            <button type="button" class="btn btn-secondary" id="${editId}">Editar</button>
            <button type="button" class="btn btn-secondary" id="${deleteId}">Eliminar</button>
        </div>
    `;
}

function renderStatusTag(label, statusKey = "") {
    const normalizedStatus = normalizeStatusKey(statusKey || label);
    const className = {
        Available: "tag-green",
        Occupied: "tag-amber",
        Locked: "tag-blue",
        Disconnected: "tag-slate",
        Orphaned: "tag-red",
        Disabled: "tag-red",
        Active: "tag-green",
        Inactive: "tag-red",
        InUse: "tag-amber"
    }[normalizedStatus] || "tag-green";

    return `<span class="tag ${className}">${label}</span>`;
}

function getLookupName(items, id) {
    return items.find(item => item.id === id)?.name || "Sin asignar";
}

function translateStatus(status) {
    return {
        Available: "Disponible",
        InUse: "En uso",
        Occupied: "Ocupado",
        Locked: "Bloqueado",
        Disconnected: "Desconectado",
        Orphaned: "Sesion huerfana",
        Disabled: "Deshabilitado",
        active: "Activa",
        locked: "Bloqueada",
        disconnected: "Desconectada",
        ended: "Finalizada",
        Active: "Activo",
        Inactive: "Inactivo"
    }[status] || status;
}

function normalizeStatusClass(status) {
    return {
        Available: "available",
        InUse: "in-use",
        Occupied: "occupied",
        Locked: "locked",
        Disconnected: "disconnected",
        Orphaned: "orphaned",
        Disabled: "disabled",
        "Disponible": "available",
        "En uso": "in-use",
        "Ocupado": "occupied",
        "Bloqueado": "locked",
        "Desconectado": "disconnected",
        "Sesion huerfana": "orphaned",
        "Deshabilitado": "disabled"
    }[status] || status.toLowerCase().replaceAll(" ", "-");
}

function normalizeStatusKey(status) {
    return {
        available: "Available",
        disponible: "Available",
        inuse: "InUse",
        enuso: "InUse",
        occupied: "Occupied",
        ocupado: "Occupied",
        locked: "Locked",
        bloqueado: "Locked",
        disconnected: "Disconnected",
        desconectado: "Disconnected",
        orphaned: "Orphaned",
        sesionhuerfana: "Orphaned",
        disabled: "Disabled",
        deshabilitado: "Disabled",
        active: "Active",
        activo: "Active",
        inactive: "Inactive",
        inactivo: "Inactive"
    }[String(status || "").replaceAll(" ", "").toLowerCase()] || String(status || "");
}

function getStatusToneClass(status) {
    return {
        Available: "success",
        Occupied: "warning",
        Locked: "info",
        Disconnected: "slate",
        Orphaned: "danger",
        Disabled: "danger",
        InUse: "warning"
    }[normalizeStatusKey(status)] || "success";
}

function getOperationalComputers(snapshot) {
    if (snapshot?.computedComputers?.length) {
        return snapshot.computedComputers.map(normalizeComputedComputer);
    }

    return (snapshot?.computers ?? []).map(normalizeLegacyComputer);
}

function normalizeComputedComputer(item) {
    const statusKey = normalizeStatusKey(item.operationalStatus || item.status || "Available");
    return {
        id: item.computerId,
        name: item.computerName,
        location: item.location,
        inventoryTag: item.inventoryTag,
        ipAddress: item.ipAddress,
        currentUsername: item.sessionUsername,
        lastSeenUtc: item.lastSeenUtc,
        lastHeartbeatAt: item.lastHeartbeatAt,
        loginStamp: item.loginStamp,
        logoutStamp: item.logoutStamp,
        sessionState: item.sessionState,
        sessionEndReason: item.sessionEndReason,
        heartbeatAgeSeconds: item.heartbeatAgeSeconds,
        isOrphaned: item.isOrphaned,
        administrativeStatus: item.administrativeStatus,
        statusKey,
        statusLabel: item.operationalStatusLabel || translateStatus(statusKey)
    };
}

function normalizeLegacyComputer(item) {
    const statusKey = item.status === "InUse" ? "Occupied" : normalizeStatusKey(item.status || "Available");
    return {
        id: item.id,
        name: item.name,
        location: item.location,
        inventoryTag: item.inventoryTag,
        ipAddress: item.ipAddress,
        currentUsername: item.currentUsername,
        lastSeenUtc: item.lastSeenUtc,
        lastHeartbeatAt: item.lastSeenUtc,
        loginStamp: null,
        logoutStamp: null,
        sessionState: null,
        sessionEndReason: null,
        heartbeatAgeSeconds: null,
        isOrphaned: false,
        administrativeStatus: item.status,
        statusKey,
        statusLabel: translateStatus(statusKey)
    };
}

function matchesStatusFilter(computer, filterValue) {
    if (!filterValue) {
        return true;
    }

    const normalizedFilter = normalizeStatusKey(filterValue);
    return computer.statusKey === normalizedFilter || normalizeStatusKey(computer.administrativeStatus) === normalizedFilter;
}

function showDataLoadError(error) {
    const resultHost = document.getElementById("databaseConfigResult");
    if (!resultHost || error.message === "AUTH_REQUIRED" || error.message === "FORBIDDEN") {
        return;
    }

    resultHost.textContent = `La configuracion esta guardada, pero no fue posible cargar datos desde la base: ${error.message}`;
}

function createEmptySnapshot() {
    return {
        careers: [],
        semesters: [],
        users: [],
        computers: [],
        computedComputers: [],
        rooms: [],
        roomLayoutItems: [],
        usageRecords: [],
        auditEntries: []
    };
}

function createEmptyDashboard() {
    return {
        kpis: {
            totalUsers: 0,
            activeUsers: 0,
            availableComputers: 0,
            inUseComputers: 0,
            occupiedComputers: 0,
            lockedComputers: 0,
            disconnectedComputers: 0,
            orphanedComputers: 0,
            disabledComputers: 0,
            hoursInRange: 0
        },
        equipmentStatus: [
            { label: "Disponible", value: 0 },
            { label: "En uso", value: 0 },
            { label: "Deshabilitado", value: 0 }
        ],
        operationalStatus: [],
        usageByCareer: [],
        dailyUsageTrend: [],
        computerCards: [],
        sessionAlerts: []
    };
}

function updateSessionChrome() {
    const role = state.session?.role || "Sin rol";
    const username = state.session?.username || "No iniciado";
    const sessionText = state.session?.authenticated
        ? `Sesion activa: ${username} (${role})`
        : "Sesion no iniciada";

    document.getElementById("sessionBadge").textContent = sessionText;
    document.getElementById("sidebarSession").textContent = `${username} / ${role}`;
}

function updateTopbarDate() {
    document.getElementById("topbarDate").textContent = new Date().toLocaleDateString("es-CO", {
        day: "2-digit",
        month: "long",
        year: "numeric"
    });
}

function applySession(session) {
    state.session = session;
    const authShell = document.getElementById("authShell");
    const pageShell = document.querySelector(".page-shell");

    document.body.classList.toggle("auth-required", !session?.authenticated);
    authShell.classList.toggle("is-visible", !session?.authenticated);
    pageShell.classList.toggle("is-hidden", !session?.authenticated);
    updateSessionChrome();
}

function handleUnauthorized() {
    applySession(null);
    document.getElementById("loginResult").textContent = "Inicia sesion para continuar.";
}

function openDrawer(drawerId) {
    document.getElementById("drawerBackdrop").hidden = false;
    document.querySelectorAll(".drawer").forEach(drawer => {
        drawer.classList.toggle("is-open", drawer.id === drawerId);
        drawer.setAttribute("aria-hidden", drawer.id === drawerId ? "false" : "true");
    });
}

function closeDrawers() {
    document.getElementById("drawerBackdrop").hidden = true;
    document.querySelectorAll(".drawer").forEach(drawer => {
        drawer.classList.remove("is-open");
        drawer.setAttribute("aria-hidden", "true");
    });
}

function resetUserForm() {
    document.getElementById("userForm").reset();
    document.getElementById("userId").value = "";
    document.getElementById("userPassword").value = "";
    document.getElementById("passwordActionResult").textContent = "";
    document.getElementById("userDrawerTitle").textContent = "Nuevo usuario";
}

function resetComputerForm() {
    document.getElementById("computerForm").reset();
    document.getElementById("computerId").value = "";
    document.getElementById("computerDrawerTitle").textContent = "Nuevo equipo";
}

function resetRoomForm() {
    document.getElementById("roomForm").reset();
    document.getElementById("roomId").value = "";
    document.getElementById("roomCanvasWidth").value = 1200;
    document.getElementById("roomCanvasHeight").value = 720;
    document.getElementById("roomDrawerTitle").textContent = "Nueva sala";
}

function resetCareerForm() {
    document.getElementById("careerForm").reset();
    document.getElementById("careerId").value = "";
    document.getElementById("careerDrawerTitle").textContent = "Nueva carrera";
}

function resetSemesterForm() {
    document.getElementById("semesterForm").reset();
    document.getElementById("semesterId").value = "";
    document.getElementById("semesterDrawerTitle").textContent = "Nuevo semestre";
}

function editRoom(item) {
    document.getElementById("roomDrawerTitle").textContent = "Editar sala";
    document.getElementById("roomId").value = item.id;
    document.getElementById("roomName").value = item.name;
    document.getElementById("roomCode").value = item.code;
    document.getElementById("roomCanvasWidth").value = item.canvasWidth;
    document.getElementById("roomCanvasHeight").value = item.canvasHeight;
    document.getElementById("roomActive").checked = item.active;
    openDrawer("roomDrawer");
}

function openLayoutEditor() {
    const snapshot = state.snapshot ?? createEmptySnapshot();
    const room = snapshot.rooms.find(item => item.id === state.selectedRoomId);
    if (!room) {
        return;
    }

    document.getElementById("layoutDrawerTitle").textContent = `Configurar mapa visual · ${room.name}`;
    document.getElementById("layoutRoomId").value = room.id;
    document.getElementById("layoutCanvasWidth").value = room.canvasWidth || 1200;
    document.getElementById("layoutCanvasHeight").value = room.canvasHeight || 720;
    state.layoutDraft = buildLayoutDraft(room, snapshot.roomLayoutItems.filter(item => item.roomId === room.id));
    renderLayoutEditor();
    openDrawer("layoutDrawer");
}

function editUser(item) {
    document.getElementById("userDrawerTitle").textContent = "Editar usuario";
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
    openDrawer("userDrawer");
}

function editComputer(item) {
    document.getElementById("computerDrawerTitle").textContent = "Editar equipo";
    document.getElementById("computerId").value = item.id;
    document.getElementById("computerName").value = item.name;
    document.getElementById("computerInventory").value = item.inventoryTag;
    document.getElementById("computerLocation").value = item.location;
    document.getElementById("computerIpAddress").value = item.ipAddress || "";
    document.getElementById("computerStatus").value = item.status;
    document.getElementById("computerCurrentUser").value = item.currentUsername || "";
    openDrawer("computerDrawer");
}

function editCareer(item) {
    document.getElementById("careerDrawerTitle").textContent = "Editar carrera";
    document.getElementById("careerId").value = item.id;
    document.getElementById("careerName").value = item.name;
    document.getElementById("careerActive").checked = item.active;
    openDrawer("careerDrawer");
}

function editSemester(item) {
    document.getElementById("semesterDrawerTitle").textContent = "Editar semestre";
    document.getElementById("semesterId").value = item.id;
    document.getElementById("semesterName").value = item.name;
    document.getElementById("semesterActive").checked = item.active;
    openDrawer("semesterDrawer");
}

function ensureSelectedRoom(rooms) {
    if (!rooms.length) {
        state.selectedRoomId = null;
        state.selectedRoomPositionId = null;
        return;
    }

    const exists = rooms.some(item => item.id === state.selectedRoomId);
    if (!exists) {
        state.selectedRoomId = rooms[0].id;
        state.selectedRoomPositionId = null;
    }
}

function buildComputerOptions(computers, selectedValue, currentLayoutItemId = null) {
    const current = selectedValue ? String(selectedValue) : "";
    const assignedComputerIds = new Set(
        (state.layoutDraft?.items ?? [])
            .filter(item => item.id !== currentLayoutItemId)
            .map(item => item.computerId ? String(item.computerId) : "")
            .filter(Boolean)
    );
    const options = [`<option value="">Sin equipo asignado</option>`];
    computers.forEach(computer => {
        if (assignedComputerIds.has(String(computer.id)) && String(computer.id) !== current) {
            return;
        }
        options.push(`<option value="${computer.id}" ${String(computer.id) === current ? "selected" : ""}>${computer.name} · ${computer.location}</option>`);
    });
    return options.join("");
}

async function deleteEntity(url) {
    await fetchJson(url, { method: "DELETE" });
    await loadAll();
}

function syncViewState() {
    document.querySelectorAll(".module-view").forEach(view => {
        view.classList.toggle("is-active", view.id === `${state.currentView}View`);
    });
    document.querySelectorAll(".nav-item[data-view]").forEach(button => {
        button.classList.toggle("is-active", button.dataset.view === state.currentView);
    });
    updateGlobalSearchPlaceholder();
}

function syncEquipmentTabs() {
    document.querySelectorAll("[data-equipment-tab]").forEach(button => {
        button.classList.toggle("is-active", button.dataset.equipmentTab === state.currentEquipmentTab);
    });
    document.querySelectorAll("#equipmentView .tab-panel").forEach(panel => {
        panel.classList.toggle("is-active", panel.id === `equipment${capitalize(state.currentEquipmentTab)}Panel`);
    });
}

function syncAcademicTabs() {
    document.querySelectorAll("[data-academic-tab]").forEach(button => {
        button.classList.toggle("is-active", button.dataset.academicTab === state.currentAcademicTab);
    });
    document.querySelectorAll("#academicView .tab-panel").forEach(panel => {
        panel.classList.toggle("is-active", panel.id === `${state.currentAcademicTab}Panel`);
    });
}

function updateGlobalSearchPlaceholder() {
    const input = document.getElementById("globalSearch");
    const mappings = {
        overview: "Buscar usuario, equipo o accion...",
        users: "Buscar usuario, documento o correo...",
        equipment: "Buscar equipo, inventario o IP...",
        academic: "Buscar carrera o semestre...",
        import: "Buscar acciones de importacion...",
        audit: "Buscar actor, accion, entidad o IP...",
        configuration: "Buscar host, usuario o ajuste..."
    };

    input.placeholder = mappings[state.currentView] || mappings.overview;
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
            resultHost.textContent = error.message === "AUTH_REQUIRED"
                ? "Credenciales invalidas. Verifica el usuario y la clave."
                : "No fue posible iniciar sesion en este momento.";
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
        const form = document.getElementById("databaseConfigForm");
        const resultHost = document.getElementById("databaseConfigResult");
        if (!form.reportValidity()) {
            return;
        }

        resultHost.textContent = "Probando conexion...";
        try {
            const result = await fetchJson("/api/configuration/database/test", {
                method: "POST",
                body: JSON.stringify(getDatabaseConfigPayload())
            });
            resultHost.textContent = result.message;
        } catch (error) {
            resultHost.textContent = `No fue posible probar la conexion: ${error.message}`;
        }
    });

    document.getElementById("applySchemaBtn").addEventListener("click", async () => {
        const form = document.getElementById("databaseConfigForm");
        const resultHost = document.getElementById("databaseConfigResult");
        if (!form.reportValidity()) {
            return;
        }

        resultHost.textContent = "Ajustando tablas auxiliares de AdminWeb...";
        try {
            const result = await fetchJson("/api/configuration/database/schema", {
                method: "POST",
                body: JSON.stringify(getDatabaseConfigPayload())
            });
            resultHost.textContent = result.message;
        } catch (error) {
            resultHost.textContent = `No fue posible ajustar las tablas: ${error.message}`;
        }
    });

    document.getElementById("databaseConfigForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("databaseConfigResult");
        resultHost.textContent = "Guardando configuracion...";
        try {
            const result = await fetchJson("/api/configuration/database", {
                method: "PUT",
                body: JSON.stringify(getDatabaseConfigPayload())
            });
            resultHost.textContent = result.requiresRestart
                ? `${result.message} Ejecuta: docker restart opencredential-adminweb`
                : result.message;
            document.getElementById("dbPassword").placeholder = "Clave guardada. Escribela solo si quieres cambiarla";
            document.getElementById("dbPassword").required = false;
        } catch (error) {
            resultHost.textContent = `No fue posible guardar la configuracion: ${error.message}`;
        }
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
        await fetchJson(id ? `/api/careers/${id}` : "/api/careers", {
            method: id ? "PUT" : "POST",
            body: JSON.stringify(payload)
        });
        resetCareerForm();
        closeDrawers();
        await loadAll();
    });

    document.getElementById("semesterForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("semesterId").value;
        const payload = {
            name: document.getElementById("semesterName").value,
            active: document.getElementById("semesterActive").checked
        };
        await fetchJson(id ? `/api/semesters/${id}` : "/api/semesters", {
            method: id ? "PUT" : "POST",
            body: JSON.stringify(payload)
        });
        resetSemesterForm();
        closeDrawers();
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
        await fetchJson(id ? `/api/users/${id}` : "/api/users", {
            method: id ? "PUT" : "POST",
            body: JSON.stringify(payload)
        });
        resetUserForm();
        closeDrawers();
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
        await fetchJson(id ? `/api/computers/${id}` : "/api/computers", {
            method: id ? "PUT" : "POST",
            body: JSON.stringify(payload)
        });
        resetComputerForm();
        closeDrawers();
        await loadAll();
    });

    document.getElementById("roomForm").addEventListener("submit", async event => {
        event.preventDefault();
        const id = document.getElementById("roomId").value;
        const payload = {
            name: document.getElementById("roomName").value,
            code: document.getElementById("roomCode").value,
            canvasWidth: Number(document.getElementById("roomCanvasWidth").value),
            canvasHeight: Number(document.getElementById("roomCanvasHeight").value),
            active: document.getElementById("roomActive").checked
        };
        const room = await fetchJson(id ? `/api/rooms/${id}` : "/api/rooms", {
            method: id ? "PUT" : "POST",
            body: JSON.stringify(payload)
        });
        state.selectedRoomId = room.id;
        resetRoomForm();
        closeDrawers();
        await loadAll();
    });

    document.getElementById("layoutForm").addEventListener("submit", async event => {
        event.preventDefault();
        if (!state.layoutDraft) {
            return;
        }

        const resultHost = document.getElementById("layoutFormResult");
        const duplicateIds = findDuplicateAssignedComputerIds(state.layoutDraft.items);
        if (duplicateIds.size) {
            const computersById = new Map((state.snapshot?.computers ?? []).map(item => [String(item.id), item]));
            const duplicateNames = Array.from(duplicateIds).map(id => computersById.get(id)?.name || `ID ${id}`);
            resultHost.textContent = `No se puede guardar el mapa visual porque el mismo equipo esta asignado en mas de un puesto: ${duplicateNames.join(", ")}.`;
            return;
        }

        resultHost.textContent = "Guardando mapa visual...";
        const roomId = Number(document.getElementById("layoutRoomId").value);
        const payload = {
            canvasWidth: Number(document.getElementById("layoutCanvasWidth").value),
            canvasHeight: Number(document.getElementById("layoutCanvasHeight").value),
                items: state.layoutDraft.items.map(item => ({
                    label: item.label.trim(),
                    itemType: item.itemType,
                    x: item.x,
                    y: item.y,
                    width: item.width,
                    height: item.height,
                    orientation: normalizeLayoutOrientation(item.orientation),
                    capacity: normalizeLayoutCapacity(item.capacity),
                    computerId: parseNullableInt(item.computerId)
                }))
        };
        try {
            await fetchJson(`/api/rooms/${roomId}/layout`, {
                method: "PUT",
                body: JSON.stringify(payload)
            });
            resultHost.textContent = "Mapa visual guardado correctamente.";
            closeDrawers();
            await loadAll();
        } catch (error) {
            resultHost.textContent = `No fue posible guardar el mapa visual: ${error.message}`;
        }
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

    document.getElementById("rangeDays").addEventListener("change", loadAll);
    document.getElementById("careerFilter").addEventListener("change", loadAll);
    document.getElementById("semesterFilter").addEventListener("change", loadAll);
    document.getElementById("statusFilter").addEventListener("change", loadAll);

    document.getElementById("userSearch").addEventListener("input", event => {
        state.filters.users.query = event.target.value.trim().toLowerCase();
        state.pagination.usersTable = 1;
        renderUsersModule();
    });
    document.getElementById("userStatusFilter").addEventListener("change", event => {
        state.filters.users.status = event.target.value;
        state.pagination.usersTable = 1;
        renderUsersModule();
    });
    document.getElementById("userCareerFilter").addEventListener("change", event => {
        state.filters.users.careerId = event.target.value;
        state.pagination.usersTable = 1;
        renderUsersModule();
    });

    document.getElementById("equipmentSearch").addEventListener("input", event => {
        state.filters.equipment.query = event.target.value.trim().toLowerCase();
        state.pagination.computersTable = 1;
        renderEquipmentModule();
    });
    document.getElementById("equipmentStatusFilter").addEventListener("change", event => {
        state.filters.equipment.status = event.target.value;
        state.pagination.computersTable = 1;
        renderEquipmentModule();
    });
    document.getElementById("equipmentLocationSearch").addEventListener("input", event => {
        state.filters.equipment.location = event.target.value.trim().toLowerCase();
        state.pagination.computersTable = 1;
        renderEquipmentModule();
    });
    document.getElementById("roomSelector").addEventListener("change", event => {
        state.selectedRoomId = parseNullableInt(event.target.value);
        state.selectedRoomPositionId = null;
        renderEquipmentModule();
    });

    document.getElementById("academicSearch").addEventListener("input", event => {
        state.filters.academic.query = event.target.value.trim().toLowerCase();
        state.pagination.careersTable = 1;
        state.pagination.semestersTable = 1;
        renderAcademicModule();
    });

    document.getElementById("auditSearch").addEventListener("input", event => {
        state.filters.audit.query = event.target.value.trim().toLowerCase();
        state.pagination.auditTable = 1;
        renderAuditModule();
    });

    document.getElementById("globalSearch").addEventListener("input", event => {
        const value = event.target.value.trim().toLowerCase();
        if (state.currentView === "users") {
            state.filters.users.query = value;
            document.getElementById("userSearch").value = event.target.value;
            renderUsersModule();
        } else if (state.currentView === "equipment") {
            state.filters.equipment.query = value;
            document.getElementById("equipmentSearch").value = event.target.value;
            renderEquipmentModule();
        } else if (state.currentView === "academic") {
            state.filters.academic.query = value;
            document.getElementById("academicSearch").value = event.target.value;
            renderAcademicModule();
        } else if (state.currentView === "audit") {
            state.filters.audit.query = value;
            document.getElementById("auditSearch").value = event.target.value;
            renderAuditModule();
        }
    });
}

function bindNavigation() {
    document.querySelectorAll("[data-view]").forEach(button => {
        button.addEventListener("click", () => {
            state.currentView = button.dataset.view;
            syncViewState();
        });
    });

    document.querySelectorAll("[data-equipment-tab]").forEach(button => {
        button.addEventListener("click", () => {
            state.currentEquipmentTab = button.dataset.equipmentTab;
            syncEquipmentTabs();
        });
    });

    document.querySelectorAll("[data-academic-tab]").forEach(button => {
        button.addEventListener("click", () => {
            state.currentAcademicTab = button.dataset.academicTab;
            renderAcademicModule();
        });
    });

    document.getElementById("quickNewUserBtn").addEventListener("click", () => {
        state.currentView = "users";
        syncViewState();
        resetUserForm();
        openDrawer("userDrawer");
    });

    document.getElementById("quickNewComputerBtn").addEventListener("click", () => {
        state.currentView = "equipment";
        syncViewState();
        resetComputerForm();
        openDrawer("computerDrawer");
    });

    document.getElementById("quickImportBtn").addEventListener("click", () => {
        state.currentView = "import";
        syncViewState();
    });

    document.getElementById("newUserBtn").addEventListener("click", () => {
        resetUserForm();
        openDrawer("userDrawer");
    });

    document.getElementById("newComputerBtn").addEventListener("click", () => {
        resetComputerForm();
        openDrawer("computerDrawer");
    });

    document.getElementById("newRoomBtn").addEventListener("click", () => {
        resetRoomForm();
        openDrawer("roomDrawer");
    });

    document.getElementById("editRoomBtn").addEventListener("click", () => {
        const room = (state.snapshot?.rooms ?? []).find(item => item.id === state.selectedRoomId);
        if (!room) {
            return;
        }
        editRoom(room);
    });

    document.getElementById("editLayoutBtn").addEventListener("click", openLayoutEditor);
    document.getElementById("resizeLayoutCanvasBtn").addEventListener("click", resizeLayoutCanvas);
    document.getElementById("addComputerItemBtn").addEventListener("click", () => addLayoutItem("Computer"));
    document.getElementById("addEmptySpaceBtn").addEventListener("click", () => addLayoutItem("EmptySpace"));
    document.getElementById("addTeacherDeskBtn").addEventListener("click", () => addLayoutItem("TeacherDesk"));
    document.getElementById("deleteLayoutItemBtn").addEventListener("click", deleteSelectedLayoutItem);

    document.getElementById("academicActionBtn").addEventListener("click", () => {
        if (state.currentAcademicTab === "careers") {
            resetCareerForm();
            openDrawer("careerDrawer");
        } else {
            resetSemesterForm();
            openDrawer("semesterDrawer");
        }
    });

    document.querySelectorAll("[data-close-drawer]").forEach(button => {
        button.addEventListener("click", closeDrawers);
    });

    document.getElementById("drawerBackdrop").addEventListener("click", closeDrawers);
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

function capitalize(value) {
    return value.charAt(0).toUpperCase() + value.slice(1);
}

function escapeHtml(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("\"", "&quot;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");
}

async function initializeApp() {
    bindForms();
    bindNavigation();
    bindLayoutInspector();
    syncViewState();

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
