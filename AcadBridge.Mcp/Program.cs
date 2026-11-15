using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AcadBridge.Mcp;

/// <summary>
/// Servidor MCP mínimo, escrito à mão (sem SDK) para estudo do protocolo.
/// Transporte stdio: cada mensagem JSON-RPC 2.0 é UMA linha em stdin/stdout.
/// stdout é exclusivo do protocolo; logs vão para stderr.
///
/// Fluxo: initialize → notifications/initialized → tools/list → tools/call (quantas vezes quiser).
/// </summary>
internal static class Program
{
    // Versão vem do <Version> do .csproj (única fonte da verdade).
    private static readonly string Version = typeof(Program).Assembly.GetName().Version!.ToString(3);
    // UnsafeRelaxedJsonEscaping: mantém "+", acentos e "ç" legíveis (o texto vai para o modelo, não para HTML).
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Desenho em que ESTA conversa está fixada (v0.3). Um processo MCP stdio = uma sessão do cliente
    /// (Claude Code/Codex), então um campo estático é exatamente "estado da conversa".
    /// Pid junto: ids de documento recomeçam quando o AutoCAD reabre.
    /// </summary>
    private sealed record Target(int Id, string Name, int Pid);
    private static Target? _target;

    private static async Task<int> Main()
    {
        var stdin = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        Log($"acad-bridge MCP v{Version} iniciado. Locks em {BridgeClient.LockDirectory}");

        string? line;
        while ((line = await stdin.ReadLineAsync()) != null)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonNode? msg;
            try { msg = JsonNode.Parse(line); }
            catch (Exception ex) { await Send(stdout, Error(null, -32700, "Parse error: " + ex.Message)); continue; }

            string? method = msg?["method"]?.GetValue<string>();
            JsonNode? id = msg?["id"]?.DeepClone();
            if (method == null) continue;  // resposta vinda do cliente: não usamos
            if (id == null) continue;      // notificação (ex.: notifications/initialized): não se responde

            JsonObject response;
            try
            {
                response = method switch
                {
                    "initialize" => Result(id, Initialize(msg!["params"])),
                    "ping" => Result(id, new JsonObject()),
                    "tools/list" => Result(id, new JsonObject { ["tools"] = Tools.Definitions() }),
                    "tools/call" => Result(id, await CallToolAsync(msg!["params"])),
                    _ => Error(id, -32601, $"Method not found: {method}"),
                };
            }
            catch (Exception ex)
            {
                response = Error(id, -32603, ex.Message);
            }
            await Send(stdout, response);
        }
        return 0;
    }

    private static JsonObject Initialize(JsonNode? p)
    {
        // Negociação de versão: devolvemos a que o cliente pediu (usamos só recursos básicos).
        string requested = p?["protocolVersion"]?.GetValue<string>() ?? "2025-06-18";
        Log($"initialize: cliente={p?["clientInfo"]?["name"]} protocolo={requested}");
        return new JsonObject
        {
            ["protocolVersion"] = requested,
            ["capabilities"] = new JsonObject { ["tools"] = new JsonObject { ["listChanged"] = false } },
            ["serverInfo"] = new JsonObject { ["name"] = "acad-bridge", ["title"] = "AutoCAD Bridge", ["version"] = Version },
            ["instructions"] =
                "Ponte para o AutoCAD 2025 aberto neste PC. " +
                "Comece por get_drawing_summary. Use get_selection para o que o usuário selecionou e " +
                "capture_view para ver a vista atual. Unidades vêm em 'units' (ex.: Millimeters). " +
                "Ferramentas marcadas 'ALTERA O DESENHO' modificam o arquivo do usuário: só use quando ele pedir " +
                "uma alteração, diga o que vai fazer e, depois, o que mudou. Cada chamada vira UM passo de undo " +
                "(o usuário desfaz com U). Respeite os padrões existentes (nomes/cores de layers) — consulte antes. " +
                "Se o AutoCAD estiver ocupado com um comando, peça ao usuário para terminar ou apertar Esc. " +
                "VÁRIOS DESENHOS: as ferramentas agem na aba ATIVA do AutoCAD. A primeira chamada fixa esta conversa " +
                "nessa aba; toda resposta começa com 'Desenho: ...'. Se o usuário trocar de aba, as chamadas são " +
                "recusadas (nada é lido nem alterado): pergunte se ele quer trabalhar no outro desenho e, se sim, use " +
                "set_target_document; se não, peça para ele voltar à aba anterior.",
        };
    }

    private static async Task<JsonObject> CallToolAsync(JsonNode? p)
    {
        string name = p?["name"]?.GetValue<string>() ?? "";
        JsonObject args = p?["arguments"] as JsonObject ?? new JsonObject();
        Log($"tools/call {name} {args.ToJsonString()}");
        try
        {
            if (name == "acad_status")
            {
                BridgeClient.LockInfo lk = BridgeClient.FindLock();
                JsonNode pong = await BridgeClient.CallAsync("ping", null, 5_000);
                return Text(JsonSerializer.Serialize(new
                {
                    connected = true, pid = lk.Pid, port = lk.Port, acad_version = lk.AcadVersion,
                    started_at = lk.StartedAt, plugin_version = pong["plugin_version"]?.GetValue<string>(),
                    // plugin 0.1.x não manda este campo → null = versão sem edição
                    writes_enabled = pong["writes_enabled"]?.GetValue<bool>(),
                    target_document = _target is { } t && t.Pid == lk.Pid ? new { id = t.Id, name = t.Name } : null,
                }, Pretty));
            }

            if (name == "set_target_document") return await SetTargetAsync(args);

            if (name == "list_documents")
            {
                JsonNode list = await BridgeClient.CallAsync("list_documents", null);
                bool samePid = _target != null && list["acad_pid"]?.GetValue<int>() == _target.Pid;
                list["target_document_id"] = samePid ? _target!.Id : null;
                return Text(list.ToJsonString(Pretty));
            }

            if (!Tools.BridgeMethods.Contains(name))
                return Text($"Ferramenta desconhecida: {name}", isError: true);

            JsonObject sent = args.DeepClone().AsObject();
            if (_target != null) { sent["document_id"] = _target.Id; sent["acad_pid"] = _target.Pid; }
            BridgeClient.Reply reply = await BridgeClient.CallFullAsync(name, sent);
            JsonNode result = reply.Result;
            string where = DocumentHeader(reply.Document);

            if (name == "capture_view")
            {
                string data = result["base64"]!.GetValue<string>();
                // Tamanho real lido do próprio PNG (fonte da verdade; funciona até com plugin antigo,
                // que devolvia o tamanho PEDIDO em width/height).
                (int w, int h) = PngSize(Convert.FromBase64String(data));
                int reqW = Math.Clamp(args["width"]?.GetValue<int>() ?? 1280, 256, 2048);
                int reqH = Math.Clamp(args["height"]?.GetValue<int>() ?? 800, 256, 2048);
                string size = w == reqW && h == reqH
                    ? $"{w}x{h} px"
                    : $"{w}x{h} px (pedido {reqW}x{reqH}; o AutoCAD mantém a proporção da janela)";
                return new JsonObject
                {
                    ["content"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "image", ["data"] = data, ["mimeType"] = "image/png" },
                        new JsonObject { ["type"] = "text", ["text"] = $"{where}Vista atual do AutoCAD: {size}." },
                    },
                    ["isError"] = false,
                };
            }
            return Text(where + result.ToJsonString(Pretty));
        }
        catch (Exception ex)
        {
            // Erro de ferramenta vai no resultado (isError), não como erro JSON-RPC:
            // assim o modelo lê a mensagem e pode orientar o usuário.
            Log($"erro em {name}: {ex.Message}");
            string msg = ex.Message;
            if (msg.StartsWith("Método desconhecido", StringComparison.Ordinal))
                msg += " O plugin carregado no AutoCAD é mais antigo que este servidor MCP: peça ao usuário para " +
                       "salvar e fechar o AutoCAD, rode o setup.ps1 do repositório AcadBridge de novo e abra o AutoCAD.";
            return Text(msg, isError: true);
        }
    }

    /// <summary>
    /// Linha "Desenho: ..." no topo de cada resposta. Na 1ª resposta da conversa, fixa o alvo.
    /// Plugin &lt; 0.3 não manda "document" → sem cabeçalho e sem fixação (comportamento antigo).
    /// </summary>
    private static string DocumentHeader(JsonObject? doc)
    {
        if (doc == null) return "";
        int id = doc["id"]!.GetValue<int>();
        string name = doc["name"]!.GetValue<string>();
        int pid = doc["acad_pid"]!.GetValue<int>();
        if (_target == null)
        {
            _target = new Target(id, name, pid);
            Log($"conversa fixada em '{name}' (id {id}, pid {pid})");
            return $"Desenho: '{name}' (id {id}). Esta conversa ficou FIXADA neste desenho: se o usuário trocar de aba, " +
                   "as próximas chamadas serão recusadas até ele voltar ou você usar set_target_document.\n\n";
        }
        _target = _target with { Name = name };   // SAVEAS muda o nome, o id continua
        return $"Desenho: '{name}' (id {id}, fixado nesta conversa).\n\n";
    }

    private static async Task<JsonObject> SetTargetAsync(JsonObject args)
    {
        int? wantedId = args["document_id"]?.GetValue<int>();
        string? wantedName = args["document"]?.GetValue<string>()?.Trim();
        if (wantedId == null && string.IsNullOrEmpty(wantedName))
            return Text("Informe document_id (veja list_documents) ou document (nome do arquivo).", isError: true);

        JsonNode list = await BridgeClient.CallAsync("list_documents", null);
        int pid = list["acad_pid"]!.GetValue<int>();
        var docs = list["documents"]!.AsArray().Select(d => d!).ToList();

        static bool NameMatches(JsonNode d, string w)
        {
            string n = d["name"]!.GetValue<string>();
            string? path = d["path"]?.GetValue<string>();
            return n.Equals(w, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(n).Equals(w, StringComparison.OrdinalIgnoreCase)
                || (path != null && path.Equals(w, StringComparison.OrdinalIgnoreCase));
        }
        var matches = wantedId != null
            ? docs.Where(d => d["id"]!.GetValue<int>() == wantedId).ToList()
            : docs.Where(d => NameMatches(d, wantedName!)).ToList();

        string available = string.Join("; ", docs.Select(d =>
            $"id {d["id"]} = '{d["name"]}'" + (d["path"] is { } p ? $" ({p})" : " (nunca salvo)")));
        if (matches.Count == 0)
            return Text($"Nenhum desenho aberto corresponde. Abertos: {available}.", isError: true);
        if (matches.Count > 1)
            return Text($"Mais de um desenho com esse nome — use document_id. Abertos: {available}.", isError: true);

        JsonNode m = matches[0];
        _target = new Target(m["id"]!.GetValue<int>(), m["name"]!.GetValue<string>(), pid);
        Log($"conversa fixada em '{_target.Name}' (id {_target.Id}) via set_target_document");
        string text = $"Conversa fixada em '{_target.Name}' (id {_target.Id}).";
        if (m["active"]?.GetValue<bool>() != true)
        {
            string active = docs.FirstOrDefault(d => d["active"]?.GetValue<bool>() == true)?["name"]?.GetValue<string>() ?? "nenhuma";
            text += $" Atenção: essa NÃO é a aba ativa agora ('{active}'). As chamadas serão recusadas até o usuário " +
                    $"trocar para a aba '{_target.Name}' no AutoCAD.";
        }
        return Text(text);
    }

    // PNG = assinatura de 8 bytes + chunk IHDR (4 tamanho + 4 "IHDR" + 4 largura + 4 altura, big-endian).
    // Largura nos bytes 16..19, altura em 20..23.
    private static (int Width, int Height) PngSize(byte[] png)
    {
        if (png.Length < 24 || png[0] != 0x89 || png[1] != (byte)'P' || png[12] != (byte)'I' || png[13] != (byte)'H')
            return (0, 0);
        return (System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)),
                System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
    }

    private static JsonObject Text(string text, bool isError = false) => new()
    {
        ["content"] = new JsonArray { new JsonObject { ["type"] = "text", ["text"] = text } },
        ["isError"] = isError,
    };

    private static JsonObject Result(JsonNode id, JsonNode result) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result };

    private static JsonObject Error(JsonNode? id, int code, string message) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } };

    private static readonly JsonSerializerOptions Wire = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static Task Send(StreamWriter stdout, JsonObject message) => stdout.WriteLineAsync(message.ToJsonString(Wire));

    private static void Log(string message) => Console.Error.WriteLine($"[acad-bridge] {message}");
}
