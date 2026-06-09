const portalState = {
    session: null,
    profile: null,
    sessions: [],
    currentView: "profile",
    currentAuthView: "login"
};

async function portalFetchJson(url, options) {
    const response = await fetch(url, {
        headers: { "Content-Type": "application/json" },
        credentials: "same-origin",
        ...options
    });

    if (response.status === 401) {
        applyPortalSession(null);
        throw new Error("AUTH_REQUIRED");
    }

    if (!response.ok) {
        let detail = "";
        const raw = await response.text();
        if (raw) {
            try {
                const payload = JSON.parse(raw);
                detail = payload.detail || payload.message || payload.title || JSON.stringify(payload);
            } catch {
                detail = raw;
            }
        }
        throw new Error(detail || `Error ${response.status}`);
    }

    if (response.status === 204) {
        return null;
    }

    const raw = await response.text();
    return raw ? JSON.parse(raw) : null;
}

function applyPortalSession(session) {
    portalState.session = session;
    const isAuthenticated = !!session?.authenticated;
    document.getElementById("portalAuthShell").classList.toggle("is-visible", !isAuthenticated);
    document.getElementById("portalShell").classList.toggle("is-hidden", !isAuthenticated);
    document.getElementById("portalSessionBadge").textContent = isAuthenticated
        ? `${session.username} · portal`
        : "Sesion no iniciada";
}

async function loadPortalData() {
    portalState.profile = await portalFetchJson("/api/portal/me");
    try {
        portalState.sessions = await portalFetchJson("/api/portal/me/sessions?take=25");
    } catch (error) {
        portalState.sessions = [];
        const host = document.getElementById("portalSessionsTable");
        if (host) {
            host.innerHTML = `<p class="support-text">No fue posible cargar el historial de accesos: ${escapePortalHtml(error.message)}.</p>`;
        }
    }
    renderPortal();
}

function renderPortal() {
    renderPortalProfile();
    renderPortalSessions();
    syncPortalViews();
}

function renderPortalProfile() {
    const profile = portalState.profile;
    if (!profile) {
        return;
    }

    document.getElementById("portalHeroName").textContent = profile.fullName || profile.username;
    document.getElementById("portalHeroCopy").textContent = `Bienvenido, ${profile.firstName || profile.username}. Aqui puedes actualizar tus datos, cambiar tu clave y consultar tus accesos recientes.`;
    document.getElementById("portalHeroGroups").innerHTML = renderGroupBadges(profile.groups);

    document.getElementById("portalProfileUsername").value = profile.username || "";
    document.getElementById("portalProfileDocument").value = profile.documentId || "";
    document.getElementById("portalProfileFirstName").value = profile.firstName || "";
    document.getElementById("portalProfileLastName").value = profile.lastName || "";
    document.getElementById("portalProfileEmail").value = profile.email || "";
    document.getElementById("portalProfileCareer").textContent = profile.careerName || "Sin carrera";
    document.getElementById("portalProfileSemester").textContent = profile.semesterName || "Sin semestre";
    document.getElementById("portalProfileStatus").textContent = profile.active ? "Activo" : "Inactivo";
    document.getElementById("portalProfileGroups").innerHTML = renderGroupBadges(profile.groups);
}

function renderPortalSessions() {
    const host = document.getElementById("portalSessionsTable");
    const sessions = portalState.sessions || [];
    if (!sessions.length) {
        host.innerHTML = `<p class="support-text">Todavia no hay accesos registrados para este usuario.</p>`;
        return;
    }

    const rows = sessions.map(item => `
        <tr>
            <td>${formatPortalDate(item.loginStamp)}</td>
            <td>${escapePortalHtml(item.machine || "Sin equipo")}</td>
            <td>${escapePortalHtml(item.roomName || "Sin sala")}</td>
            <td>${renderPortalTag(item.originLabel || translatePortalOrigin(item.sessionOrigin), "neutral")}</td>
            <td>${renderPortalTag(item.sessionStateLabel || translatePortalState(item.sessionState), "slate")}</td>
            <td>${renderPortalTag(item.operationalStatusLabel || item.operationalStatus || "Disponible", portalToneFromOperationalStatus(item.operationalStatus))}</td>
            <td>${item.logoutStamp ? formatPortalDate(item.logoutStamp) : "Sesion abierta"}</td>
            <td>${formatPortalHours(item.durationHours)}</td>
        </tr>
    `).join("");

    host.innerHTML = `
        <div class="table-frame">
            <table class="data-table">
                <thead>
                    <tr>
                        <th>Inicio</th>
                        <th>Equipo</th>
                        <th>Sala</th>
                        <th>Modo de acceso</th>
                        <th>Sesion</th>
                        <th>Estado</th>
                        <th>Cierre</th>
                        <th>Horas</th>
                    </tr>
                </thead>
                <tbody>${rows}</tbody>
            </table>
        </div>
    `;
}

function renderGroupBadges(groups) {
    if (!groups?.length) {
        return `<span class="support-text">Sin grupos asignados</span>`;
    }

    return groups
        .map(group => `<span class="group-badge">${escapePortalHtml(group.name || group)}</span>`)
        .join("");
}

function renderPortalTag(label, tone) {
    return `<span class="tag ${tone ? `tag-${tone}` : ""}">${escapePortalHtml(label)}</span>`;
}

function portalToneFromOperationalStatus(status) {
    return {
        Available: "success",
        Occupied: "warning",
        Locked: "info",
        Disconnected: "slate",
        Orphaned: "danger",
        Disabled: "danger"
    }[String(status || "")] || "neutral";
}

function syncPortalViews() {
    document.querySelectorAll(".portal-view").forEach(view => {
        view.classList.toggle("is-active", view.id === `portal${capitalizePortal(portalState.currentView)}View`);
    });
    document.querySelectorAll("[data-portal-view]").forEach(button => {
        button.classList.toggle("is-active", button.dataset.portalView === portalState.currentView);
    });
}

function syncPortalAuthViews() {
    document.querySelectorAll(".portal-auth-form").forEach(form => {
        const activeFormId = {
            login: "portalLoginForm",
            recover: "portalRecoveryForm",
            reset: "portalResetForm"
        }[portalState.currentAuthView] || "portalLoginForm";
        const isActive = form.id === activeFormId;
        form.hidden = !isActive;
        form.classList.toggle("is-active", isActive);
    });
    document.querySelectorAll("[data-portal-auth-view]").forEach(button => {
        button.classList.toggle("is-active", button.dataset.portalAuthView === portalState.currentAuthView);
    });
}

function bindPortal() {
    document.getElementById("portalLoginForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("portalAuthResult");
        resultHost.textContent = "Validando credenciales...";

        try {
            const session = await portalFetchJson("/api/portal/auth/login", {
                method: "POST",
                body: JSON.stringify({
                    username: document.getElementById("portalLoginUsername").value,
                    password: document.getElementById("portalLoginPassword").value
                })
            });
            applyPortalSession(session);
            document.getElementById("portalLoginPassword").value = "";
            resultHost.textContent = "";
            await loadPortalData();
        } catch (error) {
            resultHost.textContent = error.message === "AUTH_REQUIRED"
                ? "Credenciales invalidas o sin permisos para el portal."
                : `No fue posible iniciar sesion: ${error.message}`;
        }
    });

    document.getElementById("portalRecoveryForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("portalAuthResult");
        resultHost.textContent = "Validando datos para recuperacion...";

        try {
            const result = await portalFetchJson("/api/portal/auth/recover", {
                method: "POST",
                body: JSON.stringify({
                    username: document.getElementById("portalRecoveryUsername").value,
                    documentId: document.getElementById("portalRecoveryDocument").value,
                    email: document.getElementById("portalRecoveryEmail").value
                })
            });
            resultHost.textContent = result.success
                ? result.resetToken
                    ? `${result.message} Token: ${result.resetToken}. Expira: ${result.expiresAtUtc ? formatPortalDate(result.expiresAtUtc) : "N/D"}. ${result.deliveryHint || ""}`
                    : `${result.message} ${result.deliveryHint || "Si la consola esta en modo produccion, el token puede quedar oculto por seguridad."}`
                : result.message;
            if (result.success && result.resetToken) {
                document.getElementById("portalResetToken").value = result.resetToken;
                portalState.currentAuthView = "reset";
                syncPortalAuthViews();
            }
        } catch (error) {
            resultHost.textContent = `No fue posible recuperar la clave: ${error.message}`;
        }
    });

    document.getElementById("portalResetForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("portalAuthResult");
        resultHost.textContent = "Restableciendo clave...";

        try {
            const result = await portalFetchJson("/api/portal/auth/reset", {
                method: "POST",
                body: JSON.stringify({
                    token: document.getElementById("portalResetToken").value,
                    newPassword: document.getElementById("portalResetNewPassword").value,
                    confirmPassword: document.getElementById("portalResetConfirmPassword").value
                })
            });
            document.getElementById("portalResetNewPassword").value = "";
            document.getElementById("portalResetConfirmPassword").value = "";
            resultHost.textContent = `${result.message} Ya puedes iniciar sesion con tu nueva clave.`;
            portalState.currentAuthView = "login";
            syncPortalAuthViews();
        } catch (error) {
            resultHost.textContent = `No fue posible restablecer la clave: ${error.message}`;
        }
    });

    document.getElementById("portalLogoutBtn").addEventListener("click", async () => {
        await fetch("/api/portal/auth/logout", {
            method: "POST",
            credentials: "same-origin"
        });
        applyPortalSession(null);
        document.getElementById("portalAuthResult").textContent = "Sesion cerrada.";
    });

    document.getElementById("portalProfileForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("portalProfileResult");
        resultHost.textContent = "Guardando cambios...";

        try {
            portalState.profile = await portalFetchJson("/api/portal/me", {
                method: "PUT",
                body: JSON.stringify({
                    firstName: document.getElementById("portalProfileFirstName").value,
                    lastName: document.getElementById("portalProfileLastName").value,
                    email: document.getElementById("portalProfileEmail").value
                })
            });
            resultHost.textContent = "Tus datos se actualizaron correctamente.";
            renderPortalProfile();
        } catch (error) {
            resultHost.textContent = `No fue posible actualizar tu perfil: ${error.message}`;
        }
    });

    document.getElementById("portalPasswordForm").addEventListener("submit", async event => {
        event.preventDefault();
        const resultHost = document.getElementById("portalPasswordResult");
        resultHost.textContent = "Actualizando clave...";

        try {
            const result = await portalFetchJson("/api/portal/me/password", {
                method: "POST",
                body: JSON.stringify({
                    currentPassword: document.getElementById("portalCurrentPassword").value,
                    newPassword: document.getElementById("portalNewPassword").value,
                    confirmPassword: document.getElementById("portalConfirmPassword").value
                })
            });
            document.getElementById("portalCurrentPassword").value = "";
            document.getElementById("portalNewPassword").value = "";
            document.getElementById("portalConfirmPassword").value = "";
            resultHost.textContent = result.message || "Clave actualizada correctamente.";
        } catch (error) {
            resultHost.textContent = `No fue posible actualizar la clave: ${error.message}`;
        }
    });

    document.querySelectorAll("[data-portal-view]").forEach(button => {
        button.addEventListener("click", () => {
            portalState.currentView = button.dataset.portalView;
            syncPortalViews();
        });
    });

    document.querySelectorAll("[data-portal-auth-view]").forEach(button => {
        button.addEventListener("click", () => {
            portalState.currentAuthView = button.dataset.portalAuthView;
            syncPortalAuthViews();
            document.getElementById("portalAuthResult").textContent = portalState.currentAuthView === "login"
                ? "Inicia sesion para continuar."
                : portalState.currentAuthView === "recover"
                    ? "Completa tus datos para solicitar un token temporal."
                    : "Pega el token y define tu nueva clave.";
        });
    });
}

async function initializePortal() {
    bindPortal();
    syncPortalAuthViews();
    syncPortalViews();

    try {
        const session = await portalFetchJson("/api/portal/auth/me");
        applyPortalSession(session);
        await loadPortalData();
    } catch (error) {
        if (error.message !== "AUTH_REQUIRED") {
            document.getElementById("portalAuthResult").textContent = `No fue posible validar tu sesion: ${error.message}`;
        }
    }
}

function formatPortalDate(value) {
    return new Date(value).toLocaleString("es-CO", {
        year: "numeric",
        month: "2-digit",
        day: "2-digit",
        hour: "2-digit",
        minute: "2-digit"
    });
}

function formatPortalHours(value) {
    return Number(value || 0).toFixed(2);
}

function translatePortalOrigin(origin) {
    return {
        online: "Con conexion",
        offline_cache: "Sin conexion (sincronizado)"
    }[String(origin || "").toLowerCase()] || (origin ? origin : "Registro anterior");
}

function translatePortalState(sessionState) {
    return {
        active: "Activa",
        locked: "Bloqueada",
        disconnected: "Desconectada",
        ended: "Finalizada"
    }[String(sessionState || "").toLowerCase()] || sessionState || "Sin estado";
}

function capitalizePortal(value) {
    return value.charAt(0).toUpperCase() + value.slice(1);
}

function escapePortalHtml(value) {
    return String(value)
        .replaceAll("&", "&amp;")
        .replaceAll("\"", "&quot;")
        .replaceAll("<", "&lt;")
        .replaceAll(">", "&gt;");
}

initializePortal();
