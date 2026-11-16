using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadBridge.Plugin;

/// <summary>
/// Servidor TCP local (127.0.0.1, porta aleatória) com protocolo NDJSON:
///   requisição:  {"token":"...","id":1,"method":"get_drawing_summary","params":{}}\n
///   resposta:    {"id":1,"ok":true,"result":{...}}\n   ou   {"id":1,"ok":false,"error":"..."}\n
/// Porta e token são publicados num lock file em %APPDATA%\AcadBridge\&lt;pid&gt;.lock
/// (mesmo padrão de lock file + token usado por extensões de IA para IDEs).
/// </summary>
internal sealed class BridgeServer : IDisposable
{
    private const int MaxRequestBytes = 1024 * 1024;           // 1 MB por requisição
    private static readonly TimeSpan MainThreadTimeout = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _cts = new();
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private readonly System.Windows.Forms.Control _mainThread;  // "ponte" para a thread principal
    private int _requests;

    public int Port { get; private set; }
    public string LockPath { get; private set; } = "";
    public string? LastError { get; private set; }
    public int Requests => _requests;
    public bool Running { get; private set; }

    public BridgeServer()
    {
        // Construído na thread principal (Initialize). O handle do Control fica preso a ela;
        // BeginInvoke a partir de outra thread agenda o código na fila de mensagens do AutoCAD.
        _mainThread = new System.Windows.Forms.Control();
        _ = _mainThread.Handle;
    }

    public void Start()
    {
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        WriteLockFile();
        Running = true;
        _ = Task.Run(AcceptLoopAsync);
    }

    private void WriteLockFile()
    {
        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBridge");
        Directory.CreateDirectory(dir);
        LockPath = Path.Combine(dir, $"{Environment.ProcessId}.lock");
        var info = new
        {
            pid = Environment.ProcessId,
            port = Port,
            token = _token,
            protocol = "acadbridge-ndjson/0.2",
            plugin_version = BridgeApp.Version,
            acad_version = AcApp.Version.ToString(),
            started_at = DateTimeOffset.Now.ToString("o"),
        };
        File.WriteAllText(LockPath, JsonSerializer.Serialize(info));
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(_cts.Token); }
            catch { break; }
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        {
            NetworkStream stream = client.GetStream();
            string response;
            try
            {
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                string? line = await ReadLineAsync(stream, readTimeout.Token);
                response = line == null ? Error(null, "Requisição vazia.") : Process(line);
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                response = Error(null, ex.Message);
            }
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(response + "\n");
                await stream.WriteAsync(bytes);
            }
            catch { /* cliente desconectou */ }
        }
    }

    private string Process(string line)
    {
        using JsonDocument jd = JsonDocument.Parse(line);
        JsonElement root = jd.RootElement;
        JsonElement? id = root.TryGetProperty("id", out JsonElement idEl) ? idEl.Clone() : null;

        string given = root.TryGetProperty("token", out JsonElement t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : "";
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), Encoding.UTF8.GetBytes(_token)))
            return Error(id, "Token inválido.");

        string method = root.TryGetProperty("method", out JsonElement m) && m.ValueKind == JsonValueKind.String ? m.GetString()! : "";
        JsonElement p = root.TryGetProperty("params", out JsonElement pe) ? pe : default;

        Interlocked.Increment(ref _requests);
        try
        {
            if (method == "ping")
            {
                object pong = new { pong = true, plugin_version = BridgeApp.Version, pid = Environment.ProcessId, writes_enabled = !BridgeApp.ReadOnlyMode };
                return JsonSerializer.Serialize(new { id, ok = true, result = pong });
            }
            var done = (Executed)RunOnMainThread(() => Execute(method, p));
            // "document" = em qual desenho isto aconteceu (null para list_documents). Clientes antigos ignoram.
            return JsonSerializer.Serialize(new { id, ok = true, result = done.Result, document = done.Document });
        }
        catch (Exception ex)
        {
            Exception inner = ex is System.Reflection.TargetInvocationException { InnerException: { } ie } ? ie : ex;
            LastError = $"{method}: {inner.Message}";
            return Error(id, inner.Message);
        }
    }

    private const int WorkPending = 0, WorkRunning = 1, WorkCancelled = 2;

    /// <summary>
    /// A API do AutoCAD só pode ser usada na thread principal.
    /// IMPORTANTE: nenhuma exceção pode escapar do delegate. Num BeginInvoke (assíncrono), o WinForms
    /// entrega a exceção ao Application.OnThreadException → janela "unhandled exception" no AutoCAD
    /// (com botão Quit!), mesmo que o EndInvoke também a receba. Bug real visto na v0.2.0 com
    /// erros de validação comuns ("Cor inválida"). Então: captura lá dentro, relança aqui fora.
    ///
    /// TIMEOUT (corrigido na 0.3.1): BeginInvoke só ENFILEIRA o trabalho. Se a thread principal estiver
    /// presa (diálogo modal, comando longo), desistir de esperar não tira o trabalho da fila: ele rodaria
    /// depois, e uma edição aconteceria mesmo com a IA tendo recebido "falhou". Agora o trabalho tem um
    /// estado (pendente → rodando | cancelado) trocado com Interlocked.CompareExchange: exatamente um dos
    /// lados vence. Se o timeout vence, o delegate vira no-op quando finalmente rodar. Se o delegate já
    /// começou, não dá para abortar no meio de uma transação → esperamos ele terminar e devolvemos o
    /// resultado verdadeiro.
    /// </summary>
    private object RunOnMainThread(Func<object> work)
    {
        object? result = null;
        Exception? error = null;
        int state = WorkPending;
        IAsyncResult ar = _mainThread.BeginInvoke(new Action(() =>
        {
            if (Interlocked.CompareExchange(ref state, WorkRunning, WorkPending) != WorkPending) return;   // cancelado: não executa
            try { result = work(); }
            catch (Exception ex) { error = ex; }
        }));
        if (!ar.AsyncWaitHandle.WaitOne(MainThreadTimeout))
        {
            if (Interlocked.CompareExchange(ref state, WorkCancelled, WorkPending) == WorkPending)
                throw new TimeoutException(
                    $"O AutoCAD não respondeu em {MainThreadTimeout.TotalSeconds:0} s (há uma janela aberta ou um comando " +
                    "demorado?). O pedido foi CANCELADO: nada foi lido nem alterado. Peça ao usuário para fechar a " +
                    "janela/terminar o comando e tente de novo.");
            ar.AsyncWaitHandle.WaitOne();   // já está rodando: espera terminar (transação não pode ficar pela metade)
        }
        _mainThread.EndInvoke(ar);
        if (error != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        return result!;
    }

    private sealed record Executed(object Result, object? Document);

    private static Executed Execute(string method, JsonElement p)
    {
        // Não depende de haver desenho ativo (com zero abas abertas devolve lista vazia).
        if (method == "list_documents") return new Executed(DocumentRegistry.List(), null);

        Document doc = AcApp.DocumentManager.MdiActiveDocument
            ?? throw new InvalidOperationException("Nenhum desenho aberto no AutoCAD.");
        DocumentRegistry.EnsureTarget(doc, p);          // aba errada → recusa antes de ler/mexer
        object where = DocumentRegistry.Describe(doc);
        string busy = doc.CommandInProgress;
        if (!string.IsNullOrEmpty(busy))
            throw new InvalidOperationException($"AutoCAD ocupado com o comando '{busy}'. Termine/cancele (Esc) e tente de novo.");

        if (DrawingEdits.Methods.Contains(method))
        {
            // Variante só de leitura (ex.: generate_elevation com dry_run): sem undo e liberada no modo somente leitura.
            using (doc.LockDocument())
            {
                if (DrawingEdits.ReadOnlyVariant(doc, method, p) is { } preview) return new Executed(preview, where);
            }
            DrawingEdits.EnsureWritable();
            using (doc.LockDocument())   // lock SEM nome: só leitura aqui, não abre grupo de undo
            {
                if (DrawingEdits.TryNoOp(doc, method, p) is { } noop) return new Executed(noop, where);
            }
            // Lock de ESCRITA com nome de comando: o AutoCAD trata o bloco inteiro como um "comando"
            // (dispara os eventos de comando e abre/fecha um grupo de undo). Resultado esperado:
            // um único U desfaz tudo o que a ação fez, e o undo mostra o nome abaixo.
            string cmd = "ACADBRIDGE_" + method.ToUpperInvariant();
            using DocumentLock w = doc.LockDocument(DocumentLockMode.Write, cmd, cmd, false);
            return new Executed(DrawingEdits.Run(doc, method, p), where);
        }

        using DocumentLock _ = doc.LockDocument();
        object result = method switch
        {
            "get_drawing_summary" => DrawingQueries.Summary(doc),
            "list_layers" => DrawingQueries.Layers(doc, GetBool(p, "include_counts", true)),
            "list_blocks" => DrawingQueries.Blocks(doc),
            "get_selection" => DrawingQueries.Selection(doc, Math.Clamp(GetInt(p, "max_items", 100), 1, 500)),
            "capture_view" => DrawingQueries.Capture(doc,
                                  (uint)Math.Clamp(GetInt(p, "width", 1280), 256, 2048),
                                  (uint)Math.Clamp(GetInt(p, "height", 800), 256, 2048)),
            "read_geometry" => GeometryReader.Read(doc, p),
            _ => throw new InvalidOperationException($"Método desconhecido: '{method}'."),
        };
        return new Executed(result, where);
    }

    private static bool GetBool(JsonElement p, string name, bool def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : def;

    private static int GetInt(JsonElement p, string name, int def) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int i) ? i : def;

    private static string Error(JsonElement? id, string message) =>
        JsonSerializer.Serialize(new { id, ok = false, error = message });

    private static async Task<string?> ReadLineAsync(NetworkStream s, CancellationToken ct)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            int n = await s.ReadAsync(chunk, ct);
            if (n == 0) break;
            int nl = Array.IndexOf(chunk, (byte)'\n', 0, n);
            if (nl >= 0) { buffer.Write(chunk, 0, nl); break; }
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxRequestBytes) throw new InvalidDataException("Requisição maior que 1 MB.");
        }
        return buffer.Length == 0 ? null : Encoding.UTF8.GetString(buffer.ToArray());
    }

    public void Dispose()
    {
        if (!Running) return;
        Running = false;
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        try { if (File.Exists(LockPath)) File.Delete(LockPath); } catch { }
        try { _mainThread.Dispose(); } catch { }
    }
}
