// ============================================
// DeepSeek Agent MCP - User Logs Viewer
// ============================================

class LogsViewer {
    constructor() {
        this.entries = [];
        this.filteredEntries = [];
        this.isAuthenticated = false;
        this.autoRefresh = false;
        this.autoRefreshTimer = null;
        this.currentSort = { field: 'timestamp', order: 'desc' };

        this.initElements();
        this.initEventListeners();
        this.initTheme();
        this.checkAuthStatus();
    }

    // --- DOM Elements ---
    initElements() {
        this.elements = {
            loginOverlay: document.getElementById('login-overlay'),
            googleSigninBtn: document.getElementById('google-signin-btn'),
            tbody: document.getElementById('logs-tbody'),
            table: document.getElementById('logs-table'),
            loading: document.getElementById('logs-loading'),
            empty: document.getElementById('logs-empty'),
            error: document.getElementById('logs-error'),
            errorMsg: document.getElementById('logs-error-msg'),
            retryBtn: document.getElementById('btn-retry'),
            refreshBtn: document.getElementById('btn-refresh'),
            autoRefreshBtn: document.getElementById('btn-auto-refresh'),
            exportBtn: document.getElementById('btn-export'),
            filterEvent: document.getElementById('filter-event'),
            filterSearch: document.getElementById('filter-search'),
            filterLimit: document.getElementById('filter-limit'),
            countBadge: document.getElementById('logs-count-badge'),
            footerInfo: document.getElementById('logs-footer-info'),
            subtitle: document.getElementById('logs-subtitle'),
            themeToggle: document.getElementById('theme-toggle-logs'),
        };
    }

    // --- Event Listeners ---
    initEventListeners() {
        this.elements.refreshBtn.addEventListener('click', () => this.loadLogs());
        this.elements.retryBtn.addEventListener('click', () => this.loadLogs());
        this.elements.exportBtn.addEventListener('click', () => this.exportJson());
        this.elements.filterEvent.addEventListener('change', () => this.applyFilters());
        this.elements.filterSearch.addEventListener('input', () => this.applyFilters());
        this.elements.filterLimit.addEventListener('change', () => this.applyFilters());
        this.elements.themeToggle.addEventListener('click', () => this.toggleTheme());
        this.elements.autoRefreshBtn.addEventListener('click', () => this.toggleAutoRefresh());

        // Sortable column headers
        document.querySelectorAll('.logs-th-sortable').forEach(th => {
            th.addEventListener('click', () => {
                const field = th.dataset.sort;
                const sameField = this.currentSort.field === field;
                this.currentSort.order = sameField && this.currentSort.order === 'desc' ? 'asc' : 'desc';
                this.currentSort.field = field;

                // Update active class
                document.querySelectorAll('.logs-th-sortable').forEach(h => h.classList.remove('active'));
                th.classList.add('active');
                th.dataset.order = this.currentSort.order;

                this.renderTable();
            });
        });

        // Filter on Enter key in search
        this.elements.filterSearch.addEventListener('keydown', (e) => {
            if (e.key === 'Enter') this.applyFilters();
        });
    }

    // --- Theme ---
    initTheme() {
        const saved = localStorage.getItem('deepseek-theme');
        if (saved === 'dark' || saved === null) {
            document.documentElement.setAttribute('data-theme', 'dark');
            this.updateThemeIcons(true);
        }
    }

    toggleTheme() {
        const isDark = document.documentElement.getAttribute('data-theme') === 'dark';
        if (isDark) {
            document.documentElement.removeAttribute('data-theme');
            localStorage.setItem('deepseek-theme', 'light');
            this.updateThemeIcons(false);
        } else {
            document.documentElement.setAttribute('data-theme', 'dark');
            localStorage.setItem('deepseek-theme', 'dark');
            this.updateThemeIcons(true);
        }
    }

    updateThemeIcons(isDark) {
        const sun = this.elements.themeToggle.querySelector('.theme-icon-sun');
        const moon = this.elements.themeToggle.querySelector('.theme-icon-moon');
        if (sun) sun.style.display = isDark ? 'none' : 'block';
        if (moon) moon.style.display = isDark ? 'block' : 'none';
    }

    // --- Authentication ---
    async checkAuthStatus() {
        try {
            const response = await fetch('/api/auth/status');
            const data = await response.json();

            if (data.authenticated) {
                this.isAuthenticated = true;
                this.hideLogin();
                this.loadLogs();
                this.startAutoRefreshCheck();
            } else if (data.authDisabled) {
                this.isAuthenticated = true;
                this.hideLogin();
                this.loadLogs();
                this.startAutoRefreshCheck();
            } else {
                this.isAuthenticated = false;
                this.showLogin();
            }
        } catch {
            // Assume auth disabled on error
            this.isAuthenticated = true;
            this.hideLogin();
            this.loadLogs();
            this.startAutoRefreshCheck();
        }
    }

    showLogin() {
        this.elements.loginOverlay.style.display = 'flex';
        this.elements.googleSigninBtn.onclick = () => {
            window.location.href = '/api/auth/google/login';
        };
    }

    hideLogin() {
        this.elements.loginOverlay.style.display = 'none';
    }

    // --- Data Loading ---
    async loadLogs() {
        this.showLoading();

        try {
            const limit = this.elements.filterLimit.value;
            const response = await fetch(`/api/user-log?count=${limit}`);

            if (response.status === 401) {
                // Try to re-auth
                const data = await response.json().catch(() => null);
                if (data?.code === 'AUTH_REQUIRED') {
                    window.location.href = '/api/auth/google/login';
                    return;
                }
                // Token auth — just show error
                this.showError('Não autorizado. Verifique sua chave de API.');
                return;
            }

            if (!response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();

            if (!data.enabled) {
                this.showEmpty('O logger de usuário não está habilitado. Configure UserLogPath no appsettings.json.');
                return;
            }

            this.entries = data.entries || [];
            this.elements.subtitle.textContent =
                ` ${data.totalBuffered} entradas em buffer · ${this.entries.length} carregadas`;

            this.applyFilters();
        } catch (err) {
            this.showError(`Erro ao carregar logs: ${err.message}`);
        }
    }

    // --- Filtering & Sorting ---
    applyFilters() {
        const eventFilter = this.elements.filterEvent.value.toLowerCase();
        const searchFilter = this.elements.filterSearch.value.toLowerCase().trim();

        this.filteredEntries = this.entries.filter(entry => {
            // Event filter
            if (eventFilter && entry.event?.toLowerCase() !== eventFilter) return false;

            // Search filter
            if (searchFilter) {
                const searchTarget = [
                    entry.user,
                    entry.email,
                    entry.detail,
                    entry.sessionId,
                    entry.clientIp
                ].filter(Boolean).join(' ').toLowerCase();
                if (!searchTarget.includes(searchFilter)) return false;
            }

            return true;
        });

        this.updateCounts();
        this.renderTable();
    }

    updateCounts() {
        const total = this.entries.length;
        const filtered = this.filteredEntries.length;
        const label = total > 0 ? `${filtered} de ${total} entradas` : '0 entradas';
        this.elements.countBadge.textContent = label;
        this.elements.footerInfo.textContent =
            total > 0
                ? `${filtered} entradas exibidas (de ${total} carregadas, ${this.entries.length} no buffer)`
                : 'Nenhum dado carregado.';
    }

    // --- Render ---
    renderTable() {
        const sorted = this.sortEntries([...this.filteredEntries]);

        if (sorted.length === 0) {
            this.elements.table.style.display = 'none';
            this.elements.empty.style.display = 'flex';
            this.elements.error.style.display = 'none';
            this.elements.loading.style.display = 'none';
            return;
        }

        this.elements.table.style.display = 'table';
        this.elements.empty.style.display = 'none';
        this.elements.error.style.display = 'none';
        this.elements.loading.style.display = 'none';

        this.elements.tbody.innerHTML = sorted.map(entry => {
            const ts = this.formatTimestamp(entry.timestamp);
            const eventLabel = this.getEventLabel(entry.event);
            const eventClass = this.getEventClass(entry.event);
            const detail = this.escapeHtml(entry.detail || '');
            const sessionShort = entry.sessionId
                ? this.truncateMiddle(this.escapeHtml(entry.sessionId), 16)
                : '—';
            const sessionFull = entry.sessionId
                ? this.escapeHtml(entry.sessionId)
                : '';

            return `
            <tr class="logs-tr">
                <td class="logs-td logs-td-timestamp" title="${ts.full}">
                    <span class="logs-timestamp-date">${ts.date}</span>
                    <span class="logs-timestamp-time">${ts.time}</span>
                </td>
                <td class="logs-td">
                    <span class="logs-event-badge ${eventClass}" title="${eventLabel}">${eventLabel}</span>
                </td>
                <td class="logs-td logs-td-user">${this.escapeHtml(entry.user || '—')}</td>
                <td class="logs-td logs-td-email">${this.escapeHtml(entry.email || '—')}</td>
                <td class="logs-td logs-td-session" ${sessionFull ? `title="${sessionFull}"` : ''}>${sessionShort}</td>
                <td class="logs-td logs-td-ip">${this.escapeHtml(entry.clientIp || '—')}</td>
                <td class="logs-td logs-td-detail">${detail || '—'}</td>
            </tr>`;
        }).join('');
    }

    sortEntries(entries) {
        const { field, order } = this.currentSort;
        const dir = order === 'desc' ? -1 : 1;

        return entries.sort((a, b) => {
            let valA, valB;

            switch (field) {
                case 'timestamp':
                    valA = new Date(a.timestamp).getTime();
                    valB = new Date(b.timestamp).getTime();
                    break;
                case 'event':
                    valA = (a.event || '').toLowerCase();
                    valB = (b.event || '').toLowerCase();
                    return dir * valA.localeCompare(valB);
                case 'user':
                    valA = (a.user || '').toLowerCase();
                    valB = (b.user || '').toLowerCase();
                    return dir * valA.localeCompare(valB);
                case 'email':
                    valA = (a.email || '').toLowerCase();
                    valB = (b.email || '').toLowerCase();
                    return dir * valA.localeCompare(valB);
                default:
                    return 0;
            }

            return dir * (valA - valB);
        });
    }

    // --- UI States ---
    showLoading() {
        this.elements.loading.style.display = 'flex';
        this.elements.table.style.display = 'none';
        this.elements.empty.style.display = 'none';
        this.elements.error.style.display = 'none';
    }

    showEmpty(message) {
        this.elements.loading.style.display = 'none';
        this.elements.table.style.display = 'none';
        this.elements.empty.style.display = 'flex';
        this.elements.error.style.display = 'none';
        const emptyMsg = this.elements.empty.querySelector('p');
        if (emptyMsg) emptyMsg.textContent = message || 'Nenhum log encontrado.';
    }

    showError(message) {
        this.elements.loading.style.display = 'none';
        this.elements.table.style.display = 'none';
        this.elements.empty.style.display = 'none';
        this.elements.error.style.display = 'flex';
        this.elements.errorMsg.textContent = message || 'Erro ao carregar logs.';
    }

    // --- Auto Refresh ---
    startAutoRefreshCheck() {
        // Auto-refresh is off by default; user toggles it
    }

    toggleAutoRefresh() {
        this.autoRefresh = !this.autoRefresh;
        this.elements.autoRefreshBtn.classList.toggle('active', this.autoRefresh);

        if (this.autoRefresh) {
            this.elements.autoRefreshBtn.title = 'Auto-atualização ligada';
            this.loadLogs(); // Immediate refresh
            this.autoRefreshTimer = setInterval(() => this.loadLogs(), 10000);
        } else {
            this.elements.autoRefreshBtn.title = 'Auto-atualização desligada';
            if (this.autoRefreshTimer) {
                clearInterval(this.autoRefreshTimer);
                this.autoRefreshTimer = null;
            }
        }
    }

    // --- Export ---
    exportJson() {
        const data = JSON.stringify(this.filteredEntries, null, 2);
        const blob = new Blob([data], { type: 'application/json' });
        const url = URL.createObjectURL(blob);
        const a = document.createElement('a');
        a.href = url;
        const now = new Date();
        const dateStr = now.toISOString().slice(0, 10);
        a.download = `user-logs-${dateStr}.json`;
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(url);
    }

    // --- Helpers ---
    formatTimestamp(ts) {
        if (!ts) return { full: '—', date: '—', time: '—' };
        const d = new Date(ts);
        if (isNaN(d.getTime())) return { full: ts, date: ts, time: '' };

        const date = d.toLocaleDateString('pt-BR', {
            day: '2-digit', month: '2-digit', year: 'numeric'
        });
        const time = d.toLocaleTimeString('pt-BR', {
            hour: '2-digit', minute: '2-digit', second: '2-digit'
        });
        const full = `${date} ${time}`;
        return { full, date, time };
    }

    getEventLabel(event) {
        const labels = {
            'login': 'Login',
            'logout': 'Logout',
            'message_sent': 'Mensagem',
            'session_created': 'Sessão criada',
            'session_removed': 'Sessão removida',
            'session_cleaned': 'Cleanup',
            'session_cleared': 'Limpeza',
            'request_cancelled': 'Cancelado',
            'rate_limit_exceeded': 'Rate limit',
            'session_limit_exceeded': 'Limite sessões',
            'error': 'Erro',
        };
        return labels[event] || event || '—';
    }

    getEventClass(event) {
        const classes = {
            'login': 'event-login',
            'logout': 'event-logout',
            'message_sent': 'event-message',
            'session_created': 'event-session',
            'session_removed': 'event-session',
            'session_cleaned': 'event-session',
            'session_cleared': 'event-session',
            'request_cancelled': 'event-cancel',
            'rate_limit_exceeded': 'event-warning',
            'session_limit_exceeded': 'event-warning',
            'error': 'event-error',
        };
        return classes[event] || 'event-default';
    }

    truncateMiddle(str, maxLen) {
        if (!str || str.length <= maxLen) return str || '';
        const half = Math.floor((maxLen - 2) / 2);
        return str.slice(0, half) + '…' + str.slice(str.length - half);
    }

    escapeHtml(str) {
        if (!str) return '';
        const div = document.createElement('div');
        div.textContent = str;
        return div.innerHTML;
    }
}

// --- Initialize ---
document.addEventListener('DOMContentLoaded', () => {
    new LogsViewer();
});
