# DeepSeek Agent MCP

🤖 **DeepSeek Agent** com suporte a **MCP (Model Context Protocol)** — um agente inteligente em C# que conecta o modelo DeepSeek a ferramentas externas através do protocolo MCP.

## ✨ Funcionalidades

- **Integração com DeepSeek API** — Chat completions com suporte a streaming e function calling
- **Múltiplos servidores MCP** — Conecte-se a vários servidores MCP simultaneamente
- **Descoberta automática de ferramentas** — Ferramentas MCP são expostas automaticamente ao DeepSeek
- **Loop interativo** — Terminal interativo para conversar com o agente
- **Streaming** — Respostas em tempo real com streaming
- **Histórico de conversação** — Mantém contexto entre mensagens

## 🚀 Como usar

### 1. Configurar API Key

A chave da API **não deve** ser colocada diretamente no `config/appsettings.json` (o arquivo versionado contém apenas um placeholder vazio). Configure por variável de ambiente:

#### Windows (permanente — recomendado)

```powershell
[Environment]::SetEnvironmentVariable("DEEPSEEK_API_KEY", "sua-chave-aqui", "User")
```

Depois reinicie o terminal para aplicar.

#### Windows (sessão atual)

```powershell
$env:DEEPSEEK_API_KEY = "sua-chave-aqui"
```

#### Linux / macOS

```bash
export DEEPSEEK_API_KEY="sua-chave-aqui"
```

> O programa busca a chave na seguinte ordem: `appsettings.json` → variável de ambiente (Process) → variável de ambiente (User). Se nenhuma for encontrada, solicita a digitação no terminal.

### 2. Configurar Servidores MCP

Edite o arquivo `config/mcp-servers.json` para adicionar os servidores MCP desejados.

Servidores pré-configurados:

| Nome | Comando | Descrição |
|------|---------|-----------|
| Filesystem | `npx -y @modelcontextprotocol/server-filesystem` | Acesso ao sistema de arquivos |
| Fetch | `npx -y @modelcontextprotocol/server-fetch` | Busca de páginas web |
| GitHub | `npx -y @modelcontextprotocol/server-github` | Integração com GitHub (requer token) |

### 3. Executar

```bash
cd src/DeepSeekAgentMCP
dotnet run
```

### 4. Comandos

| Comando | Descrição |
|---------|-----------|
| `/exit` | Sair do agente |
| `/clear` | Limpar histórico da conversa |
| `/history` | Mostrar histórico da conversa |
| `/mcp` | Mostrar status dos servidores MCP |
| `/help` | Mostrar ajuda |

## 🧩 Adicionar Novos Servidores MCP

Adicione novas entradas em `config/mcp-servers.json`:

```json
{
  "Name": "MeuServidor",
  "Enabled": true,
  "TransportType": "stdio",
  "Command": "npx",
  "Arguments": ["-y", "@modelcontextprotocol/server-exemplo"],
  "EnvironmentVariables": {
    "API_KEY": "minha-chave"
  }
}
```

## 🏗️ Estrutura do Projeto

```
DeepSeekAgentMCP/
├── config/
│   ├── appsettings.json        # Configuração principal
│   └── mcp-servers.json        # Configuração dos servidores MCP
├── src/DeepSeekAgentMCP/
│   ├── Models/
│   │   ├── ChatMessage.cs      # Modelos de mensagem
│   │   └── DeepSeekResponses.cs # Modelos de requisição/resposta
│   ├── DeepSeekClient.cs       # Cliente HTTP para DeepSeek API
│   ├── McpToolManager.cs       # Gerenciador de servidores MCP
│   ├── DeepSeekAgent.cs        # Lógica principal do agente
│   └── Program.cs              # Ponto de entrada e loop interativo
├── DeepSeekAgentMCP.sln
└── README.md
```

## �️ Windows Service

O agente pode ser executado como um **Serviço Windows**, rodando em segundo plano com a interface web habilitada.

### Instalação

Execute o PowerShell como **Administrador** e use o script de instalação:

```powershell
# Instalar o serviço
.\scripts\install-service.ps1 -Action install

# Verificar status
.\scripts\install-service.ps1 -Action status

# Desinstalar o serviço
.\scripts\install-service.ps1 -Action uninstall
```

O script:
1. Compila o projeto em modo **Release** com `dotnet publish --self-contained`
2. Cria o serviço Windows com nome `DeepSeekAgentMCP`
3. Configura inicialização automática
4. Inicia o serviço automaticamente

### Gerenciamento manual

```powershell
# Via PowerShell (como Administrador)
Stop-Service -Name DeepSeekAgentMCP
Start-Service -Name DeepSeekAgentMCP
Restart-Service -Name DeepSeekAgentMCP

# Via sc.exe
sc.exe stop DeepSeekAgentMCP
sc.exe start DeepSeekAgentMCP
```

### Logs

O serviço registra eventos no **Visualizador de Eventos do Windows**:

```powershell
Get-EventLog -LogName Application -Source DeepSeekAgentMCP -Newest 20
```

### Acesso

Com o serviço rodando, acesse a interface web em:

```
http://localhost:5000
```

### Execução manual (modo serviço)

Para testar o comportamento do serviço sem instalar:

```bash
cd src/DeepSeekAgentMCP
dotnet run -- --service
```

### Estrutura de arquivos adicionada

```
DeepSeekAgentMCP/
├── scripts/
│   └── install-service.ps1    # Script de instalação/desinstalação
├── src/DeepSeekAgentMCP/
│   ├── DeepSeekAgentService.cs # Implementação do Windows Service
│   └── ...
```

## �📋 Pré-requisitos

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js](https://nodejs.org/) (para servidores MCP baseados em npx)
- Uma chave de API do [DeepSeek](https://platform.deepseek.com/)
