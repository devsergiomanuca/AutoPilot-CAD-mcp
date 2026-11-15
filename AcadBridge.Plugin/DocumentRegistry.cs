using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadBridge.Plugin;

/// <summary>
/// Vários desenhos abertos (abas) — v0.3.
///
/// Problema real (visto na v0.2): a ponte agia sempre no desenho ATIVO; com duas abas, a IA leu a aba
/// errada sem perceber. Solução:
///   1. cada documento aberto ganha um id curto (1, 2, 3…) que vale enquanto ele estiver aberto —
///      não muda se o usuário fizer SAVEAS (o nome muda, o id não);
///   2. toda resposta diz em QUAL desenho a ação aconteceu;
///   3. o servidor MCP "fixa" a conversa num desenho e manda document_id + acad_pid em toda chamada;
///      se a aba ativa não for a fixada, o plugin RECUSA antes de ler ou mexer em qualquer coisa.
///
/// Por que não trocar de aba sozinho? Mudar a aba debaixo do usuário (ou editar um desenho que ele
/// não está vendo) é exatamente o tipo de surpresa que queremos evitar: ele não veria a mudança nem
/// saberia onde dar U.
///
/// Tudo aqui roda na thread principal (via RunOnMainThread) → sem necessidade de lock.
/// </summary>
internal static class DocumentRegistry
{
    // Chave = ponteiro do objeto nativo (AcApDocument*). O wrapper .NET pode ser recriado; o objeto
    // nativo vive enquanto o documento estiver aberto. Ao fechar, removemos (o ponteiro pode ser reusado).
    private static readonly Dictionary<IntPtr, int> Ids = new();
    private static int _next;
    private static bool _hooked;

    public static int IdOf(Document doc)
    {
        Hook();
        IntPtr key = doc.UnmanagedObject;
        if (!Ids.TryGetValue(key, out int id))
        {
            id = ++_next;
            Ids[key] = id;
        }
        return id;
    }

    private static void Hook()
    {
        if (_hooked) return;
        _hooked = true;
        AcApp.DocumentManager.DocumentToBeDestroyed += (_, e) =>
        {
            try { Ids.Remove(e.Document.UnmanagedObject); } catch { /* fechando */ }
        };
    }

    /// <summary>Nome curto (só o arquivo) — é o que o usuário vê na aba.</summary>
    public static string ShortName(Document doc) => Path.GetFileName(doc.Name);

    /// <summary>Caminho completo se o desenho já foi salvo; null para desenho novo (Drawing1.dwg).</summary>
    public static string? SavedPath(Document doc) => Path.IsPathRooted(doc.Name) ? doc.Name : null;

    /// <summary>Descrição curta anexada a toda resposta ("em qual desenho isso aconteceu").</summary>
    public static object Describe(Document doc) => new
    {
        id = IdOf(doc),
        name = ShortName(doc),
        path = SavedPath(doc),
        acad_pid = Environment.ProcessId,
    };

    public static object List()
    {
        Document? active = AcApp.DocumentManager.MdiActiveDocument;
        var docs = new List<object>();
        foreach (Document d in AcApp.DocumentManager)
        {
            docs.Add(new
            {
                id = IdOf(d),
                name = ShortName(d),
                path = SavedPath(d),
                active = active != null && d.UnmanagedObject == active.UnmanagedObject,
                read_only = d.IsReadOnly,
            });
        }
        return new
        {
            count = docs.Count,
            active_id = active == null ? (int?)null : IdOf(active),
            acad_pid = Environment.ProcessId,
            documents = docs,
        };
    }

    /// <summary>
    /// Se o pedido veio "fixado" (document_id), confere que a aba ativa é a fixada. Chamado ANTES de
    /// qualquer leitura/escrita: se recusar, nada foi lido nem alterado.
    /// </summary>
    public static void EnsureTarget(Document active, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object) return;
        if (!p.TryGetProperty("document_id", out JsonElement idEl) || idEl.ValueKind != JsonValueKind.Number) return;
        int wanted = idEl.GetInt32();

        // ids recomeçam em 1 quando o AutoCAD reabre: o pid diz se ainda é o mesmo AutoCAD.
        if (p.TryGetProperty("acad_pid", out JsonElement pidEl) && pidEl.ValueKind == JsonValueKind.Number &&
            pidEl.GetInt32() != Environment.ProcessId)
            throw new InvalidOperationException(
                "O AutoCAD foi fechado e aberto de novo desde que esta conversa fixou um desenho. Nada foi feito. " +
                "Chame list_documents e, com o usuário, escolha o desenho com set_target_document.");

        int activeId = IdOf(active);
        if (activeId == wanted) return;

        Document? target = AcApp.DocumentManager.Cast<Document>().FirstOrDefault(d => IdOf(d) == wanted);
        if (target == null)
            throw new InvalidOperationException(
                $"O desenho em que esta conversa estava fixada (id {wanted}) foi fechado. Nada foi feito. " +
                $"A aba ativa agora é '{ShortName(active)}' (id {activeId}). Confirme com o usuário e use " +
                "set_target_document se ele quiser trabalhar nela.");

        throw new InvalidOperationException(
            $"Esta conversa está fixada no desenho '{ShortName(target)}' (id {wanted}), mas a aba ativa no AutoCAD " +
            $"é '{ShortName(active)}' (id {activeId}). Nada foi lido nem alterado. Peça ao usuário para voltar à aba " +
            $"'{ShortName(target)}' — ou, se ele quiser trabalhar em '{ShortName(active)}', chame set_target_document " +
            $"com document_id {activeId}.");
    }
}
