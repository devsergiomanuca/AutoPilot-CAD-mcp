using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// edit_entities: altera objetos EXISTENTES do Model, apontados por handle (o read_geometry devolve o handle
/// de cada item). Operações em lote, aplicadas em ordem: move, copy, rotate, scale, mirror, erase,
/// set_props (layer/cor), set_text, set_attributes.
///
/// Segurança (tudo antes da 1ª alteração):
///  - cada handle precisa existir, não estar apagado e estar no MODEL (nada em layouts nem dentro de blocos);
///  - objetos em layer TRAVADA são recusados (o usuário travou de propósito);
///  - uma inserção de XREF é "a planta inteira de outro arquivo": só com allow_xref=true. Nunca se edita o
///    conteúdo de um XREF (é outro arquivo);
///  - dry_run devolve exatamente quais objetos seriam afetados, sem undo, para a IA confirmar com o usuário.
/// O lote inteiro é UM passo de undo (lock nomeado do BridgeServer).
/// </summary>
internal static class EntityEdits
{
    private const int MaxOps = 200;
    private const int MaxHandles = 5000;

    private sealed class Op
    {
        public int Index;
        public string Action = "";
        public ObjectId[] Ids = Array.Empty<ObjectId>();
        public Matrix3d Xf = Matrix3d.Identity;   // move/rotate/scale/mirror
        public Vector3d Step;                      // copy
        public int Count = 1;                      // copy
        public bool KeepOriginal;                  // mirror
        public string? Layer;
        public Color? Color;
        public string? Text;
        public Dictionary<string, string>? Attrs;
        public string Summary = "";
    }

    private sealed class Plan
    {
        public readonly List<Op> Ops = new();
        public readonly Dictionary<ObjectId, string> Described = new();   // objetos tocados, para o relatório
        public readonly List<string> Warnings = new();
    }

    public static object DryRun(Document doc, JsonElement p)
    {
        Plan plan = Build(doc, p);
        return Report(plan, executed: false, created: null, counts: null);
    }

    public static object Run(Document doc, JsonElement p)
    {
        Plan plan = Build(doc, p);
        Database db = doc.Database;
        var created = new List<string>();
        var counts = new SortedDictionary<string, int>(StringComparer.Ordinal);

        using Transaction tr = db.TransactionManager.StartTransaction();
        ObjectId ms = SymbolUtilityServices.GetBlockModelSpaceId(db);
        foreach (Op op in plan.Ops)
        {
            int n = 0;
            switch (op.Action)
            {
                case "move" or "rotate" or "scale":
                    foreach (ObjectId id in op.Ids) { Open(tr, id).TransformBy(op.Xf); n++; }
                    break;
                case "mirror":
                    if (op.KeepOriginal)
                    {
                        foreach (Entity e in Clone(db, tr, op.Ids, ms)) { e.TransformBy(op.Xf); created.Add(e.Handle.ToString()); n++; }
                    }
                    else foreach (ObjectId id in op.Ids) { Open(tr, id).TransformBy(op.Xf); n++; }
                    break;
                case "copy":
                    for (int k = 1; k <= op.Count; k++)
                        foreach (Entity e in Clone(db, tr, op.Ids, ms)) { e.TransformBy(Matrix3d.Displacement(op.Step * k)); created.Add(e.Handle.ToString()); n++; }
                    break;
                case "erase":
                    foreach (ObjectId id in op.Ids) { Open(tr, id).Erase(); n++; }
                    break;
                case "set_props":
                    foreach (ObjectId id in op.Ids)
                    {
                        Entity e = Open(tr, id);
                        if (op.Layer != null) e.Layer = op.Layer;
                        if (op.Color != null) e.Color = op.Color;
                        n++;
                    }
                    break;
                case "set_text":
                    foreach (ObjectId id in op.Ids)
                    {
                        switch (Open(tr, id))
                        {
                            case DBText t: t.TextString = op.Text!; break;
                            case MText m: m.Contents = op.Text!.Replace("\n", "\\P"); break;
                            case Dimension d: d.DimensionText = op.Text!; break;
                        }
                        n++;
                    }
                    break;
                case "set_attributes":
                    foreach (ObjectId id in op.Ids)
                    {
                        var br = (BlockReference)tr.GetObject(id, OpenMode.ForRead);
                        foreach (ObjectId aid in br.AttributeCollection)
                        {
                            var ar = (AttributeReference)tr.GetObject(aid, OpenMode.ForRead);
                            if (op.Attrs!.TryGetValue(ar.Tag, out string? v)) { ar.UpgradeOpen(); ar.TextString = v; }
                        }
                        n++;
                    }
                    break;
            }
            counts[op.Action] = counts.GetValueOrDefault(op.Action) + n;
        }
        tr.Commit();

        DrawingEdits.Notify(doc, "editou objetos: " + string.Join(", ", counts.Select(kv => $"{kv.Key} {kv.Value}")));
        return Report(plan, executed: true, created, counts);
    }

    private static Entity Open(Transaction tr, ObjectId id) => (Entity)tr.GetObject(id, OpenMode.ForWrite);

    /// <summary>DeepClone (leva atributos junto) para o Model; devolve as cópias abertas para escrita.</summary>
    private static List<Entity> Clone(Database db, Transaction tr, ObjectId[] ids, ObjectId ms)
    {
        var map = new IdMapping();
        db.DeepCloneObjects(new ObjectIdCollection(ids), ms, map, false);
        var res = new List<Entity>();
        foreach (IdPair pair in map)
            if (pair.IsCloned && pair.IsPrimary && tr.GetObject(pair.Value, OpenMode.ForWrite) is Entity e) res.Add(e);
        return res;
    }

    // ------------------------------------------------------------------------------------------
    private static Plan Build(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        if (!Json.Has(p, "operations") || p.GetProperty("operations").ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Informe 'operations': lista de {action, handles, ...}.");
        JsonElement[] ops = p.GetProperty("operations").EnumerateArray().ToArray();
        if (ops.Length == 0) throw new InvalidOperationException("'operations' está vazia.");
        if (ops.Length > MaxOps) throw new InvalidOperationException($"No máximo {MaxOps} operações por chamada.");
        bool allowXref = Json.Bool(p, "allow_xref") ?? false;

        var plan = new Plan();
        var erased = new HashSet<ObjectId>();
        int totalHandles = 0;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        ObjectId ms = SymbolUtilityServices.GetBlockModelSpaceId(db);
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

        for (int i = 0; i < ops.Length; i++)
        {
            JsonElement o = ops[i];
            string where = $"Operação {i + 1}";
            try
            {
                var op = new Op { Index = i + 1, Action = (Json.Str(o, "action") ?? throw new InvalidOperationException("falta 'action'.")).Trim().ToLowerInvariant() };
                string[] handles = Json.StringArray(o, "handles") ?? throw new InvalidOperationException("falta 'handles' (lista de handles do read_geometry).");
                if (handles.Length == 0) throw new InvalidOperationException("'handles' está vazia.");
                totalHandles += handles.Length;
                if (totalHandles > MaxHandles) throw new InvalidOperationException($"No máximo {MaxHandles} handles por chamada.");

                // resolve e valida cada objeto
                var ids = new List<ObjectId>();
                foreach (string h in handles.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    ObjectId id = Resolve(db, h);
                    if (erased.Contains(id)) throw new InvalidOperationException($"o objeto {h} é apagado por uma operação anterior.");
                    if (tr.GetObject(id, OpenMode.ForRead) is not Entity e) throw new InvalidOperationException($"o handle {h} não é um objeto gráfico.");
                    if (e.OwnerId != ms)
                        throw new InvalidOperationException($"o objeto {h} não está no Model (está num layout ou dentro de um bloco). Só objetos do Model podem ser editados.");
                    var layer = (LayerTableRecord)tr.GetObject(e.LayerId, OpenMode.ForRead);
                    if (layer.IsLocked)
                        throw new InvalidOperationException($"o objeto {h} está na layer TRAVADA '{layer.Name}'. Pergunte ao usuário se ele quer destravá-la (update_layer locked=false).");
                    if (e is BlockReference br && tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr && btr.IsFromExternalReference && !allowXref)
                        throw new InvalidOperationException(
                            $"o objeto {h} é a inserção do XREF '{btr.Name}' (a planta inteira de outro arquivo). Editar a inserção move/apaga " +
                            "o arquivo referenciado todo; o conteúdo do XREF não pode ser editado daqui. Se o usuário quer mesmo isso, use allow_xref=true.");
                    ids.Add(id);
                    if (!plan.Described.ContainsKey(id)) plan.Described[id] = Describe(e, tr);
                }
                op.Ids = ids.ToArray();
                ParseAction(op, o, tr, lt, db);
                if (op.Action == "erase") foreach (ObjectId id in op.Ids) erased.Add(id);
                plan.Ops.Add(op);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"{where}: {ex.Message} Nada foi alterado.");
            }
        }
        if (plan.Ops.Any(x => x.Action == "mirror") && plan.Described.Values.Any(d => d.Contains(" TEXT ") || d.Contains(" MTEXT ")))
            plan.Warnings.Add("Textos espelhados podem ficar invertidos (leitura de trás para frente). Confira com capture_view.");
        return plan;
    }

    private static ObjectId Resolve(Database db, string h)
    {
        string s = h.Trim();
        if (!long.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) || v <= 0)
            throw new InvalidOperationException($"handle inválido: '{h}' (é um número hexadecimal, ex.: '1B00D').");
        if (!db.TryGetObjectId(new Handle(v), out ObjectId id) || id.IsNull || id.IsErased)
            throw new InvalidOperationException($"o handle {s} não existe neste desenho (ou já foi apagado). Leia de novo com read_geometry.");
        return id;
    }

    private static string Describe(Entity e, Transaction tr)
    {
        string type = e.GetRXClass().DxfName;
        string extra = e is BlockReference br && tr.GetObject(br.BlockTableRecord, OpenMode.ForRead) is BlockTableRecord btr
            ? (btr.IsFromExternalReference ? $" XREF='{btr.Name}'" : $" bloco='{GeometryReader.EffectiveName(br, tr)}'")
            : "";
        return $"{e.Handle} {type} layer='{e.Layer}'{extra} {GeometryReader.Compact(e)}";
    }

    private static void ParseAction(Op op, JsonElement o, Transaction tr, LayerTable lt, Database db)
    {
        Vector3d Displacement()
        {
            if (Json.Has(o, "displacement"))
            {
                Point3d d = Json.Point(o.GetProperty("displacement"), "displacement");
                return new Vector3d(d.X, d.Y, 0);
            }
            if (Json.Has(o, "from") && Json.Has(o, "to"))
                return Json.Point(o, "to", "to") - Json.Point(o, "from", "from");
            throw new InvalidOperationException("informe 'displacement' [dx, dy] ou 'from' e 'to'.");
        }
        string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        switch (op.Action)
        {
            case "move":
            {
                Vector3d v = Displacement();
                if (v.Length < 1e-12) throw new InvalidOperationException("deslocamento zero.");
                op.Xf = Matrix3d.Displacement(v);
                op.Summary = $"mover {op.Ids.Length} objeto(s) por ({F(v.X)}, {F(v.Y)})";
                break;
            }
            case "copy":
            {
                Vector3d v = Displacement();
                if (v.Length < 1e-12) throw new InvalidOperationException("deslocamento zero (a cópia ficaria em cima do original).");
                op.Step = v;
                op.Count = Json.Int(o, "count") ?? 1;
                if (op.Count is < 1 or > 100) throw new InvalidOperationException("'count' deve estar entre 1 e 100.");
                if (op.Count * op.Ids.Length > 5000) throw new InvalidOperationException("cópias demais (máx. 5000 objetos criados).");
                op.Summary = $"copiar {op.Ids.Length} objeto(s) {op.Count}x, passo ({F(v.X)}, {F(v.Y)})";
                break;
            }
            case "rotate":
            {
                Point3d b = Json.Point(o, "base", "base");
                double ang = Json.Num(o, "angle") ?? throw new InvalidOperationException("falta 'angle' (graus, anti-horário).");
                if (Math.Abs(ang % 360) < 1e-9) throw new InvalidOperationException("ângulo zero.");
                op.Xf = Matrix3d.Rotation(ang * Math.PI / 180, Vector3d.ZAxis, b);
                op.Summary = $"girar {op.Ids.Length} objeto(s) {F(ang)}° em torno de ({F(b.X)}, {F(b.Y)})";
                break;
            }
            case "scale":
            {
                Point3d b = Json.Point(o, "base", "base");
                double f = Json.Num(o, "factor") ?? throw new InvalidOperationException("falta 'factor'.");
                if (!(f > 0) || Math.Abs(f - 1) < 1e-12) throw new InvalidOperationException("'factor' deve ser > 0 e diferente de 1.");
                op.Xf = Matrix3d.Scaling(f, b);
                op.Summary = $"escalar {op.Ids.Length} objeto(s) x{F(f)} a partir de ({F(b.X)}, {F(b.Y)})";
                break;
            }
            case "mirror":
            {
                Point3d a = Json.Point(o, "p1", "p1"), b = Json.Point(o, "p2", "p2");
                if (a.DistanceTo(b) < 1e-12) throw new InvalidOperationException("p1 e p2 iguais (o eixo do espelho precisa de dois pontos).");
                op.Xf = Matrix3d.Mirroring(new Line3d(new Point3d(a.X, a.Y, 0), new Point3d(b.X, b.Y, 0)));
                op.KeepOriginal = Json.Bool(o, "keep_original") ?? false;
                op.Summary = $"espelhar {op.Ids.Length} objeto(s) no eixo ({F(a.X)}, {F(a.Y)})–({F(b.X)}, {F(b.Y)})" + (op.KeepOriginal ? ", mantendo o original" : "");
                break;
            }
            case "erase":
                op.Summary = $"APAGAR {op.Ids.Length} objeto(s)";
                break;
            case "set_props":
            {
                string? layer = Json.Str(o, "layer")?.Trim();
                if (layer != null)
                {
                    if (!lt.Has(layer)) throw new InvalidOperationException($"a layer '{layer}' não existe (crie com create_layer).");
                    op.Layer = ((LayerTableRecord)tr.GetObject(lt[layer], OpenMode.ForRead)).Name;
                }
                if (Json.Has(o, "color")) op.Color = DrawingEdits.ParseColor(o.GetProperty("color"));
                if (op.Layer == null && op.Color == null) throw new InvalidOperationException("informe 'layer' e/ou 'color'.");
                op.Summary = $"mudar {op.Ids.Length} objeto(s):" + (op.Layer != null ? $" layer → '{op.Layer}'" : "") + (op.Color != null ? $" cor → {op.Color.ColorNameForDisplay}" : "");
                break;
            }
            case "set_text":
            {
                op.Text = Json.Str(o, "text") ?? throw new InvalidOperationException("falta 'text'.");
                foreach (ObjectId id in op.Ids)
                    if (tr.GetObject(id, OpenMode.ForRead) is not (DBText or MText or Dimension))
                        throw new InvalidOperationException($"o objeto {id.Handle} não é TEXT, MTEXT nem cota (para atributo de bloco use set_attributes).");
                op.Summary = $"trocar o texto de {op.Ids.Length} objeto(s) para \"{op.Text}\"";
                break;
            }
            case "set_attributes":
            {
                if (!Json.Has(o, "attributes") || o.GetProperty("attributes").ValueKind != JsonValueKind.Object)
                    throw new InvalidOperationException("falta 'attributes' {TAG: valor}.");
                op.Attrs = o.GetProperty("attributes").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.String ? x.Value.GetString()! : x.Value.GetRawText(), StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in op.Ids)
                {
                    if (tr.GetObject(id, OpenMode.ForRead) is not BlockReference br)
                        throw new InvalidOperationException($"o objeto {id.Handle} não é um bloco.");
                    var tags = br.AttributeCollection.Cast<ObjectId>().Select(a => ((AttributeReference)tr.GetObject(a, OpenMode.ForRead)).Tag).ToList();
                    string[] missing = op.Attrs.Keys.Where(k => !tags.Contains(k, StringComparer.OrdinalIgnoreCase)).ToArray();
                    if (missing.Length > 0)
                        throw new InvalidOperationException($"o bloco {id.Handle} não tem o(s) atributo(s) {string.Join(", ", missing)}. Atributos dele: {(tags.Count > 0 ? string.Join(", ", tags) : "nenhum")}.");
                }
                op.Summary = $"mudar atributos de {op.Ids.Length} bloco(s): " + string.Join(", ", op.Attrs.Select(kv => $"{kv.Key}=\"{kv.Value}\""));
                break;
            }
            default:
                throw new InvalidOperationException($"ação '{op.Action}' desconhecida. Use move, copy, rotate, scale, mirror, erase, set_props, set_text ou set_attributes.");
        }
    }

    private static object Report(Plan plan, bool executed, List<string>? created, SortedDictionary<string, int>? counts)
    {
        const int maxListed = 150;
        return new
        {
            action = executed ? "edit_entities" : "edit_entities (dry_run: nada foi alterado)",
            operations = plan.Ops.Select(o => $"{o.Index}. {o.Summary}"),
            objects_affected = plan.Described.Count,
            objects = plan.Described.Values.Take(maxListed),
            objects_truncated = plan.Described.Count > maxListed ? true : (bool?)null,
            counts,
            created_handles = created is { Count: > 0 } ? created.Take(300) : null,
            warnings = plan.Warnings.Count > 0 ? plan.Warnings : null,
            next = executed
                ? "Para desfazer: digite U no AutoCAD (desfaz o lote inteiro de uma vez). Confira com capture_view."
                : "Mostre ao usuário o que será alterado (e destaque com select_entities se ajudar). Com o ok dele, chame de novo sem dry_run.",
        };
    }
}

/// <summary>
/// Navegação e destaque: não alteram o desenho (mas o zoom entra no histórico de undo do AutoCAD, como
/// qualquer zoom). Servem para a IA mostrar ao usuário "são estes objetos?" e para enquadrar antes do capture_view.
/// </summary>
internal static class ViewOps
{
    /// <summary>select_entities: seleciona (com grips) os objetos dados e, por padrão, enquadra.</summary>
    public static object Select(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        string[] handles = Json.StringArray(p, "handles") ?? throw new InvalidOperationException("Informe 'handles'.");
        if (handles.Length == 0) throw new InvalidOperationException("'handles' está vazia.");
        if (handles.Length > 5000) throw new InvalidOperationException("No máximo 5000 handles.");
        var ids = new List<ObjectId>();
        var missing = new List<string>();
        Extents3d? ext = null;
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            ObjectId ms = SymbolUtilityServices.GetBlockModelSpaceId(db);
            foreach (string h in handles.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!long.TryParse(h.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) ||
                    !db.TryGetObjectId(new Handle(v), out ObjectId id) || id.IsErased ||
                    tr.GetObject(id, OpenMode.ForRead) is not Entity e || e.OwnerId != ms)
                { missing.Add(h); continue; }
                ids.Add(id);
                if (GeometryReader.TryExtents(e, Matrix3d.Identity, out Extents3d ex)) { if (ext is { } x) { x.AddExtents(ex); ext = x; } else ext = ex; }
            }
        }
        if (ids.Count == 0) throw new InvalidOperationException("Nenhum dos handles existe no Model deste desenho. Leia de novo com read_geometry.");
        doc.Editor.SetImpliedSelection(ids.ToArray());
        bool zoomed = false;
        if ((Json.Bool(p, "zoom") ?? true) && ext is { } z) { ZoomExtents(doc, z, Json.Num(p, "margin") ?? 0.15); zoomed = true; }
        return new
        {
            action = "select_entities",
            selected = ids.Count,
            not_found = missing.Count > 0 ? missing : null,
            zoomed,
            next = "Os objetos estão selecionados (com grips) no AutoCAD. Pergunte ao usuário se são esses; capture_view mostra a tela. " +
                   "Avise que o zoom entra no histórico: no U, ele pode precisar de um U a mais.",
        };
    }

    /// <summary>zoom_to: enquadra uma região (coordenadas do desenho) ou objetos por handle.</summary>
    public static object Zoom(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        Extents3d ext;
        if (Json.Has(p, "region")) ext = Json.Region(p.GetProperty("region"));
        else if (Json.StringArray(p, "handles") is { Length: > 0 } hs)
        {
            Extents3d? acc = null;
            using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
            foreach (string h in hs)
            {
                if (long.TryParse(h.Trim(), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long v) &&
                    db.TryGetObjectId(new Handle(v), out ObjectId id) && !id.IsErased &&
                    tr.GetObject(id, OpenMode.ForRead) is Entity e && GeometryReader.TryExtents(e, Matrix3d.Identity, out Extents3d ex))
                { if (acc is { } x) { x.AddExtents(ex); acc = x; } else acc = ex; }
            }
            ext = acc ?? throw new InvalidOperationException("Nenhum dos handles foi encontrado. Leia de novo com read_geometry.");
        }
        else throw new InvalidOperationException("Informe 'region' {min, max} ou 'handles'.");
        ZoomExtents(doc, ext, Json.Num(p, "margin") ?? 0.1);
        return new
        {
            action = "zoom_to",
            region = new { min = GeometryReader.P2(ext.MinPoint), max = GeometryReader.P2(ext.MaxPoint) },
            next = "Use capture_view para ver. O zoom entra no histórico de undo do AutoCAD (um U a mais para o usuário).",
        };
    }

    /// <summary>A região inteira aparece na tela atual? (TraceBoundary só enxerga o que está visível.)</summary>
    internal static bool ViewContains(Document doc, Extents3d ext)
    {
        using ViewTableRecord view = doc.Editor.GetCurrentView();
        Matrix3d dcs2wcs = Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                           Matrix3d.Displacement(view.Target - Point3d.Origin) *
                           Matrix3d.PlaneToWorld(view.ViewDirection);
        Matrix3d wcs2dcs = dcs2wcs.Inverse();
        Point3d a = new Point3d(ext.MinPoint.X, ext.MinPoint.Y, 0).TransformBy(wcs2dcs);
        Point3d b = new Point3d(ext.MaxPoint.X, ext.MaxPoint.Y, 0).TransformBy(wcs2dcs);
        double hw = view.Width / 2, hh = view.Height / 2;
        bool In(Point3d q) => Math.Abs(q.X - view.CenterPoint.X) <= hw && Math.Abs(q.Y - view.CenterPoint.Y) <= hh;
        return In(a) && In(b) && In(new Point3d(a.X, b.Y, 0)) && In(new Point3d(b.X, a.Y, 0));
    }

    internal static void ZoomExtents(Document doc, Extents3d ext, double margin)
    {
        if (!doc.Database.TileMode)
            throw new InvalidOperationException("O AutoCAD está numa aba de layout (folha). Peça ao usuário para ir para a aba Model e tente de novo.");
        margin = Math.Clamp(margin, 0, 2);
        Editor ed = doc.Editor;
        using ViewTableRecord view = ed.GetCurrentView();
        Matrix3d wcs2dcs = (Matrix3d.Rotation(-view.ViewTwist, view.ViewDirection, view.Target) *
                            Matrix3d.Displacement(view.Target - Point3d.Origin) *
                            Matrix3d.PlaneToWorld(view.ViewDirection)).Inverse();
        Point3d a = ext.MinPoint, b = ext.MaxPoint;
        var corners = new[] { new Point3d(a.X, a.Y, 0), new Point3d(b.X, a.Y, 0), new Point3d(b.X, b.Y, 0), new Point3d(a.X, b.Y, 0) }
            .Select(q => q.TransformBy(wcs2dcs)).ToArray();
        double minX = corners.Min(q => q.X), maxX = corners.Max(q => q.X), minY = corners.Min(q => q.Y), maxY = corners.Max(q => q.Y);
        double w = Math.Max(maxX - minX, 1e-6) * (1 + 2 * margin), h = Math.Max(maxY - minY, 1e-6) * (1 + 2 * margin);
        double ratio = view.Width / view.Height;
        if (w / h > ratio) h = w / ratio; else w = h * ratio;
        view.CenterPoint = new Point2d((minX + maxX) / 2, (minY + maxY) / 2);
        view.Width = w;
        view.Height = h;
        ed.SetCurrentView(view);
    }
}
