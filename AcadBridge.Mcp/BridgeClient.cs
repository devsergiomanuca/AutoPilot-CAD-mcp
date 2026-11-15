using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;

namespace AcadBridge.Mcp;

/// <summary>
/// Lado cliente do canal local: acha o lock file do AutoCAD vivo, conecta em 127.0.0.1:porta,
/// manda UMA requisição NDJSON com o token e lê UMA resposta.
/// TCP em loopback funciona mesmo se este processo estiver elevado (admin) e o AutoCAD não —
/// diferente do COM, que falha entre níveis de integridade diferentes.
/// </summary>
internal static class BridgeClient
{
    private const int MaxResponseBytes = 64 * 1024 * 1024;

    public static string LockDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AcadBridge");

    public sealed record LockInfo(int Pid, int Port, string Token, string? StartedAt, string? AcadVersion, string Path);

    public static LockInfo FindLock()
    {
        if (!Directory.Exists(LockDirectory))
            throw new InvalidOperationException(NotFoundMessage);

        var alive = new List<LockInfo>();
        foreach (string file in Directory.GetFiles(LockDirectory, "*.lock"))
        {
            try
            {
                JsonNode? j = JsonNode.Parse(File.ReadAllText(file));
                if (j == null) continue;
                var info = new LockInfo(
                    j["pid"]!.GetValue<int>(), j["port"]!.GetValue<int>(), j["token"]!.GetValue<string>(),
                    j["started_at"]?.GetValue<string>(), j["acad_version"]?.GetValue<string>(), file);
                using Process p = Process.GetProcessById(info.Pid);   // lança se o processo não existe
                if (!p.HasExited && p.ProcessName.Equals("acad", StringComparison.OrdinalIgnoreCase))
                    alive.Add(info);
            }
            catch { /* lock velho/corrompido: ignora */ }
        }
        return alive.OrderByDescending(l => l.StartedAt).FirstOrDefault()
               ?? throw new InvalidOperationException(NotFoundMessage);
    }

    private const string NotFoundMessage =
        "Nenhum AutoCAD com o AcadBridge ativo foi encontrado. Peça ao usuário para abrir o AutoCAD 2025 " +
        "(o plugin carrega sozinho; se aparecer o aviso de segurança, clicar em 'Always Load'). " +
        "Para conferir, digitar ACADBRIDGE no AutoCAD.";

    /// <summary>Resposta do plugin: o resultado e EM QUAL desenho ele foi obtido (null em plugin &lt; 0.3 ou list_documents).</summary>
    public sealed record Reply(JsonNode Result, JsonObject? Document);

    public static async Task<JsonNode> CallAsync(string method, JsonObject? parameters, int timeoutMs = 60_000) =>
        (await CallFullAsync(method, parameters, timeoutMs)).Result;

    public static async Task<Reply> CallFullAsync(string method, JsonObject? parameters, int timeoutMs = 60_000)
    {
        try { return await CallCoreAsync(method, parameters, timeoutMs); }
        catch (OperationCanceledException)
        {
            // O plugin responde sozinho em ~30 s (e cancela o pedido se ele nem começou). Chegar aqui
            // significa que o trabalho começou e demorou: não dá para afirmar que nada mudou.
            throw new TimeoutException(
                $"O AutoCAD não respondeu em {timeoutMs / 1000} s. Se o pedido era uma alteração, ela PODE ter " +
                "acontecido: confira o estado (ex.: list_layers) antes de repetir.");
        }
    }

    private static async Task<Reply> CallCoreAsync(string method, JsonObject? parameters, int timeoutMs)
    {
        LockInfo lk = FindLock();
        using var cts = new CancellationTokenSource(timeoutMs);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, lk.Port, cts.Token);
        NetworkStream stream = client.GetStream();

        var request = new JsonObject
        {
            ["token"] = lk.Token,
            ["id"] = 1,
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject(),
        };
        await stream.WriteAsync(Encoding.UTF8.GetBytes(request.ToJsonString() + "\n"), cts.Token);

        var buffer = new MemoryStream();
        var chunk = new byte[65536];
        while (true)
        {
            int n = await stream.ReadAsync(chunk, cts.Token);
            if (n == 0) break;
            int nl = Array.IndexOf(chunk, (byte)'\n', 0, n);
            if (nl >= 0) { buffer.Write(chunk, 0, nl); break; }
            buffer.Write(chunk, 0, n);
            if (buffer.Length > MaxResponseBytes) throw new InvalidDataException("Resposta do AutoCAD grande demais.");
        }

        JsonNode response = JsonNode.Parse(Encoding.UTF8.GetString(buffer.ToArray()))
                            ?? throw new InvalidDataException("Resposta vazia do AutoCAD.");
        if (response["ok"]?.GetValue<bool>() != true)
            throw new InvalidOperationException(response["error"]?.GetValue<string>() ?? "Erro desconhecido no AutoCAD.");
        return new Reply(response["result"]!.DeepClone(), response["document"]?.DeepClone() as JsonObject);
    }
}
