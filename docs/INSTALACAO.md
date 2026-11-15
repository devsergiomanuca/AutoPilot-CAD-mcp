# Roteiro de instalação do AcadBridge (para agentes de IA)

Este roteiro é para **você, agente** (Codex, Claude Code ou outro cliente MCP), seguir no Windows do
usuário. Quem pede costuma ser um profissional que usa AutoCAD (arquiteto, projetista, engenheiro…).
Ele só colou a URL deste repositório e quer usar o AutoCAD com IA. **Trate-o como não técnico por
padrão.** Se ele se mostrar desenvolvedor, pode ser mais técnico e mostrar os comandos.

**Regras de conduta**
- Execute os comandos você mesmo. Explique em português simples o que está fazendo, numa frase por etapa.
- Peça ao usuário **só** o que exige ação humana: salvar/fechar/abrir o AutoCAD, aprovar permissões
  (inclusive o UAC do Windows), clicar **Always Load** e abrir uma conversa nova.
- Use **Windows PowerShell** e os comandos exatamente como estão aqui.
- Os comandos gravam fora da pasta de trabalho (`%USERPROFILE%`, `%APPDATA%`, `%LOCALAPPDATA%`,
  `~/.codex`) e usam a rede (git, winget). Se o seu sandbox bloquear, **peça aprovação/permissão
  elevada** ao usuário. Não contorne o bloqueio.
- O `setup.ps1` imprime linhas `ACADBRIDGE_RESULT chave=valor`. Baseie suas decisões nelas e no código
  de saída: `0` ok · `1` erro · `2` AutoCAD aberto · `3` falta pré-requisito.

---

## 0. Descubra o valor de `-Agent`

| Você é | `-Agent` |
|---|---|
| Codex (CLI, app ou extensão de IDE; todos usam `~/.codex/config.toml`) | `codex` |
| Claude Code | `claude` |
| Outro cliente MCP | `none`: o setup imprime o comando/JSON; configure conforme a documentação do seu cliente |

## 1. Baixe o código para `$HOME\AcadBridge`

Repositório oficial: `https://github.com/devsergiomanuca/AutoPilot-CAD-mcp` (se o usuário colou um fork, use a URL dele no lugar).

```powershell
$dest = Join-Path $HOME 'AcadBridge'
if (Test-Path (Join-Path $dest '.git')) {
    git -C $dest pull --ff-only                       # já instalado antes: atualiza
} elseif (Get-Command git -ErrorAction SilentlyContinue) {
    git clone https://github.com/devsergiomanuca/AutoPilot-CAD-mcp.git $dest
} else {
    # Sem git: baixa o .zip do branch main.
    $zip = Join-Path $env:TEMP 'AcadBridge.zip'
    Invoke-WebRequest 'https://github.com/devsergiomanuca/AutoPilot-CAD-mcp/archive/refs/heads/main.zip' -OutFile $zip -UseBasicParsing
    $tmp = Join-Path $env:TEMP 'AcadBridge-src'
    if (Test-Path $tmp) { Remove-Item -Recurse -Force $tmp }
    Expand-Archive $zip $tmp
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    Move-Item (Get-ChildItem $tmp -Directory | Select-Object -First 1).FullName $dest
    Get-ChildItem $dest -Recurse -File | Unblock-File
}
```

Repositório privado: o `git clone` vai pedir login (o Git Credential Manager abre o navegador) e o .zip
não funciona sem autenticação. Nesse caso, peça ao usuário para fazer o login quando a janela abrir.

## 2. Confira os pré-requisitos

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -CheckOnly
```

| Resultado | O que fazer |
|---|---|
| `autocad_2025_dir=` (vazio) | **Pare.** Explique que é preciso o **AutoCAD 2025 completo** (o LT não suporta plugins) |
| `dotnet=` (vazio) | Instale o .NET SDK 8 (o Windows pode pedir confirmação de administrador): `winget install --id Microsoft.DotNet.SDK.8 -e --accept-source-agreements --accept-package-agreements`. Sem winget, peça ao usuário para baixar o "SDK x64" em https://dotnet.microsoft.com/download/dotnet/8.0. Depois repita o passo 2 |
| `autocad_running=True` | Peça: "**Salve seus desenhos e feche o AutoCAD**, depois me avise." Repita o passo 2 |
| `status=ready` | Siga em frente |

## 3. Instale

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -Agent codex   # ou claude / none
```

O script compila o plugin e o servidor MCP, instala o bundle do AutoCAD, testa o MCP
(`tools=11` esperado) e registra o servidor `acad-bridge` no agente. Espere `status=installed`.

- `exit 2`: o AutoCAD foi aberto no meio. Peça para fechar e rode de novo.
- `exit 1`: leia a linha `XX ...` e a seção **Problemas comuns**.
- Rodar de novo é seguro: o setup reinstala e substitui o registro anterior.

## 4. Peça ao usuário para abrir o AutoCAD

Diga algo como:

> Pronto! Agora abra o AutoCAD 2025. Se aparecer um aviso de segurança sobre **AcadBridge.Plugin.dll**,
> clique em **Always Load** (Carregar sempre). Ele só aparece na primeira vez e a cada atualização.
> Na linha de comando deve surgir "[AcadBridge] v… ativo". Me avise quando abrir.

Depois, verifique:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -Verify
```

Espere `status=connected`. Se vier `not_connected` com `plugin_lock_files=0`, o plugin não carregou:
veja **Problemas comuns**.

## 5. Conversa nova

As ferramentas MCP só aparecem em **conversas novas**. Diga:

> Instalação concluída. Abra uma **conversa nova** e peça, por exemplo: "o que tem neste desenho?".
> Cada alteração que eu fizer no desenho pode ser desfeita com **U** no AutoCAD. Para impedir
> alterações, digite **ACADBRIDGE_READONLY** no AutoCAD.

---

## Atualizar

Repita os passos 1 (o `git pull` atualiza), 2, 3 e 4. Se a versão do plugin mudou, o aviso
**Always Load** aparece de novo uma vez. Conversas já abertas continuam com o MCP antigo: oriente o
usuário a abrir uma conversa nova.

## Desinstalar

Com o AutoCAD fechado:
```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -Uninstall
```
Isso remove o bundle, o MCP publicado e o registro no Codex/Claude Code. A pasta `$HOME\AcadBridge`
(código) pode ser apagada depois.

---

## Onde as coisas ficam

| O quê | Onde |
|---|---|
| Código-fonte | `%USERPROFILE%\AcadBridge` |
| Servidor MCP (o que o agente executa) | `%LOCALAPPDATA%\AcadBridge\mcp\<versão>\AcadBridge.Mcp.exe` |
| Plugin do AutoCAD | `%APPDATA%\Autodesk\ApplicationPlugins\AcadBridge.bundle\` |
| Lock file (porta + token, só com o AutoCAD aberto) | `%APPDATA%\AcadBridge\<pid>.lock` |
| Registro no Codex | `~/.codex/config.toml`, seção `[mcp_servers.acad-bridge]` (backup em `config.toml.bak-acadbridge`) |
| Registro no Claude Code | `claude mcp get acad-bridge` (escopo user) |

## Problemas comuns

| Sintoma | Causa provável | Solução |
|---|---|---|
| `-Verify`: `not_connected`, `plugin_lock_files=0`, AutoCAD aberto | o usuário clicou "Do Not Load" ou o plugin não carregou | peça para digitar `ACADBRIDGE` no AutoCAD. Se o comando não existir, feche o AutoCAD, rode o passo 3 de novo e, na abertura, escolha **Always Load** |
| `ACADBRIDGE` responde "parado" | alguém rodou `ACADBRIDGE_STOP` | peça para digitar `ACADBRIDGE_START` |
| ferramenta responde "AutoCAD ocupado com o comando…" | há um comando em andamento | peça para terminar o comando ou apertar **Esc** |
| ferramenta responde "modo SOMENTE LEITURA" | o usuário ligou `ACADBRIDGE_READONLY` | só libere se ele quiser: digitar `ACADBRIDGE_READONLY` de novo |
| "plugin mais antigo que este servidor MCP" | atualização incompleta | AutoCAD fechado → passo 3 |
| `dotnet publish`/`build` falhou | SDK ausente ou corrompido, ou AutoCAD em pasta diferente | repita o passo 2; confira `autocad_2025_dir` |
| as ferramentas não aparecem no agente | conversa aberta antes da instalação, ou registro faltando | conversa nova. Codex: confira `[mcp_servers.acad-bridge]` em `~/.codex/config.toml`. Claude Code: `claude mcp get acad-bridge` |
| Codex: timeout ao iniciar o MCP | antivírus examinando o .exe novo | tente de novo; o setup já usa `startup_timeout_sec = 20` |

## Configuração manual (qualquer cliente MCP)

Servidor **stdio**, sem argumentos nem variáveis obrigatórias:

```json
{ "mcpServers": { "acad-bridge": { "command": "C:\\Users\\<usuário>\\AppData\\Local\\AcadBridge\\mcp\\<versão>\\AcadBridge.Mcp.exe" } } }
```

O caminho exato é a linha `ACADBRIDGE_RESULT mcp_exe=` do passo 3. Se o cliente iniciar servidores
com ambiente reduzido, inclua a variável `SystemRoot` (o TCP do Windows precisa dela).
