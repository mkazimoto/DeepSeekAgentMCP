// ============================================
// DeepSeek Agent MCP - Web Interface
// ============================================

class ChatApp {
    constructor() {
        this.isLoading = false;
        this.initElements();
        this.initEventListeners();
        this.initTheme();
        this.loadMcpStatus();
        this.autoResizeTextarea();
    }

    // --- DOM Elements ---
    initElements() {
        this.elements = {
            messagesContainer: document.getElementById('messages-container'),
            messageInput: document.getElementById('message-input'),
            sendBtn: document.getElementById('send-btn'),
            typingIndicator: document.getElementById('typing-indicator'),
            mcpStatus: document.getElementById('mcp-status'),
            modelName: document.getElementById('model-name'),
            welcomeMessage: document.getElementById('welcome-message'),
            sidebar: document.getElementById('sidebar'),
            menuToggle: document.getElementById('menu-toggle'),
            themeToggle: document.getElementById('theme-toggle'),
            statusIndicator: document.getElementById('status-indicator'),
        };
    }

    // --- Event Listeners ---
    initEventListeners() {
        // Send message
        this.elements.sendBtn.addEventListener('click', () => this.sendMessage());
        this.elements.messageInput.addEventListener('keydown', (e) => {
            if (e.key === 'Enter' && !e.shiftKey) {
                e.preventDefault();
                this.sendMessage();
            }
        });
        this.elements.messageInput.addEventListener('input', () => {
            this.autoResizeTextarea();
            this.updateSendButton();
        });

        // Sidebar toggle
        this.elements.menuToggle.addEventListener('click', () => {
            this.elements.sidebar.classList.toggle('open');
        });

        // Close sidebar on outside click (mobile)
        document.addEventListener('click', (e) => {
            if (window.innerWidth <= 768 &&
                !this.elements.sidebar.contains(e.target) &&
                !this.elements.menuToggle.contains(e.target)) {
                this.elements.sidebar.classList.remove('open');
            }
        });

        // Command buttons
        document.querySelectorAll('.command-btn').forEach(btn => {
            btn.addEventListener('click', () => this.handleCommand(btn.dataset.command));
        });

        // Theme toggle
        this.elements.themeToggle.addEventListener('click', () => this.toggleTheme());
    }

    // --- Theme ---
    initTheme() {
        const saved = localStorage.getItem('deepseek-theme');
        if (saved === 'dark' || (!saved && window.matchMedia('(prefers-color-scheme: dark)').matches)) {
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
        if (isDark) {
            sun.style.display = 'none';
            moon.style.display = 'block';
        } else {
            sun.style.display = 'block';
            moon.style.display = 'none';
        }
    }

    // --- MCP Status ---
    async loadMcpStatus() {
        try {
            const response = await fetch('/api/status');
            const data = await response.json();
            this.renderMcpStatus(data);
            if (data.model) {
                this.elements.modelName.textContent = data.model;
            }
        } catch (err) {
            this.elements.mcpStatus.innerHTML = `
                <div class="mcp-loading" style="color: #ef4444">
                    <span>Erro ao carregar status MCP</span>
                </div>
            `;
        }
    }

    renderMcpStatus(data) {
        if (!data.mcpServers || data.mcpServers.length === 0) {
            this.elements.mcpStatus.innerHTML = `
                <div style="font-size:13px;color:var(--text-tertiary);padding:8px 0;">
                    Nenhum servidor MCP conectado
                </div>
            `;
            return;
        }

        const html = data.mcpServers.map(server => `
            <div class="mcp-server-item">
                <span class="mcp-server-dot ${server.connected ? 'connected' : 'disconnected'}"></span>
                <span class="mcp-server-name">${this.escapeHtml(server.name)}</span>
                <span class="mcp-server-tools">${server.toolCount} ferramenta${server.toolCount !== 1 ? 's' : ''}</span>
            </div>
        `).join('');

        this.elements.mcpStatus.innerHTML = html;
    }

    // --- Send Message ---
    async sendMessage() {
        const text = this.elements.messageInput.value.trim();
        if (!text || this.isLoading) return;

        this.elements.messageInput.value = '';
        this.autoResizeTextarea();
        this.updateSendButton();

        // Remove welcome message
        if (this.elements.welcomeMessage) {
            this.elements.welcomeMessage.remove();
            this.elements.welcomeMessage = null;
        }

        // Add user message
        this.addMessage(text, 'user');

        // Show loading
        this.setLoading(true);

        try {
            const response = await fetch('/api/chat', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: text })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => null);
                throw new Error(errorData?.error || `Erro ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();

            // Hide typing indicator
            this.setLoading(false);

            // Add agent response
            if (data.response) {
                this.addMessage(data.response, 'agent');
            }

            // Add tool calls if present
            if (data.toolCalls && data.toolCalls.length > 0) {
                data.toolCalls.forEach(tc => {
                    this.addToolCall(tc);
                });
            }

        } catch (err) {
            this.setLoading(false);
            this.addMessage(`Erro: ${err.message}`, 'error');
        }

        this.scrollToBottom();
    }

    // --- Add Message ---
    addMessage(text, type) {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = `message ${type === 'user' ? 'user-message' : type === 'error' ? 'error-message' : ''}`;

        const avatar = document.createElement('div');
        avatar.className = `message-avatar ${type === 'user' ? 'user-avatar' : 'agent-avatar'}`;

        if (type === 'user') {
            avatar.innerHTML = `
                <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                    <circle cx="9" cy="6" r="3" stroke="currentColor" stroke-width="1.5"/>
                    <path d="M3 17C3 13.6863 5.68629 11 9 11C12.3137 11 15 13.6863 15 17" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/>
                </svg>
            `;
        } else {
            avatar.innerHTML = `
                <svg width="18" height="18" viewBox="0 0 18 18" fill="none">
                    <rect width="18" height="18" rx="4" fill="currentColor" fill-opacity="0.2"/>
                    <path d="M4.5 9L7.5 12L13.5 6" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
                </svg>
            `;
        }

        div.appendChild(avatar);

        const content = document.createElement('div');
        content.className = 'message-content';

        const sender = document.createElement('div');
        sender.className = 'message-sender';
        sender.textContent = type === 'user' ? 'Você' : type === 'error' ? 'Erro' : 'DeepSeek Agent';
        content.appendChild(sender);

        const textDiv = document.createElement('div');
        textDiv.className = 'message-text';

        // Format markdown-like content
        textDiv.innerHTML = this.formatMessage(text);

        content.appendChild(textDiv);
        div.appendChild(content);

        container.appendChild(div);
        this.scrollToBottom();
    }

    // --- Add Tool Call ---
    addToolCall(toolCall) {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = 'message tool-message';

        const content = document.createElement('div');
        content.className = 'message-content';

        const sender = document.createElement('div');
        sender.className = 'message-sender';
        sender.innerHTML = `🔧 ${this.escapeHtml(toolCall.name)}`;
        content.appendChild(sender);

        const textDiv = document.createElement('div');
        textDiv.className = 'message-text';
        try {
            const args = JSON.parse(toolCall.arguments);
            textDiv.textContent = `Argumentos: ${JSON.stringify(args, null, 2)}`;
        } catch {
            textDiv.textContent = this.escapeHtml(toolCall.arguments || 'Sem argumentos');
        }

        content.appendChild(textDiv);
        div.appendChild(content);

        container.appendChild(div);
        this.scrollToBottom();
    }

    // --- Format Message (Markdown renderer via marked) ---
    formatMessage(text) {
        if (!text) return '';

        // Configure marked for safe rendering
        if (typeof marked !== 'undefined') {
            marked.setOptions({
                breaks: true,
                gfm: true,
                headerIds: false,
                mangle: false,
            });

            let html = marked.parse(text);

            // Open external links in new tab
            html = html.replace(/<a href=/g, '<a target="_blank" rel="noopener" href=');

            return html;
        }

        // Fallback: simple text-only rendering
        return `<p>${this.escapeHtml(text)}</p>`;
    }

    // --- Commands ---
    async handleCommand(command) {
        switch (command) {
            case '/clear':
                await this.clearConversation();
                break;
            case '/history':
                await this.showHistory();
                break;
            case '/mcp':
                await this.loadMcpStatus();
                this.showToast('✅ Status MCP atualizado');
                break;
        }
    }

    async clearConversation() {
        try {
            await fetch('/api/clear', { method: 'POST' });
            this.elements.messagesContainer.innerHTML = '';
            this.showToast('🧹 Conversa limpa');
        } catch (err) {
            this.showToast('❌ Erro ao limpar conversa');
        }
    }

    async showHistory() {
        try {
            const response = await fetch('/api/history');
            const data = await response.json();

            if (!data.history || data.history.length === 0) {
                this.showToast('📭 Nenhum histórico disponível');
                return;
            }

            // Show history in messages
            this.elements.messagesContainer.innerHTML = '';
            data.history.forEach(msg => {
                if (msg.role === 'user') {
                    this.addMessage(msg.content, 'user');
                } else if (msg.role === 'assistant') {
                    this.addMessage(msg.content, 'agent');
                }
            });
            this.showToast(`📜 Histórico carregado (${data.history.length} mensagens)`);
        } catch (err) {
            this.showToast('❌ Erro ao carregar histórico');
        }
    }

    // --- UI Helpers ---
    setLoading(loading) {
        this.isLoading = loading;
        this.elements.sendBtn.disabled = loading;
        this.elements.messageInput.disabled = loading;
        this.elements.typingIndicator.style.display = loading ? 'flex' : 'none';
        if (loading) {
            this.elements.sendBtn.classList.add('loading');
        } else {
            this.elements.sendBtn.classList.remove('loading');
        }
    }

    updateSendButton() {
        this.elements.sendBtn.disabled = !this.elements.messageInput.value.trim() || this.isLoading;
    }

    autoResizeTextarea() {
        const textarea = this.elements.messageInput;
        textarea.style.height = 'auto';
        textarea.style.height = Math.min(textarea.scrollHeight, 200) + 'px';
    }

    scrollToBottom() {
        const container = this.elements.messagesContainer;
        requestAnimationFrame(() => {
            container.scrollTop = container.scrollHeight;
        });
    }

    showToast(message) {
        let toast = document.querySelector('.toast');
        if (!toast) {
            toast = document.createElement('div');
            toast.className = 'toast';
            document.body.appendChild(toast);
        }
        toast.textContent = message;
        toast.classList.add('show');
        clearTimeout(this._toastTimeout);
        this._toastTimeout = setTimeout(() => {
            toast.classList.remove('show');
        }, 3000);
    }

    escapeHtml(text) {
        const div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }
}

// --- Initialize ---
document.addEventListener('DOMContentLoaded', () => {
    new ChatApp();
});
