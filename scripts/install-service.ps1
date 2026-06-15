<#
.SYNOPSIS
    Instala ou desinstala o DeepSeek Agent MCP como um Serviço Windows.

.DESCRIPTION
    Este script compila o projeto e instala/desinstala o Windows Service
    usando o utilitário sc.exe.

.PARAMETER Action
    install   - Compila e instala o serviço (padrão)
    uninstall - Para e remove o serviço
    status    - Mostra o status atual do serviço

.EXAMPLE
    .\install-service.ps1 -Action install
    .\install-service.ps1 -Action uninstall
    .\install-service.ps1 -Action status
#>

param(
    [ValidateSet("install", "uninstall", "status")]
    [string]$Action = "install"
)

$ServiceName = "DeepSeekAgentMCP2"
$DisplayName = "DeepSeek Agent MCP Service"
$Description = "Agente inteligente DeepSeek com suporte a MCP (Model Context Protocol) para consultas a bancos de dados e ferramentas externas."
$ProjectPath = Join-Path $PSScriptRoot "..\src\DeepSeekAgentMCP\DeepSeekAgentMCP.csproj"

function Write-Info {
    param([string]$Message)
    Write-Host "[INFO] $Message" -ForegroundColor Cyan
}

function Write-Success {
    param([string]$Message)
    Write-Host "[OK] $Message" -ForegroundColor Green
}

function Write-Error {
    param([string]$Message)
    Write-Host "[ERROR] $Message" -ForegroundColor Red
}

function Show-Status {
    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($service) {
        Write-Host "Serviço: $ServiceName" -ForegroundColor Yellow
        Write-Host "Status : $($service.Status)" -ForegroundColor Yellow
        Write-Host "Display: $($service.DisplayName)" -ForegroundColor Yellow
    }
    else {
        Write-Host "Serviço '$ServiceName' não encontrado." -ForegroundColor Yellow
    }
}

function Install-Service {
    # Stop service first if running (prevents file lock issues during build)
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService -and $existingService.Status -eq 'Running') {
        Write-Info "Parando serviço '$ServiceName' antes da compilação..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        Start-Sleep -Seconds 2
        Write-Success "Serviço parado."
    }

    Write-Info "Compilando o projeto..."
    
    # Build the project in Release mode
    $buildResult = & dotnet publish "$ProjectPath" -c Release -o "$PSScriptRoot\..\publish" --self-contained true -r win-x64 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Error "Falha ao compilar o projeto."
        $buildResult | ForEach-Object { Write-Host $_ -ForegroundColor Red }
        return
    }
    Write-Success "Projeto compilado com sucesso."

    # Copy Skills folder to publish
    $skillsSrc = "$PSScriptRoot\..\src\DeepSeekAgentMCP\Skills"
    $skillsDst = "$PSScriptRoot\..\publish\Skills"
    if (Test-Path $skillsSrc) {
        Write-Info "Copiando pasta Skills para publish..."
        Copy-Item -Path $skillsSrc -Destination $skillsDst -Recurse -Force
        Write-Success "Pasta Skills copiada com sucesso."
    }
    else {
        Write-Host "[WARN] Pasta Skills não encontrada em: $skillsSrc" -ForegroundColor Yellow
    }

    # Copy instructions.md to publish if not exists
    $instructionsSrc = "$PSScriptRoot\..\src\DeepSeekAgentMCP\instructions.md"
    $instructionsDst = "$PSScriptRoot\..\publish\instructions.md"
    if (-not (Test-Path $instructionsDst)) {
        if (Test-Path $instructionsSrc) {
            Write-Info "Copiando instructions.md para publish..."
            Copy-Item -Path $instructionsSrc -Destination $instructionsDst -Force
            Write-Success "instructions.md copiado com sucesso."
        }
        else {
            Write-Host "[WARN] instructions.md não encontrado em: $instructionsSrc" -ForegroundColor Yellow
        }
    }
    else {
        Write-Info "instructions.md já existe em publish, mantendo versão atual."
    }

    $exePath = "$PSScriptRoot\..\publish\DeepSeekAgentMCP.exe"

    if (-not (Test-Path $exePath)) {
        Write-Error "Executável não encontrado em: $exePath"
        return
    }

    # Check if service already exists
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existingService) {
        Write-Info "O serviço '$ServiceName' já existe. Removendo..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        & sc.exe delete $ServiceName 2>&1 | Out-Null
        Start-Sleep -Seconds 2
        Write-Success "Serviço antigo removido."
    }

    Write-Info "Instalando serviço '$ServiceName'..."
    
    # Create the service with dependency on TotvsRmDatabaseMcpServer2
    & sc.exe create $ServiceName `
        binPath="$exePath --service" `
        start=auto `
        depend=totvsrmdatabasemcpserver2.exe `
        DisplayName=$DisplayName 2>&1 | Out-Null

    if ($LASTEXITCODE -ne 0) {
        Write-Error "Falha ao criar o serviço. Execute o PowerShell como Administrador."
        return
    }

    # Set description
    & sc.exe description $ServiceName $Description 2>&1 | Out-Null

    Write-Success "Serviço criado com sucesso."
    Write-Info "Iniciando serviço..."
    
    Start-Service -Name $ServiceName -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 3

    $service = Get-Service -Name $ServiceName
    if ($service.Status -eq 'Running') {
        Write-Success "Serviço '$ServiceName' está rodando!"
        Write-Host ""
        Write-Host "Acesse a interface web em: http://localhost:5000" -ForegroundColor Green
        Write-Host ""
        Write-Host "Comandos úteis:" -ForegroundColor Yellow
        Write-Host "  Ver logs:        Get-EventLog -LogName Application -Source DeepSeekAgentMCP -Newest 20" -ForegroundColor Gray
        Write-Host "  Parar serviço:   Stop-Service -Name $ServiceName" -ForegroundColor Gray
        Write-Host "  Iniciar serviço: Start-Service -Name $ServiceName" -ForegroundColor Gray
        Write-Host "  Reiniciar:       Restart-Service -Name $ServiceName" -ForegroundColor Gray
    }
    else {
        Write-Error "Falha ao iniciar o serviço. Status: $($service.Status)"
    }
}

function Uninstall-Service {
    $existingService = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if (-not $existingService) {
        Write-Error "Serviço '$ServiceName' não encontrado."
        return
    }

    Write-Info "Parando serviço '$ServiceName'..."
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2

    Write-Info "Removendo serviço '$ServiceName'..."
    & sc.exe delete $ServiceName 2>&1 | Out-Null
    Start-Sleep -Seconds 2

    Write-Success "Serviço '$ServiceName' removido com sucesso."
}

# Main
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  DeepSeek Agent MCP - Windows Service" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

switch ($Action) {
    "install"   { Install-Service }
    "uninstall" { Uninstall-Service }
    "status"    { Show-Status }
}
