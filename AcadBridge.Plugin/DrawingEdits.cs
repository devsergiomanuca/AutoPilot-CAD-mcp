using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadBridge.Plugin;

/// <summary>
/// Operações que ALTERAM o desenho (v0.2). Regras de segurança, todas aplicadas antes de mexer em algo:
///  1. o BridgeServer abre um lock de escrita NOMEADO (LockDocument com nome de comando): o AutoCAD trata
///     o bloco como um "comando" e grava tudo num único grupo de undo → um único U desfaz a ação inteira;
///  2. recusa se o undo estiver desligado (UNDOCTL) ou se o usuário ligou o modo somente leitura;
///  3. valida TODOS os parâmetros antes da 1ª alteração (nada de ação pela metade);
///  4. escreve uma linha na linha de comando do AutoCAD dizendo o que foi feito (transparência).
/// </summary>
internal static class DrawingEdits
{
    public static readonly HashSet<string> Methods = new(StringComparer.Ordinal)
    {
        "create_layer", "update_layer", "move_selection_to_layer",
        "draw_entities", "generate_elevation",   // v0.4
        "edit_entities",                         // v0.5
    };

    public static object Run(Document doc, string method, JsonElement p) => method switch
    {
        "create_layer" => CreateLayer(doc, p),
        "update_layer" => UpdateLayer(doc, p),
        "move_selection_to_layer" => MoveSelectionToLayer(doc, p),
        "draw_entities" => DrawEntities.Run(doc, p),
        "generate_elevation" => Elevation.Generate(doc, p),
        "edit_entities" => EntityEdits.Run(doc, p),
        _ => throw new InvalidOperationException($"Método desconhecido: '{method}'."),
    };

    /// <summary>
    /// Chamadas de métodos de escrita que, com estes parâmetros, só LEEM (ex.: generate_elevation com
    /// dry_run=true). Rodam pelo caminho de leitura: sem lock nomeado, sem passo de undo e liberadas mesmo
    /// no modo somente leitura. Devolve null = não é o caso.
    /// </summary>
    public static object? ReadOnlyVariant(Document doc, string method, JsonElement p)
    {
        if (p.ValueKind != JsonValueKind.Object || !Has(p, "dry_run") || p.GetProperty("dry_run").ValueKind != JsonValueKind.True) return null;
        return method switch
        {
            "generate_elevation" => Elevation.DryRun(doc, p),
            "edit_entities" => EntityEdits.DryRun(doc, p),
            _ => null,
        };
    }

    /// <summary>
    /// update_layer que não mudaria nada: responde aqui, SEM abrir o lock nomeado — senão o AutoCAD
    /// gravaria um passo de undo vazio (um U "que não faz nada"). Devolve null = siga o caminho normal
    /// (inclusive para os casos de erro, que o caminho normal explica melhor).
    /// </summary>
    public static object? TryNoOp(Document doc, string method, JsonElement p)
    {
        if (method != "update_layer") return null;
        Database db = doc.Database;
        string name = ReqString(p, "name").Trim();
        LayerSpec spec = LayerSpec.Parse(p);
        bool makeCurrent = OptBool(p, "make_current") ?? false;
        using Transaction rt = db.TransactionManager.StartOpenCloseTransaction();
        var lt = (LayerTable)rt.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!lt.Has(name)) return null;
        var l = (LayerTableRecord)rt.GetObject(lt[name], OpenMode.ForRead);
        if (spec.IsEmpty && !makeCurrent) return null;
        if (makeCurrent && lt[name] != db.Clayer) return null;
        if (spec.WouldChange(l, rt)) return null;
        return new
        {
            action = "update_layer",
            changed = 0,
            changes = new Dictionary<string, object?>(),
            layer = DescribeLayer(l, rt, db),
            undo = (string?)null,
            note = "Nada mudou: a layer já estava assim (nenhum passo de undo foi gravado).",
        };
    }

    /// <summary>Chamado ANTES de abrir o lock de escrita.</summary>
    public static void EnsureWritable()
    {
        if (BridgeApp.ReadOnlyMode)
            throw new InvalidOperationException(
                "O AcadBridge está em modo SOMENTE LEITURA neste AutoCAD (comando ACADBRIDGE_READONLY). " +
                "Peça ao usuário para liberar a edição, se ele quiser.");
        int undoctl = Convert.ToInt32(AcApp.GetSystemVariable("UNDOCTL"));
        if ((undoctl & 1) == 0)
            throw new InvalidOperationException(
                "O UNDO está desligado neste AutoCAD (UNDOCTL=0), então a alteração não poderia ser desfeita. " +
                "Peça ao usuário para rodar UNDO → All e tente de novo.");
    }

    // =====================================================================================
    // create_layer
    // =====================================================================================
    private static object CreateLayer(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        string name = ValidName(ReqString(p, "name"));
        LayerSpec spec = LayerSpec.Parse(p);                // valida cor/espessura/etc. antes de tudo
        bool makeCurrent = OptBool(p, "make_current") ?? false;
        if (makeCurrent && spec.Frozen == true)
            throw new InvalidOperationException("Uma layer congelada não pode ser a layer atual.");

        using (Transaction rt = db.TransactionManager.StartOpenCloseTransaction())
        {
            var lt0 = (LayerTable)rt.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt0.Has(name))
                throw new InvalidOperationException($"A layer '{name}' já existe. Use update_layer para alterá-la.");
        }
        ObjectId linetypeId = spec.Linetype == null ? ObjectId.Null : EnsureLinetype(db, spec.Linetype);

        using Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForWrite);
        // Uma LayerTableRecord nova nasce com LinetypeObjectId NULO (bug visto na 0.2.1: eNullObjectId).
        // O comando LAYER do AutoCAD usa Continuous; fazemos o mesmo.
        var layer = new LayerTableRecord { Name = name, LinetypeObjectId = db.ContinuousLinetype };
        lt.Add(layer);
        tr.AddNewlyCreatedDBObject(layer, true);
        spec.Apply(layer, linetypeId, isCurrent: false, changes: null);  // depois do Add: Description exige estar no banco
        if (makeCurrent) db.Clayer = layer.ObjectId;
        object result = DescribeLayer(layer, tr, db);
        tr.Commit();

        Notify(doc, $"criou a layer '{name}'" + (makeCurrent ? " (agora é a atual)" : ""));
        return new { action = "create_layer", layer = result, undo = UndoHint };
    }

    // =====================================================================================
    // update_layer
    // =====================================================================================
    private static object UpdateLayer(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        string name = ReqString(p, "name").Trim();
        LayerSpec spec = LayerSpec.Parse(p);
        bool makeCurrent = OptBool(p, "make_current") ?? false;
        if (spec.IsEmpty && !makeCurrent)
            throw new InvalidOperationException("Nenhuma propriedade para alterar foi informada.");

        // Validação contra o estado atual, ainda sem alterar nada.
        using (Transaction rt = db.TransactionManager.StartOpenCloseTransaction())
        {
            var lt0 = (LayerTable)rt.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (!lt0.Has(name))
                throw new InvalidOperationException($"A layer '{name}' não existe. Veja os nomes com list_layers ou crie com create_layer.");
            var cur = (LayerTableRecord)rt.GetObject(lt0[name], OpenMode.ForRead);
            bool willBeFrozen = spec.Frozen ?? cur.IsFrozen;
            bool willBeCurrent = makeCurrent || lt0[name] == db.Clayer;
            if (willBeFrozen && willBeCurrent)
                throw new InvalidOperationException(
                    $"A layer '{name}' é (ou ficaria) a atual e não pode ser congelada. Torne outra layer atual primeiro.");
        }
        ObjectId linetypeId = spec.Linetype == null ? ObjectId.Null : EnsureLinetype(db, spec.Linetype);

        var changes = new Dictionary<string, object?>();
        using Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        ObjectId id = lt[name];
        var layer = (LayerTableRecord)tr.GetObject(id, OpenMode.ForWrite);
        spec.Apply(layer, linetypeId, isCurrent: id == db.Clayer, changes);
        if (makeCurrent && db.Clayer != id)
        {
            string before = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name;
            db.Clayer = id;
            changes["current_layer"] = new { from = before, to = name };
        }
        object result = DescribeLayer(layer, tr, db);
        tr.Commit();

        // Ligar/desligar/congelar/cor/tipo via API só aparecem na tela depois de um REGEN.
        if (spec.NeedsRegen) doc.Editor.Regen();

        Notify(doc, changes.Count == 0
            ? $"layer '{name}': nada mudou (já estava assim)"
            : $"alterou a layer '{name}': {string.Join(", ", changes.Keys)}");
        return new { action = "update_layer", changed = changes.Count, changes, layer = result, undo = changes.Count > 0 ? UndoHint : null };
    }

    // =====================================================================================
    // move_selection_to_layer
    // =====================================================================================
    private static object MoveSelectionToLayer(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        string name = ReqString(p, "layer").Trim();

        PromptSelectionResult psr = doc.Editor.SelectImplied();
        if (psr.Status != PromptStatus.OK || psr.Value == null)
            throw new InvalidOperationException(
                "Nada selecionado. Peça ao usuário para selecionar os objetos no AutoCAD (sem comando ativo) e tente de novo.");
        ObjectId[] ids = psr.Value.GetObjectIds();

        using Transaction tr = db.TransactionManager.StartTransaction();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (!lt.Has(name))
            throw new InvalidOperationException($"A layer '{name}' não existe. Crie com create_layer primeiro.");
        ObjectId target = lt[name];
        var targetLayer = (LayerTableRecord)tr.GetObject(target, OpenMode.ForRead);

        int moved = 0, already = 0;
        var from = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var skippedLocked = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var lockedCache = new Dictionary<ObjectId, bool>();

        foreach (ObjectId eid in ids)
        {
            if (tr.GetObject(eid, OpenMode.ForRead) is not Entity ent) continue;
            if (ent.LayerId == target) { already++; continue; }
            if (!lockedCache.TryGetValue(ent.LayerId, out bool locked))
                lockedCache[ent.LayerId] = locked = ((LayerTableRecord)tr.GetObject(ent.LayerId, OpenMode.ForRead)).IsLocked;
            string src = ent.Layer;
            if (locked) { Inc(skippedLocked, src); continue; }   // o próprio AutoCAD não deixa mexer em layer travada
            ent.UpgradeOpen();
            ent.LayerId = target;
            Inc(from, src);
            moved++;
        }
        tr.Commit();
        doc.Editor.UpdateScreen();

        var warnings = new List<string>();
        if (skippedLocked.Count > 0) warnings.Add("Objetos em layers TRAVADAS não foram movidos (destrave a layer de origem se quiser movê-los).");
        if (targetLayer.IsOff || targetLayer.IsFrozen) warnings.Add($"A layer '{name}' está desligada/congelada: os objetos movidos sumiram da tela.");
        if (targetLayer.IsLocked) warnings.Add($"A layer '{name}' está travada: os objetos movidos ficaram travados.");

        Notify(doc, $"moveu {moved} objeto(s) para a layer '{name}'");
        return new
        {
            action = "move_selection_to_layer",
            target_layer = name,
            selected = ids.Length,
            moved,
            already_on_layer = already,
            moved_from = from,
            skipped_on_locked_layers = skippedLocked,
            warnings,
            undo = moved > 0 ? UndoHint : null,
        };
    }

    // =====================================================================================
    // Propriedades de layer: parse (validação) separado de apply (alteração)
    // =====================================================================================
    private sealed class LayerSpec
    {
        public Color? Color;
        public string? Linetype;
        public LineWeight? LineWeight;
        public bool? Plottable, On, Frozen, Locked;
        public string? Description;

        public bool IsEmpty => Color == null && Linetype == null && LineWeight == null && Plottable == null &&
                               On == null && Frozen == null && Locked == null && Description == null;
        public bool NeedsRegen => Color != null || Linetype != null || LineWeight != null || On != null || Frozen != null;

        public static LayerSpec Parse(JsonElement p) => new()
        {
            Color = Has(p, "color") ? ParseColor(p.GetProperty("color")) : null,
            Linetype = OptString(p, "linetype")?.Trim(),
            LineWeight = Has(p, "lineweight") ? ParseLineweight(p.GetProperty("lineweight")) : null,
            Plottable = OptBool(p, "plottable"),
            On = OptBool(p, "on"),
            Frozen = OptBool(p, "frozen"),
            Locked = OptBool(p, "locked"),
            Description = OptString(p, "description"),
        };

        /// <summary>Mesmos critérios de comparação do Track() em Apply (texto da cor, nome do tipo de linha...).</summary>
        public bool WouldChange(LayerTableRecord l, Transaction tr)
        {
            string lt = !l.LinetypeObjectId.IsNull && tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord r ? r.Name : "";
            return (Color != null && l.Color.ToString() != Color.ToString())
                || (Linetype != null && !string.Equals(lt, Linetype, StringComparison.OrdinalIgnoreCase))
                || (LineWeight != null && l.LineWeight != LineWeight.Value)
                || (Plottable != null && l.IsPlottable != Plottable.Value)
                || (On != null && l.IsOff == On.Value)
                || (Frozen != null && l.IsFrozen != Frozen.Value)
                || (Locked != null && l.IsLocked != Locked.Value)
                || (Description != null && l.Description != Description);
        }

        public void Apply(LayerTableRecord l, ObjectId linetypeId, bool isCurrent, Dictionary<string, object?>? changes)
        {
            void Track(string key, object? before, object? after)
            {
                if (changes != null && !Equals(before, after)) changes[key] = new { from = before, to = after };
            }
            if (Color != null) { Track("color", l.Color.ToString(), Color.ToString()); l.Color = Color; }
            if (Linetype != null)
            {
                string before = l.LinetypeObjectId.IsNull ? "" : ((LinetypeTableRecord)l.LinetypeObjectId.GetObject(OpenMode.ForRead)).Name;
                l.LinetypeObjectId = linetypeId;
                Track("linetype", before, ((LinetypeTableRecord)linetypeId.GetObject(OpenMode.ForRead)).Name);
            }
            if (LineWeight != null) { Track("lineweight", DrawingQueries.Lw(l.LineWeight), DrawingQueries.Lw(LineWeight.Value)); l.LineWeight = LineWeight.Value; }
            if (Plottable != null) { Track("plottable", l.IsPlottable, Plottable.Value); l.IsPlottable = Plottable.Value; }
            if (On != null) { Track("on", !l.IsOff, On.Value); l.IsOff = !On.Value; }
            if (Frozen != null && !(isCurrent && Frozen.Value)) { Track("frozen", l.IsFrozen, Frozen.Value); l.IsFrozen = Frozen.Value; }
            if (Locked != null) { Track("locked", l.IsLocked, Locked.Value); l.IsLocked = Locked.Value; }
            if (Description != null) { Track("description", l.Description, Description); l.Description = Description; }
        }
    }

    private static readonly Dictionary<string, short> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 1, ["vermelho"] = 1, ["yellow"] = 2, ["amarelo"] = 2, ["green"] = 3, ["verde"] = 3,
        ["cyan"] = 4, ["ciano"] = 4, ["blue"] = 5, ["azul"] = 5, ["magenta"] = 6, ["white"] = 7, ["branco"] = 7,
        ["gray"] = 8, ["grey"] = 8, ["cinza"] = 8,
    };

    /// <summary>Aceita índice ACI 1–255 (número ou texto), "R,G,B" ou um nome básico (red/vermelho...).</summary>
    internal static Color ParseColor(JsonElement el)
    {
        string s = el.ValueKind == JsonValueKind.Number ? el.GetRawText() : (el.GetString() ?? "").Trim();
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int aci))
        {
            if (aci is < 1 or > 255) throw new InvalidOperationException($"Cor ACI deve estar entre 1 e 255 (recebido {aci}).");
            return Color.FromColorIndex(ColorMethod.ByAci, (short)aci);
        }
        string[] rgb = s.Split(',');
        if (rgb.Length == 3 && rgb.All(c => byte.TryParse(c.Trim(), out _)))
            return Color.FromRgb(byte.Parse(rgb[0].Trim()), byte.Parse(rgb[1].Trim()), byte.Parse(rgb[2].Trim()));
        if (ColorNames.TryGetValue(s, out short named))
            return Color.FromColorIndex(ColorMethod.ByAci, named);
        throw new InvalidOperationException($"Cor inválida: '{s}'. Use ACI 1–255 (ex.: 1=vermelho, 3=verde, 5=azul, 8=cinza), 'R,G,B' ou um nome básico.");
    }

    /// <summary>Espessura em mm (0.25, 0.5...) ou "default". Só valores da tabela padrão do AutoCAD.</summary>
    private static LineWeight ParseLineweight(JsonElement el)
    {
        if (el.ValueKind == JsonValueKind.String)
        {
            string s = (el.GetString() ?? "").Trim();
            if (s.Equals("default", StringComparison.OrdinalIgnoreCase) || s.Equals("padrão", StringComparison.OrdinalIgnoreCase))
                return LineWeight.ByLineWeightDefault;
            if (!double.TryParse(s.Replace(',', '.').Replace("mm", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out double mmS))
                throw new InvalidOperationException($"Espessura inválida: '{s}'.");
            return ToLineWeight(mmS);
        }
        return ToLineWeight(el.GetDouble());
    }

    private static LineWeight ToLineWeight(double mm)
    {
        int hundredths = (int)Math.Round(mm * 100);
        var valid = Enum.GetValues<LineWeight>().Where(v => (int)v >= 0).ToArray();
        if (valid.Any(v => (int)v == hundredths)) return (LineWeight)hundredths;
        string list = string.Join(", ", valid.Select(v => ((int)v / 100.0).ToString("0.00", CultureInfo.InvariantCulture)));
        throw new InvalidOperationException($"Espessura {mm} mm não existe no AutoCAD. Valores válidos (mm): {list} ou 'default'.");
    }

    /// <summary>Acha o tipo de linha no desenho; se não houver, carrega do arquivo .lin padrão (métrico ou imperial).</summary>
    private static ObjectId EnsureLinetype(Database db, string name)
    {
        using (Transaction rt = db.TransactionManager.StartOpenCloseTransaction())
        {
            var ltt = (LinetypeTable)rt.GetObject(db.LinetypeTableId, OpenMode.ForRead);
            if (ltt.Has(name)) return ltt[name];
        }
        string file = db.Measurement == MeasurementValue.Metric ? "acadiso.lin" : "acad.lin";
        try { db.LoadLineTypeFile(name, file); }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new InvalidOperationException(
                $"Tipo de linha '{name}' não existe no desenho nem em {file}. Ex.: Continuous, DASHED, HIDDEN, CENTER, PHANTOM, ACAD_ISO02W100.");
        }
        using Transaction rt2 = db.TransactionManager.StartOpenCloseTransaction();
        var ltt2 = (LinetypeTable)rt2.GetObject(db.LinetypeTableId, OpenMode.ForRead);
        return ltt2.Has(name) ? ltt2[name] : throw new InvalidOperationException($"Falha ao carregar o tipo de linha '{name}'.");
    }

    private static object DescribeLayer(LayerTableRecord l, Transaction tr, Database db) => new
    {
        name = l.Name,
        current = l.ObjectId == db.Clayer,
        on = !l.IsOff,
        frozen = l.IsFrozen,
        locked = l.IsLocked,
        plottable = l.IsPlottable,
        color = l.Color.ToString(),
        linetype = !l.LinetypeObjectId.IsNull && tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord ltr ? ltr.Name : "",
        lineweight = DrawingQueries.Lw(l.LineWeight),
        description = l.Description,
    };

    // =====================================================================================
    // utilidades
    // =====================================================================================
    private const string UndoHint = "Para desfazer: digite U no AutoCAD (desfaz esta ação inteira de uma vez).";

    internal static void Notify(Document doc, string what)
    {
        try
        {
            doc.Editor.WriteMessage($"\n[AcadBridge] IA {what}. U desfaz.\n");
            doc.Editor.PostCommandPrompt();
        }
        catch { /* só informativo */ }
    }

    private static string ValidName(string raw)
    {
        string name = raw.Trim();
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch (Autodesk.AutoCAD.Runtime.Exception)
        {
            throw new InvalidOperationException($"Nome de layer inválido: '{name}'. Não use < > / \\ \" : ; ? * | , = `");
        }
        return name;
    }

    private static void Inc(SortedDictionary<string, int> d, string k) => d[k] = d.TryGetValue(k, out int c) ? c + 1 : 1;

    private static bool Has(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out JsonElement v) && v.ValueKind != JsonValueKind.Null;

    private static string? OptString(JsonElement p, string name) =>
        Has(p, name) && p.GetProperty(name).ValueKind == JsonValueKind.String ? p.GetProperty(name).GetString() : null;

    private static bool? OptBool(JsonElement p, string name) =>
        Has(p, name) && p.GetProperty(name).ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetProperty(name).GetBoolean() : null;

    private static string ReqString(JsonElement p, string name) =>
        OptString(p, name) is { Length: > 0 } s ? s : throw new InvalidOperationException($"Parâmetro obrigatório ausente: '{name}'.");
}
