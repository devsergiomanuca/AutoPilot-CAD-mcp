# AcadBridge: AutoCAD 2025 ⇄ agentes de IA (MCP)

O AcadBridge deixa um agente de IA (Codex, Claude Code ou qualquer cliente
[MCP](https://modelcontextprotocol.io)) **ler e editar o desenho aberto no AutoCAD 2025**. O agente
pode resumir o desenho, listar layers e blocos, medir o que o usuário selecionou e ver a tela. Também
pode criar e alterar layers. Cada alteração da IA se desfaz com **um único `U`**.

Tudo roda **no próprio PC**: o agente conversa com um servidor MCP local, que conversa com um plugin
dentro do AutoCAD por `127.0.0.1`. Nada fica exposto na rede.

## Para quem é

- **Quem desenha no AutoCAD**: arquitetura, interiores, engenharia, detalhamento, projetos em geral.
  Acelere o que é repetitivo: conferir e padronizar layers, levantar quantitativos de blocos, medir
  seleções, organizar o desenho. Basta pedir em português ao agente que você já usa. **Não precisa
  saber programar.** O agente instala tudo sozinho.
- **Devs**: um servidor MCP pequeno e legível, escrito à mão e sem SDK, e um plugin .NET 8 com as
  partes difíceis já resolvidas: thread principal, undo em um passo, vários desenhos abertos e
  segurança. Cada ferramenta nova é um método no plugin + uma entrada no catálogo
  (veja [Desenvolvimento](#desenvolvimento)).

---

## Instalação (sem precisar programar)

Abra o seu agente de IA (Codex, Claude Code…) e cole:

> **Instale e configure o AcadBridge seguindo as instruções deste repositório: https://github.com/devsergiomanuca/AutoPilot-CAD-mcp**

O agente faz o resto. Durante a instalação ele vai pedir que você:

1. **salve e feche o AutoCAD** (o plugin não pode ser instalado com ele aberto);
2. **aprove** alguns comandos (baixar o AcadBridge e gravar a configuração). Não é preciso instalar
   mais nada: o pacote pronto usa o .NET 8 que o próprio AutoCAD 2025 já instalou;
3. **abra o AutoCAD** e, no aviso de segurança sobre `AcadBridge.Plugin.dll`, clique em **Always Load**;
4. **abra uma conversa nova** no agente. As ferramentas do AutoCAD só aparecem em conversas novas.

Para **atualizar**, peça "atualize o AcadBridge". Para **remover**, peça "desinstale o AcadBridge".

---

## 🤖 Para o agente de IA

Você recebeu este repositório para **instalar, atualizar ou remover** o AcadBridge no Windows do usuário.
**Siga o roteiro em [`docs/INSTALACAO.md`](docs/INSTALACAO.md)**. Texto puro, se você só conseguir ler esta página:
`https://raw.githubusercontent.com/devsergiomanuca/AutoPilot-CAD-mcp/main/docs/INSTALACAO.md`.

Resumo, caso não consiga abrir o roteiro:

```powershell
# 1. código em $HOME\AcadBridge (git clone; sem git, baixe o .zip do branch main e extraia)
git clone https://github.com/devsergiomanuca/AutoPilot-CAD-mcp.git "$HOME\AcadBridge"
# 2. pré-requisitos (AutoCAD 2025 completo e fechado; .NET 8: o AutoCAD já traz)
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -CheckOnly
# 3. instalar e registrar no SEU cliente: -Agent codex | claude | none
#    (sem .NET SDK, baixa o pacote pronto da Release; com SDK, compila este código)
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -Agent codex
# 4. depois que o usuário abrir o AutoCAD e clicar "Always Load":
powershell -NoProfile -ExecutionPolicy Bypass -File "$HOME\AcadBridge\setup.ps1" -Verify   # status=connected
```

Considere que o usuário **pode não ser técnico** (arquiteto, projetista…): execute você mesmo e
explique em linguagem simples. Peça a ele só o que exige ação humana (fechar/abrir o AutoCAD, aprovar
permissões, "Always Load", abrir conversa nova). Se ele se mostrar desenvolvedor, pode detalhar mais.

---

## Como usar

Com o AutoCAD aberto, peça em linguagem natural:

- "O que tem neste desenho?" · "Quais layers estão congeladas ou travadas?"
- "Quantas inserções de cada bloco?" (quantitativos de portas, luminárias, mobiliário…)
- "Selecionei uns objetos. Quais as medidas e a área?" · "Me mostra a vista atual."
- "Cria a layer CORTE, vermelha, tracejada, 0,35." · "Move o que eu selecionei para a layer COTAS."
- "Confere se as layers seguem o padrão do escritório e me diz o que está fora."

| Comando no AutoCAD | O que faz |
|---|---|
| `U` | desfaz a última ação da IA, inteira, como qualquer comando |
| `ACADBRIDGE` | mostra o status (versão, porta, modo, último erro) |
| `ACADBRIDGE_READONLY` | liga/desliga o **modo somente leitura**: a IA continua vendo, mas não altera nada |
| `ACADBRIDGE_STOP` / `ACADBRIDGE_START` | desliga/religa o acesso da IA a este AutoCAD |

Com **vários desenhos abertos**, a conversa fica presa ao desenho da primeira pergunta. Se você trocar
de aba, a IA recusa e pergunta antes de continuar. A IA nunca age num desenho que você não está vendo.

---

## Como funciona

```
 Agente (Codex / Claude Code / outro cliente MCP)
      │  MCP sobre stdio: JSON-RPC 2.0, uma mensagem por linha
      ▼
 AcadBridge.Mcp.exe            processo filho do agente (.NET 8), um por conversa
      │  1. lê %APPDATA%\AcadBridge\<pid>.lock → porta + token
      │  2. TCP 127.0.0.1:<porta>, uma linha JSON (NDJSON) com o token
      ▼
 AcadBridge.Plugin.dll         DENTRO do acad.exe (bundle carregado na abertura)
      │  3. fila → thread principal do AutoCAD (Control.BeginInvoke, timeout 30 s)
      │  4. confere desenho-alvo → comando ativo? → leitura ou escrita com undo
      ▼
 Desenho ativo (Database do documento)
```

### Por que dois processos
O agente **inicia** o servidor MCP como processo filho e fala com ele por stdin/stdout. Ninguém
consegue iniciar algo "dentro" do AutoCAD. Por isso o plugin abre um canal local e o servidor MCP
traduz MCP ⇄ canal local. É o mesmo padrão das extensões de IDE: lock file + token + socket local.

### Por que TCP local e não COM
O COM falha quando um processo está elevado (admin) e o outro não: a Running Object Table não é
compartilhada entre níveis de integridade. TCP em loopback não tem essa barreira, e o código roda
dentro do AutoCAD com acesso à API .NET completa.

### O servidor MCP (`AcadBridge.Mcp`)
Implementação **à mão, sem SDK**, sobre o transporte stdio:

| Método JSON-RPC | Resposta |
|---|---|
| `initialize` | ecoa a `protocolVersion` pedida; `capabilities.tools`; `serverInfo`; `instructions`: regras de uso para o modelo (consultar antes de alterar, avisar o que mudou, 1 chamada = 1 undo, o que fazer numa recusa) |
| `notifications/initialized` | notificação, sem resposta |
| `ping` | `{}` |
| `tools/list` | 11 ferramentas com `inputSchema` (JSON Schema, `additionalProperties: false`) e `annotations` (`readOnlyHint`, `destructiveHint`, `idempotentHint`, `openWorldHint`) |
| `tools/call` | `content` com `text` (JSON indentado) ou `image` (PNG base64) |

Regras do protocolo seguidas no código:
- O **stdout é só do protocolo**. Log vai para stderr.
- **Erro de ferramenta** ("AutoCAD ocupado", "layer não existe") volta como resultado com
  `isError: true`, para o **modelo ler e orientar o usuário**. Erro de **protocolo** (método
  inexistente, JSON inválido) usa o `error` do JSON-RPC (`-32601`, `-32700`, `-32603`).
- A `description` de cada ferramenta é o contrato com o modelo. Ferramentas que alteram o desenho
  começam com **"ALTERA O DESENHO"**.

### O canal local (plugin ⇄ MCP)
```jsonc
// %APPDATA%\AcadBridge\<pid>.lock   (criado na abertura, apagado ao fechar)
{"pid":1234,"port":52225,"token":"<64 hex>","protocol":"acadbridge-ndjson/0.2","plugin_version":"0.3.0",...}
// requisição (uma conexão = uma linha)
{"token":"<64 hex>","id":1,"method":"list_layers","params":{"include_counts":true,"document_id":2,"acad_pid":1234}}
// resposta
{"id":1,"ok":true,"result":{...},"document":{"id":2,"name":"Planta.dwg","path":"C:\\...","acad_pid":1234}}
```
- Escuta **só em 127.0.0.1**, porta aleatória. O **token** é aleatório (256 bits) e comparado em
  tempo constante. Requisições acima de 1 MB são recusadas.
- O MCP só aceita lock cujo `pid` seja um `acad.exe` vivo. Lock velho de um crash é ignorado.

### Dentro do AutoCAD
- **Thread principal**: a API do AutoCAD só funciona nela. As requisições chegam numa thread do pool
  e são agendadas com `Control.BeginInvoke`. Nenhuma exceção escapa do delegate (senão o WinForms
  mostraria a janela de "exceção não tratada", com botão *Quit*). Se a thread principal não atender
  em 30 s (por exemplo, com um diálogo aberto), o pedido é **cancelado na fila**: ele não roda mais
  tarde. Um estado trocado com `Interlocked.CompareExchange` garante que vale **ou** a execução **ou**
  o cancelamento.
- **Leitura**: `OpenCloseTransaction` sem `Commit` e tudo `ForRead`. Contagem por tipo via
  `ObjectId.ObjectClass` (sem abrir o objeto).
- **Escrita**: `LockDocument(Write, "ACADBRIDGE_<MÉTODO>", …)`. Um lock com **nome de comando** faz o
  AutoCAD abrir **um grupo de undo**, então um `U` desfaz a ação inteira. Antes disso:
  1. modo somente leitura?
  2. `UNDOCTL` ligado?
  3. **valida todos os parâmetros**;
  4. se nada mudaria, responde sem abrir o lock, para não gravar um undo vazio.

  Exceção antes do `Commit` = nada gravado. Cada ação escreve uma linha na linha de comando:
  `[AcadBridge] IA criou a layer 'X'. U desfaz.`
- **Vários desenhos**: cada documento aberto ganha um id (chave = ponteiro nativo, estável no
  `SAVEAS`). O MCP fixa a conversa no desenho da 1ª chamada bem-sucedida e manda
  `document_id` + `acad_pid` em toda chamada. O plugin **recusa antes de ler ou escrever** se a aba
  ativa for outra. Ele nunca troca de aba sozinho.

### Instalação no AutoCAD (bundle)
O plugin é instalado como `%APPDATA%\Autodesk\ApplicationPlugins\AcadBridge.bundle\`, o formato da
Autodesk App Store. O AutoCAD lê o `PackageContents.xml` na abertura (`SeriesMin/Max = R25.0`,
`LoadOnAutoCADStartup = True`) e carrega a DLL sozinho, sem `NETLOAD`. Como a DLL não é assinada, o
`SECURELOAD` pergunta uma vez **por versão** do plugin (**Always Load**).

---

## Ferramentas

| Ferramenta | Tipo | O que faz |
|---|---|---|
| `acad_status` | leitura | AutoCAD encontrado? pid, porta, versões, edição liberada, desenho fixado |
| `list_documents` | leitura | abas abertas: id, nome, caminho, ativa, fixada |
| `set_target_document` | conversa | fixa a conversa em outro desenho (só quando o usuário pedir) |
| `get_drawing_summary` | leitura | arquivo, unidades, alterações não salvas, layer atual, extensão, entidades por tipo, layouts, contagens |
| `list_layers` | leitura | estado, cor, tipo/espessura de linha, descrição, nº de entidades |
| `list_blocks` | leitura | inserções (inclui variações `*U` de blocos dinâmicos), atributos, dinâmico/xref |
| `get_selection` | leitura | objetos selecionados: medidas, área, raio, texto, cotas, atributos e propriedades dinâmicas |
| `capture_view` | leitura | PNG da vista atual (para ver, não para medir) |
| `create_layer` | **altera** (acrescenta) | cor (ACI, RGB ou nome), tipo de linha (carrega de `acadiso.lin`), espessura, plotável, descrição, tornar atual |
| `update_layer` | **altera** | só o que for informado; devolve **de → para**; pedido sem mudança não grava undo |
| `move_selection_to_layer` | **altera** | move a seleção; pula layers travadas e avisa |

---

## Segurança e privacidade

- **Rede**: só loopback (`127.0.0.1`) + token por sessão do AutoCAD. Nenhuma porta externa.
- **Controle do usuário**: `U` desfaz cada ação; `ACADBRIDGE_READONLY` bloqueia edições;
  `ACADBRIDGE_STOP` corta o acesso. A IA não age com um comando em andamento nem em outra aba.
- **SECURELOAD continua ligado**: liberamos só esta DLL (Always Load), sem desligar a proteção contra
  plugins maliciosos.
- **Privacidade**: o que a IA lê (dados do desenho e imagens da vista) vai para o provedor do modelo
  que o agente usa (OpenAI, Anthropic…), como qualquer conteúdo de conversa.

---

## Compatibilidade e limitações

- **AutoCAD 2025 completo** (e verticais, ex. Architecture), Windows x64. **Não** funciona no
  AutoCAD LT (sem .NET). Outras versões não foram testadas (o bundle aceita só a série R25.0).
- Um AutoCAD por vez: se houver vários abertos, vale o aberto mais recentemente.
- `get_selection` e `move_selection_to_layer` usam a seleção feita **sem comando ativo** (pickfirst).
- `extents` é calculado pelas entidades do Model. Acima de 50 mil entidades, usa `EXTMIN/EXTMAX`, que
  o AutoCAD só atualiza em certos momentos (ex.: `ZOOM E`); a resposta indica a origem (`source`).
- `list_layers` com contagem abre todas as entidades: pode demorar em desenhos enormes
  (`include_counts: false`).

---

## Desenvolvimento

```
AcadBridge.Mcp/        servidor MCP (net8.0, console). Program.cs = protocolo; Tools.cs = catálogo; BridgeClient.cs = canal local
AcadBridge.Plugin/     plugin (net8.0-windows, x64). BridgeServer.cs = TCP + thread principal + undo;
                       DrawingQueries.cs = leitura; DrawingEdits.cs = escrita; DocumentRegistry.cs = vários desenhos
bundle/                PackageContents.xml (modelo) + install-bundle.ps1 (compila e monta o .bundle)
setup.ps1              instalação completa: pré-requisitos, MCP, bundle, registro no agente, verificação
scripts/build-release.ps1   gera dist/AcadBridge.zip (o pacote pronto das Releases)
.github/workflows/     CI: compila em Windows limpo; em tag v* publica a Release
docs/INSTALACAO.md     roteiro que o agente de IA segue
00-hello/              primeiro plugin de estudo (comando OLAPONTE); não é instalado
```

```powershell
dotnet build AcadBridge.Mcp -c Release
dotnet build AcadBridge.Plugin -c Release
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1               # gera dist\AcadBridge.zip
powershell -ExecutionPolicy Bypass -File setup.ps1 -Agent claude -Source build   # instala e registra (AutoCAD fechado)
```

- Requisitos para compilar: só o .NET SDK ≥ 8. A API do AutoCAD vem dos pacotes NuGet oficiais
  `AutoCAD.NET` 25.0.x, com `ExcludeAssets=runtime`: as DLLs `acdbmgd/acmgd/accoremgd` servem só para
  compilar e nunca vão para o bundle. Em execução valem as da instalação do AutoCAD. Por isso o CI
  compila sem AutoCAD.
- **Publicar uma versão**: ajuste `<Version>` nos dois `.csproj`, envie o commit e, **num push
  separado**, `git tag vX.Y.Z` e `git push origin vX.Y.Z`. O workflow confere se a tag bate com as versões, gera o zip e cria a
  Release. O `setup.ps1` baixa sempre a **última** Release
  (`releases/latest/download/AcadBridge.zip`).
- Com o bundle, o AutoCAD trava a **cópia instalada**, não `bin\`. Dá para **compilar** com ele aberto;
  só **instalar** exige fechá-lo. Não use `NETLOAD` no AcadBridge (carregaria duas vezes).
- Versão: `<Version>` de cada `.csproj` é a única fonte. Subir a versão do plugin faz o AutoCAD
  pedir "Always Load" de novo.
- Teste sem cliente: mande linhas JSON-RPC no stdin do `AcadBridge.Mcp.exe` (`initialize`,
  `notifications/initialized`, `tools/list`, `tools/call`). O `setup.ps1 -Verify` faz isso.
- Scripts `.ps1` ficam em **UTF-8 com BOM**: o Windows PowerShell 5.1 lê arquivos sem BOM como ANSI.

### Como adicionar uma ferramenta

1. **Plugin**: implemente o método.
   - Leitura vai em `DrawingQueries.cs` e entra no `switch` de `BridgeServer.Execute`.
   - Escrita vai em `DrawingEdits.cs` e é registrada em `DrawingEdits.Methods`. O `BridgeServer` já
     abre o lock nomeado, que vira um grupo de undo.
2. **MCP**: acrescente o nome em `Tools.BridgeMethods` e a definição em `Tools.Definitions()`: nome,
   `description` (é o que o modelo lê para decidir quando usar), `inputSchema` e o tipo
   (`ReadOnly`/`Additive`/`Modifies`, que gera as `annotations`).
3. **Escrita**:
   - valide **todos** os parâmetros antes da 1ª alteração;
   - se o pedido puder não mudar nada, trate em `TryNoOp`, para não gravar um undo vazio;
   - termine com `Notify(...)`;
   - a `description` começa com "ALTERA O DESENHO".
4. Regras completas e o que não pode quebrar: [`AGENTS.md`](AGENTS.md). Esse arquivo também serve
   para agentes que forem alterar o código.

Ideias de próximas ferramentas: cotar a seleção, inserir bloco com atributos, preencher atributos em
lote, auditoria de padrão de layers, ler um desenho não ativo (somente leitura).

### Contribuindo

Issues e pull requests são bem-vindos. Ao propor uma ferramenta, conte **qual tarefa repetitiva do
dia a dia** ela resolve. É isso que guia o que entra no projeto.

---

Licença: [MIT](LICENSE).
