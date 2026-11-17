using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// "Pincel" usado por draw_entities e generate_elevation. Tudo acontece dentro da transação da ação, que
/// roda sob o lock NOMEADO do BridgeServer, então tudo o que a ação desenhou se desfaz com um único U.
/// Desenha sempre no Model space. Layers que não existem são criadas (e informadas no resultado).
/// </summary>
internal sealed class Canvas
{
    private readonly Database _db;
    private readonly Transaction _tr;
    private readonly BlockTableRecord _space;
    private readonly Dictionary<string, ObjectId> _layers = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> CreatedLayers = new();
    public readonly SortedDictionary<string, int> Counts = new(StringComparer.Ordinal);
    public readonly List<string> Handles = new();
    private Extents3d? _bounds;

    public Canvas(Database db, Transaction tr)
    {
        _db = db;
        _tr = tr;
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        _space = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForWrite);
    }

    public object Bounds => _bounds is { } b
        ? new { min = GeometryReader.P2(b.MinPoint), max = GeometryReader.P2(b.MaxPoint) }
        : null!;

    /// <summary>Garante a layer (cria com a cor dada se não existir). Não altera layers existentes.</summary>
    public ObjectId Layer(string name, short aci = 7)
    {
        if (_layers.TryGetValue(name, out ObjectId id)) return id;
        var lt = (LayerTable)_tr.GetObject(_db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name)) id = lt[name];
        else
        {
            lt.UpgradeOpen();
            var l = new LayerTableRecord { Name = name, Color = Color.FromColorIndex(ColorMethod.ByAci, aci), LinetypeObjectId = _db.ContinuousLinetype };
            id = lt.Add(l);
            _tr.AddNewlyCreatedDBObject(l, true);
            CreatedLayers.Add(name);
        }
        _layers[name] = id;
        return id;
    }

    public T Add<T>(T e, string layer, Color? color = null) where T : Entity
    {
        e.SetDatabaseDefaults(_db);
        e.LayerId = Layer(layer);
        if (color != null) e.Color = color;
        _space.AppendEntity(e);
        _tr.AddNewlyCreatedDBObject(e, true);
        string type = e.GetRXClass().DxfName;
        Counts[type] = Counts.GetValueOrDefault(type) + 1;
        if (Handles.Count < 60) Handles.Add(e.Handle.ToString());
        if (GeometryReader.TryExtents(e, Matrix3d.Identity, out Extents3d ex))
        {
            if (_bounds is { } b) { b.AddExtents(ex); _bounds = b; } else _bounds = ex;
        }
        return e;
    }

    public Line Line(Point3d a, Point3d b, string layer, Color? c = null) => Add(new Line(a, b), layer, c);

    public Polyline Polyline(IReadOnlyList<Point2d> pts, bool closed, string layer, Color? c = null, IReadOnlyList<double>? bulges = null)
    {
        var pl = new Polyline(pts.Count);
        for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, pts[i], bulges != null && i < bulges.Count ? bulges[i] : 0, 0, 0);
        pl.Closed = closed;
        return Add(pl, layer, c);
    }

    public Polyline Rect(Point2d a, Point2d b, string layer, Color? c = null) =>
        Polyline(new[] { a, new Point2d(b.X, a.Y), b, new Point2d(a.X, b.Y) }, true, layer, c);

    public DBText Text(Point3d pos, string text, double height, double rotation, string layer, string justify = "left", Color? c = null)
    {
        var t = new DBText { TextString = text, Height = height, Rotation = rotation, Position = pos };
        switch (justify)
        {
            case "center": t.HorizontalMode = TextHorizontalMode.TextCenter; t.AlignmentPoint = pos; break;
            case "right": t.HorizontalMode = TextHorizontalMode.TextRight; t.AlignmentPoint = pos; break;
        }
        Add(t, layer, c);
        if (justify != "left") t.AdjustAlignment(_db);
        return t;
    }

    public MText MText(Point3d pos, string text, double height, string layer, AttachmentPoint attach = AttachmentPoint.TopLeft, double width = 0, Color? c = null)
    {
        var m = new MText { Location = pos, Contents = text, TextHeight = height, Attachment = attach };
        if (width > 0) m.Width = width;
        return Add(m, layer, c);
    }

    /// <summary>Cota alinhada (rotation null) ou girada (rotation em radianos: 0 = horizontal, π/2 = vertical).</summary>
    public Dimension Dim(Point3d p1, Point3d p2, Point3d dimLine, double? rotation, string layer, string? text = null)
    {
        Dimension d = rotation is { } r
            ? new RotatedDimension(r, p1, p2, dimLine, text ?? "", _db.Dimstyle)
            : new AlignedDimension(p1, p2, dimLine, text ?? "", _db.Dimstyle);
        return Add(d, layer);
    }

    /// <summary>Hachura com contorno externo e, opcionalmente, furos (ex.: portas e janelas dentro de uma pintura).</summary>
    public Hatch Hatch(IReadOnlyList<Point2d> boundary, string pattern, double scale, string layer, Color? c = null,
                       IEnumerable<IReadOnlyList<Point2d>>? holes = null)
    {
        var h = new Hatch();
        Add(h, layer, c);   // a hachura precisa estar no banco antes de receber o contorno
        h.SetHatchPattern(HatchPatternType.PreDefined, pattern.ToUpperInvariant());
        if (!pattern.Equals("SOLID", StringComparison.OrdinalIgnoreCase)) h.PatternScale = scale > 0 ? scale : 1;
        h.HatchStyle = HatchStyle.Normal;   // laços internos viram furos
        AppendLoop(h, boundary, HatchLoopTypes.External);
        if (holes != null) foreach (IReadOnlyList<Point2d> hole in holes) AppendLoop(h, hole, HatchLoopTypes.Default);
        h.EvaluateHatch(true);
        return h;
    }

    private static void AppendLoop(Hatch h, IReadOnlyList<Point2d> loop, HatchLoopTypes type)
    {
        var pts = new Point2dCollection(loop.ToArray());
        if (!loop[0].IsEqualTo(loop[^1])) pts.Add(loop[0]);
        h.AppendLoop(type, pts, new DoubleCollection(Enumerable.Repeat(0.0, pts.Count).ToArray()));
    }

    /// <summary>Chamada (leader) com seta no primeiro ponto, no estilo de cota atual.</summary>
    public Leader Leader(IReadOnlyList<Point3d> pts, string layer)
    {
        var ld = new Leader();
        ld.SetDatabaseDefaults(_db);
        foreach (Point3d q in pts) ld.AppendVertex(q);
        ld.HasArrowHead = true;
        return Add(ld, layer);
    }

    public BlockReference Block(string name, Point3d pos, double scale, double rotation, IReadOnlyDictionary<string, string>? attrs, string layer)
    {
        var bt = (BlockTable)_tr.GetObject(_db.BlockTableId, OpenMode.ForRead);
        var btr = (BlockTableRecord)_tr.GetObject(bt[name], OpenMode.ForRead);
        var br = new BlockReference(pos, btr.ObjectId) { Rotation = rotation, ScaleFactors = new Scale3d(scale) };
        Add(br, layer);
        if (btr.HasAttributeDefinitions)
        {
            foreach (ObjectId id in btr)
            {
                if (_tr.GetObject(id, OpenMode.ForRead) is not AttributeDefinition ad || ad.Constant) continue;
                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                if (attrs != null && attrs.TryGetValue(ad.Tag, out string? v)) ar.TextString = v;
                br.AttributeCollection.AppendAttribute(ar);
                _tr.AddNewlyCreatedDBObject(ar, true);
            }
        }
        return br;
    }

    public bool HasBlock(string name) => ((BlockTable)_tr.GetObject(_db.BlockTableId, OpenMode.ForRead)).Has(name);
}

/// <summary>
/// draw_entities: lote de primitivas. VALIDA TUDO antes de desenhar a primeira (um item inválido = nada
/// é desenhado, e a mensagem diz qual item e por quê).
/// </summary>
internal static class DrawEntities
{
    private const int MaxItems = 2000;
    private const string DefaultLayer = "IA-DESENHO";

    public static object Run(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        if (!Json.Has(p, "entities") || p.GetProperty("entities").ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("Informe 'entities': uma lista de objetos a desenhar.");
        JsonElement[] items = p.GetProperty("entities").EnumerateArray().ToArray();
        if (items.Length == 0) throw new InvalidOperationException("'entities' está vazia.");
        if (items.Length > MaxItems) throw new InvalidOperationException($"No máximo {MaxItems} entidades por chamada (recebidas {items.Length}).");
        string defaultLayer = ValidLayer(Json.Str(p, "layer") ?? DefaultLayer, "layer");

        // 1) validação completa: cada item vira uma ação pronta para executar
        var actions = new List<Action<Canvas>>(items.Length);
        using (Transaction rt = db.TransactionManager.StartOpenCloseTransaction())
        {
            var bt = (BlockTable)rt.GetObject(db.BlockTableId, OpenMode.ForRead);
            for (int i = 0; i < items.Length; i++)
            {
                try { actions.Add(Parse(items[i], defaultLayer, bt)); }
                catch (InvalidOperationException ex) { throw new InvalidOperationException($"Item {i} ({Json.Str(items[i], "type") ?? "?"}): {ex.Message} Nada foi desenhado."); }
            }
        }

        // 2) desenho (uma transação; o lock nomeado do BridgeServer faz disso um único undo)
        using Transaction tr = db.TransactionManager.StartTransaction();
        var canvas = new Canvas(db, tr);
        foreach (Action<Canvas> a in actions) a(canvas);
        tr.Commit();

        int total = canvas.Counts.Values.Sum();
        DrawingEdits.Notify(doc, $"desenhou {total} objeto(s)" + (canvas.CreatedLayers.Count > 0 ? $" (layers novas: {string.Join(", ", canvas.CreatedLayers)})" : ""));
        return new
        {
            action = "draw_entities",
            created = total,
            by_type = canvas.Counts,
            layers_created = canvas.CreatedLayers,
            bounds = canvas.Bounds,
            handles = canvas.Handles,
            undo = "Para desfazer: digite U no AutoCAD (desfaz este lote inteiro de uma vez).",
        };
    }

    private static Action<Canvas> Parse(JsonElement it, string defaultLayer, BlockTable bt)
    {
        string type = (Json.Str(it, "type") ?? throw new InvalidOperationException("falta 'type'.")).Trim().ToLowerInvariant();
        string layer = ValidLayer(Json.Str(it, "layer") ?? defaultLayer, "layer");
        Color? color = Json.Has(it, "color") ? DrawingEdits.ParseColor(it.GetProperty("color")) : null;
        double Deg(string n) => (Json.Num(it, n) ?? 0) * Math.PI / 180.0;
        double Pos(string n, double? def = null)
        {
            double v = Json.Num(it, n) ?? def ?? throw new InvalidOperationException($"falta '{n}'.");
            if (!double.IsFinite(v) || v <= 0) throw new InvalidOperationException($"'{n}' deve ser > 0.");
            return v;
        }
        Point2d[] Pts(string n, int min)
        {
            if (!Json.Has(it, n) || it.GetProperty(n).ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"falta '{n}' (lista de [x, y]).");
            Point2d[] pts = it.GetProperty(n).EnumerateArray().Select(e => { Point3d q = Json.Point(e, n); return new Point2d(q.X, q.Y); }).ToArray();
            if (pts.Length < min) throw new InvalidOperationException($"'{n}' precisa de pelo menos {min} pontos.");
            return pts;
        }

        switch (type)
        {
            case "line":
            {
                Point3d a = Json.Point(it, "start", "start"), b = Json.Point(it, "end", "end");
                if (a.IsEqualTo(b)) throw new InvalidOperationException("início e fim iguais.");
                return c => c.Line(a, b, layer, color);
            }
            case "polyline":
            {
                Point2d[] pts = Pts("points", 2);
                bool closed = Json.Bool(it, "closed") ?? false;
                return c => c.Polyline(pts, closed, layer, color);
            }
            case "rectangle":
            {
                Point3d a = Json.Point(it, "corner1", "corner1"), b = Json.Point(it, "corner2", "corner2");
                return c => c.Rect(new Point2d(a.X, a.Y), new Point2d(b.X, b.Y), layer, color);
            }
            case "circle":
            {
                Point3d ctr = Json.Point(it, "center", "center"); double r = Pos("radius");
                return c => c.Add(new Circle(ctr, Vector3d.ZAxis, r), layer, color);
            }
            case "arc":
            {
                Point3d ctr = Json.Point(it, "center", "center"); double r = Pos("radius");
                double a0 = Deg("start_angle"), a1 = Deg("end_angle");
                return c => c.Add(new Arc(ctr, r, a0, a1), layer, color);
            }
            case "text":
            {
                Point3d pos = Json.Point(it, "position", "position");
                string s = Json.Str(it, "text") ?? throw new InvalidOperationException("falta 'text'.");
                double h = Pos("height"); double rot = Deg("rotation");
                string just = (Json.Str(it, "justify") ?? "left").ToLowerInvariant();
                if (just is not ("left" or "center" or "right")) throw new InvalidOperationException("'justify' deve ser left, center ou right.");
                return c => c.Text(pos, s, h, rot, layer, just, color);
            }
            case "mtext":
            {
                Point3d pos = Json.Point(it, "position", "position");
                string s = Json.Str(it, "text") ?? throw new InvalidOperationException("falta 'text'.");
                double h = Pos("height"); double w = Json.Num(it, "width") ?? 0;
                return c => c.MText(pos, s.Replace("\n", "\\P"), h, layer, AttachmentPoint.TopLeft, w, color);
            }
            case "dimension":
            {
                Point3d a = Json.Point(it, "p1", "p1"), b = Json.Point(it, "p2", "p2"), dl = Json.Point(it, "dim_line_point", "dim_line_point");
                double? rot = Json.Has(it, "rotation") ? Deg("rotation") : null;
                string? txt = Json.Str(it, "text");
                return c => c.Dim(a, b, dl, rot, layer, txt);
            }
            case "hatch":
            {
                Point2d[] pts = Pts("points", 3);
                string pat = Json.Str(it, "pattern") ?? "SOLID";
                double sc = Json.Num(it, "scale") ?? 1;
                return c => c.Hatch(pts, pat, sc, layer, color);
            }
            case "block":
            {
                string name = Json.Str(it, "name") ?? throw new InvalidOperationException("falta 'name'.");
                if (!bt.Has(name)) throw new InvalidOperationException($"o bloco '{name}' não existe neste desenho (veja list_blocks).");
                Point3d pos = Json.Point(it, "position", "position");
                double sc = Json.Num(it, "scale") ?? 1; double rot = Deg("rotation");
                Dictionary<string, string>? attrs = null;
                if (Json.Has(it, "attributes") && it.GetProperty("attributes").ValueKind == JsonValueKind.Object)
                    attrs = it.GetProperty("attributes").EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ToString(), StringComparer.OrdinalIgnoreCase);
                return c => c.Block(name, pos, sc, rot, attrs, layer);
            }
            default:
                throw new InvalidOperationException("tipo desconhecido. Use line, polyline, rectangle, circle, arc, text, mtext, dimension, hatch ou block.");
        }
    }

    internal static string ValidLayer(string raw, string what)
    {
        string name = raw.Trim();
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch (Autodesk.AutoCAD.Runtime.Exception) { throw new InvalidOperationException($"Nome de layer inválido em '{what}': '{name}'."); }
        return name;
    }
}
