# AGENTS.md: instruções para agentes de IA neste repositório

## Se o usuário pediu para instalar, atualizar ou remover o AcadBridge
Siga **[docs/INSTALACAO.md](docs/INSTALACAO.md)** à risca. Por padrão, trate o usuário como não
técnico (arquiteto, projetista…).

## Se o usuário quer usar o AutoCAD
As ferramentas do servidor MCP `acad-bridge` fazem o trabalho. As regras de uso chegam nas
`instructions` do próprio servidor: consulte antes de alterar, diga o que vai fazer e o que mudou,
e numa recusa por "aba ativa diferente" pergunte ao usuário.

## Se você vai alterar o código

**Mapa**
- `AcadBridge.Mcp/`: servidor MCP stdio (net8.0). `Program.cs` = JSON-RPC à mão e fixação do desenho;
  `Tools.cs` = catálogo (`tools/list`); `BridgeClient.cs` = lock file + TCP até o plugin.
- `AcadBridge.Plugin/`: roda dentro do `acad.exe` (net8.0-windows, x64). `BridgeServer.cs` = TCP,
  token, thread principal e ordem das checagens; `DrawingQueries.cs` = leitura;
  `DrawingEdits.cs` = escrita; `DocumentRegistry.cs` = ids de desenho e recusa por aba errada.
- `bundle/`: manifesto + `install-bundle.ps1`. `setup.ps1`: instalação completa.

**Regras que não podem quebrar**
1. **stdout do MCP é só JSON-RPC.** Qualquer log vai para stderr.
2. **Erro de ferramenta** = resultado com `isError: true` e mensagem que o modelo consiga repassar
   ao usuário. Erro JSON-RPC só para problema de protocolo.
3. **Nova ferramenta de escrita**:
   - registre em `DrawingEdits.Methods` e em `Tools.BridgeMethods`;
   - a `description` começa com "ALTERA O DESENHO" e usa `Kind.Additive`/`Kind.Modifies`;
   - **valide tudo antes da 1ª alteração**;
   - se a operação pode ser no-op, implemente em `TryNoOp`, para não gravar um undo vazio;
   - termine com `Notify(...)`.

   O `BridgeServer` já abre o lock nomeado `ACADBRIDGE_<MÉTODO>`, que vira 1 grupo de undo.
4. **Nada de exceção escapando de `Control.BeginInvoke`**: use sempre `RunOnMainThread`.
5. **Ordem das checagens no plugin** (não inverter):
   1. desenho-alvo (`EnsureTarget`);
   2. comando ativo;
   3. somente leitura e `UNDOCTL`;
   4. validação e no-op;
   5. lock nomeado.
6. **A IA nunca troca a aba ativa** nem age num desenho que o usuário não está vendo.
7. **Compatibilidade entre versões**: MCP novo + plugin antigo deve falhar com mensagem clara, nunca
   com crash. Campos novos no protocolo local são opcionais.
8. Mensagens ao usuário/modelo em **português**.

**Build e teste**
```powershell
dotnet build AcadBridge.Mcp -c Release
dotnet build AcadBridge.Plugin -c Release            # API do AutoCAD via NuGet; compila com ou sem AutoCAD
powershell -ExecutionPolicy Bypass -File scripts\build-release.ps1                # dist\AcadBridge.zip
powershell -ExecutionPolicy Bypass -File setup.ps1 -Agent claude -Source build    # instalar exige AutoCAD fechado
powershell -ExecutionPolicy Bypass -File setup.ps1 -Verify
```
- Não suba os pacotes `AutoCAD.NET*` para 25.0.2: ele foi republicado mirando net10.0.
- Release: `<Version>` igual nos dois `.csproj` → tag `vX.Y.Z` → o CI publica o zip.
- Teste de protocolo sem cliente: linhas JSON-RPC no stdin do `AcadBridge.Mcp.exe` (ver `Test-Mcp` no `setup.ps1`).
- Teste de undo: escolha um caso em que **um** `U` distingue as hipóteses (ex.: a cor tem que voltar).
- Versão: só `<Version>` nos `.csproj`. Subir a do plugin faz o AutoCAD pedir "Always Load" de novo.
- Scripts `.ps1`: **UTF-8 com BOM** e compatíveis com Windows PowerShell 5.1 (sem `??`, `?:`, `&&`).
- Não use `NETLOAD` no AcadBridge com o bundle instalado: carregaria duas vezes.
