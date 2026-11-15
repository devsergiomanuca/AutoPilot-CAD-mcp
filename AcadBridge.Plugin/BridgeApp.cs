using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AcadBridge.Plugin.BridgeApp))]
[assembly: CommandClass(typeof(AcadBridge.Plugin.BridgeCommands))]

namespace AcadBridge.Plugin;

/// <summary>
/// Ponto de entrada: o AutoCAD chama Initialize() no NETLOAD (na thread principal)
/// e Terminate() ao fechar.
/// </summary>
public sealed class BridgeApp : IExtensionApplication
{
    // Versão vem do <Version> do .csproj (única fonte da verdade).
    public static readonly string Version = typeof(BridgeApp).Assembly.GetName().Version!.ToString(3);
    internal static BridgeServer? Server;

    /// <summary>Chave de segurança do usuário: ACADBRIDGE_READONLY liga/desliga. Vale até fechar o AutoCAD.</summary>
    internal static volatile bool ReadOnlyMode;

    public void Initialize()
    {
        try
        {
            Server = new BridgeServer();
            Server.Start();
            Write($"\n[AcadBridge] v{Version} ativo em 127.0.0.1:{Server.Port} ({ModeText}). Digite ACADBRIDGE para status.\n");
        }
        catch (System.Exception ex)
        {
            Write($"\n[AcadBridge] falha ao iniciar: {ex.Message}\n");
        }
    }

    public void Terminate()
    {
        Server?.Dispose();
        Server = null;
    }

    internal static string ModeText => ReadOnlyMode
        ? "SOMENTE LEITURA — a IA não altera o desenho"
        : "leitura + edição com undo — ACADBRIDGE_READONLY bloqueia edições";

    internal static void Write(string msg)
    {
        try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(msg); } catch { /* sem documento */ }
    }
}

public sealed class BridgeCommands
{
    [CommandMethod("ACADBRIDGE")]
    public void Status()
    {
        Editor? ed = AcApp.DocumentManager.MdiActiveDocument?.Editor;
        if (ed == null) return;
        BridgeServer? s = BridgeApp.Server;
        if (s == null || !s.Running)
        {
            ed.WriteMessage("\n[AcadBridge] parado. Use ACADBRIDGE_START.\n");
            return;
        }
        ed.WriteMessage(
            $"\n[AcadBridge] v{BridgeApp.Version} | porta {s.Port} | requisições atendidas: {s.Requests}" +
            $"\n  modo: {BridgeApp.ModeText}" +
            $"\n  lock: {s.LockPath}" +
            $"\n  último erro: {s.LastError ?? "-"}\n");
    }

    [CommandMethod("ACADBRIDGE_STOP")]
    public void Stop()
    {
        BridgeApp.Server?.Dispose();
        BridgeApp.Server = null;
        BridgeApp.Write("\n[AcadBridge] parado. A IA não consegue mais acessar este AutoCAD.\n");
    }

    [CommandMethod("ACADBRIDGE_READONLY")]
    public void ToggleReadOnly()
    {
        BridgeApp.ReadOnlyMode = !BridgeApp.ReadOnlyMode;
        BridgeApp.Write($"\n[AcadBridge] modo: {BridgeApp.ModeText}.\n");
    }

    [CommandMethod("ACADBRIDGE_START")]
    public void Start()
    {
        if (BridgeApp.Server is { Running: true }) { Status(); return; }
        new BridgeApp().Initialize();
    }
}
