// ============================================
// DeepSeek Agent MCP - Web Interface
// ============================================

class ChatApp {
    constructor() {
        this.isLoading = false;
        // Generate a unique session ID per tab/instance
        // This ensures each browser tab has its own isolated conversation
        this.sessionId = crypto.randomUUID ? crypto.randomUUID() : 
            'xxxxxxxx-xxxx-4xxx-yxxx-xxxxxxxxxxxx'.replace(/[xy]/g, c => {
                const r = Math.random() * 16 | 0;
                return (c === 'x' ? r : (r & 0x3 | 0x8)).toString(16);
            });
        this.initElements();
        this.restoreWelcomeMessage();
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
            cancelBtn: document.getElementById('cancel-btn'),
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
        this.elements.cancelBtn.addEventListener('click', () => this.cancelRequest());
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

        // Copy code button delegation
        this.elements.messagesContainer.addEventListener('click', (e) => {
            const btn = e.target.closest('[data-copy-code]');
            if (btn) {
                this.copyCodeContent(btn);
            }
        });
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
                body: JSON.stringify({ message: text, sessionId: this.sessionId })
            });

            // If loading was already turned off (via cancel), don't process response
            if (!this.isLoading) return;

            if (!response.ok) {
                const errorData = await response.json().catch(() => null);
                throw new Error(errorData?.error || `Erro ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();

            // If loading was already turned off (via cancel), don't process response
            if (!this.isLoading) return;

            // Hide loading
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
            if (!this.isLoading) return;
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
        sender.textContent = type === 'user' ? 'Você' : type === 'error' ? 'Erro' : 'Assistente gerador de consulta SQL';
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

            // Add copy button to code blocks
            html = html.replace(
                /<pre><code(?: class="([^"]*)")?>/g,
                (match, lang) => {
                    const langLabel = lang ? lang.replace(/^language-/, '') : 'code';
                    const classAttr = lang ? ` class="${lang}"` : '';
                    return `<div class="code-block-wrapper"><div class="code-block-header"><span class="code-lang">${langLabel}</span><button class="copy-btn" data-copy-code title="Copiar código"><svg width="16" height="16" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><rect x="9" y="9" width="13" height="13" rx="2" ry="2"/><path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1"/></svg><span>Copiar</span></button></div><pre><code${classAttr}>`;
                }
            );
            html = html.replace(/<\/code><\/pre>(?!\s*<\/div>)/g, `</code></pre></div>`);

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
        }
    }

    async clearConversation() {
        try {
            await fetch('/api/clear', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: '', sessionId: this.sessionId })
            });
            this.elements.messagesContainer.innerHTML = '';
            this.restoreWelcomeMessage();
            this.showToast('🧹 Conversa limpa');
        } catch (err) {
            this.showToast('❌ Erro ao limpar conversa');
        }
    }

    restoreWelcomeMessage() {
        const container = this.elements.messagesContainer;

        const div = document.createElement('div');
        div.className = 'message welcome-message';
        div.id = 'welcome-message';

        div.innerHTML = `
            <div class="message-avatar agent-avatar">
                <svg width="20" height="20" viewBox="0 0 20 20" fill="none">
                    <rect width="20" height="20" rx="5" fill="url(#avatar-gradient)"/>
                    <path d="M5 10L8 13L15 6" stroke="white" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"/>
                    <defs>
                        <linearGradient id="avatar-gradient" x1="0" y1="0" x2="20" y2="20">
                            <stop offset="0%" stop-color="#4F46E5"/>
                            <stop offset="100%" stop-color="#7C3AED"/>
                        </linearGradient>
                    </defs>
                </svg>
            </div>
            <div class="message-content">
                <div class="message-sender">Assistente gerador de consulta SQL - ERP TOTVS RM</div>
                <div class="message-text">
                    <p>👋 Bem vindo! Sou o <strong>Assistente gerador de consulta SQL</strong>, especializado em gerar consultas SQL a partir das suas perguntas.</p>
                    <p>Exemplo:</p>
                    <p>* Listar todos os lançamentos financeiros a receber inadimplentes.</p>
                    <p>* Listar os funcionários ativos agrupados por faixa etária de 10 em 10 anos.</p>
                    <p>* Listar as possíveis situações dos funcionários.</p>
                    <p>* Listar os top 10 clientes inadimplentes de vendas de imóveis.</p>
                    <p>* Listar os top 10 alunos do ensino superior inadimplentes.</p>
                    <p>* documentação da tabela TMOV.</p>
                </div>
            </div>
        `;

        container.appendChild(div);
        this.elements.welcomeMessage = div;
        this.scrollToBottom();
    }



    // --- Cancel Request ---
    async cancelRequest() {
        try {
            await fetch('/api/cancel', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ message: '', sessionId: this.sessionId })
            });
        } catch {
            // Ignore errors on cancel
        }
        this.setLoading(false);
        this.showToast('⏹️ Pedido cancelado');
    }

    // --- UI Helpers ---
    setLoading(loading) {
        this.isLoading = loading;
        this.elements.sendBtn.disabled = loading;
        this.elements.messageInput.disabled = loading;
        this.elements.typingIndicator.style.display = loading ? 'flex' : 'none';

        // Toggle between send and cancel buttons
        this.elements.sendBtn.style.display = loading ? 'none' : 'flex';
        this.elements.cancelBtn.style.display = loading ? 'flex' : 'none';

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

    // --- Copy Code ---
    async copyCodeContent(btn) {
        const wrapper = btn.closest('.code-block-wrapper');
        if (!wrapper) return;

        const code = wrapper.querySelector('code');
        if (!code) return;

        try {
            const text = code.textContent;
            await navigator.clipboard.writeText(text);

            const span = btn.querySelector('span');
            const original = span.textContent;
            span.textContent = 'Copiado!';
            btn.classList.add('copied');

            setTimeout(() => {
                span.textContent = original;
                btn.classList.remove('copied');
            }, 2000);
        } catch {
            // Fallback for older browsers
            try {
                const range = document.createRange();
                range.selectNodeContents(code);
                const selection = window.getSelection();
                selection.removeAllRanges();
                selection.addRange(range);
                document.execCommand('copy');
                selection.removeAllRanges();

                const span = btn.querySelector('span');
                span.textContent = 'Copiado!';
                btn.classList.add('copied');
                setTimeout(() => {
                    span.textContent = 'Copiar';
                    btn.classList.remove('copied');
                }, 2000);
            } catch {
                this.showToast('❌ Erro ao copiar');
            }
        }
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
