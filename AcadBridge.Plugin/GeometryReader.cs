using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// Lê a geometria do desenho como o usuário a VÊ: entra em blocos e XREFs (aplicando a transformação de
/// cada inserção) e devolve tudo em coordenadas do desenho (WCS).
///
/// Por que isso importa: em desenhos reais a planta costuma estar dentro de um XREF (arquivo base) ou de
/// um bloco "PAVIMENTO TÉRREO"; ler só o Model mostraria uma única inserção. E nomes de layers/blocos não
/// são confiáveis (paredes na layer 0, porta num bloco chamado "cadeira"), então quem usa esta leitura
/// precisa da GEOMETRIA, não dos nomes.
///
/// Entidades dentro de blocos são copiadas com GetTransformedCopy (cópias fora do banco, descartadas depois):
/// nada no desenho é alterado.
/// </summary>
internal static class GeometryReader
{
    private const int MaxDepth = 6;          // blocos dentro de blocos dentro de XREF...
    private const int MaxVisited = 50_000;   // proteção para desenhos enormes

    /// <summary>Uma entidade "vista" pelo usuário, já em WCS.</summary>
    internal sealed class Item
    {
        public Entity Entity = null!;   // cópia transformada (Owned=true) ou a própria entidade do banco
        public bool Owned;              // true = cópia nossa: precisa de Dispose
        public string Layer = "";       // layer efetiva ("0" dentro de bloco herda a da inserção; XREF vira "xref|layer")
        public string Source = "";      // caminho: "" (Model) | "xref:BASE/" | "block:PAVIMENTO/"...
        public string Handle = "";      // handle do objeto do Model (ou da inserção que o contém): é o que se edita
    }

    internal sealed class Stats
    {
        public int Visited, Skipped, SkippedDepth, Hidden;
        public bool Truncated;
        public readonly SortedSet<string> UnresolvedXrefs = new(StringComparer.OrdinalIgnoreCase);
        public readonly SortedDictionary<string, int> Expanded = new(StringComparer.OrdinalIgnoreCase);
    }

    internal sealed class Options
    {
        public Extents3d? Region;            // só entidades cujo retângulo envolvente cruza esta região
        public bool IncludeBlocks = true;    // entrar em blocos e XREFs
        public bool IncludeHidden;           // incluir layers desligadas/congeladas
        public Func<string, bool>? LayerFilter;
        public HashSet<string>? Types;       // nomes DXF (LINE, LWPOLYLINE...)
        public ObjectId[]? Only;             // só estes objetos do Model (ex.: seleção do usuário)
    }

    /// <summary>
    /// Percorre o Model e chama <paramref name="visit"/> para cada entidade visível (WCS). visit devolve
    /// false para parar. As cópias são descartadas logo depois de cada visit: não guarde Item.Entity.
    /// </summary>
    public static Stats Walk(Database db, Transaction tr, Options opt, Func<Item, bool> visit)
    {
        var stats = new Stats();
        var hostLayers = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
        IEnumerable<ObjectId> ids = opt.Only ?? ms.Cast<ObjectId>();
        WalkIds(db, tr, tr, ids, Matrix3d.Identity, null, "", null, 0, opt, visit, stats, hostLayers, null);
        return stats;
    }

    private static bool WalkIds(Database hostDb, Transaction hostTr, Transaction tr, IEnumerable<ObjectId> ids, Matrix3d xf, string? parentLayer,
                                string source, string? xrefName, int depth, Options opt, Func<Item, bool> visit,
                                Stats stats, Dictionary<string, bool> hostLayers, string? top)
    {
        foreach (ObjectId id in ids)
        {
            if (stats.Visited >= MaxVisited) { stats.Truncated = true; return false; }
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent || !ent.Visible) continue;
            stats.Visited++;
            string handle = top ?? ent.Handle.ToString();

            // Layer efetiva: entidade na layer "0" dentro de bloco aparece na layer da inserção.
            // Entidades de um XREF anexado já trazem a layer como "XREF|layer"; só prefixa se não vier assim.
            string own = xrefName != null && !ent.Layer.Equals("0", StringComparison.Ordinal) && !ent.Layer.Contains('|')
                ? $"{xrefName}|{ent.Layer}" : ent.Layer;
            string layer = own == "0" && parentLayer != null ? parentLayer : own;
            if (!opt.IncludeHidden && !IsLayerVisible(hostDb, hostTr, layer, hostLayers)) { stats.Hidden++; continue; }

            if (ent is BlockReference br && opt.IncludeBlocks)
            {
                if (depth >= MaxDepth) { stats.SkippedDepth++; continue; }
                if (opt.Region is { } reg && TryExtents(ent, xf, out Extents3d bx) && !Intersects(bx, reg)) continue;
                var btr = (BlockTableRecord)tr.GetObject(br.BlockTableRecord, OpenMode.ForRead);
                Matrix3d m = xf * br.BlockTransform;
                string name = EffectiveName(br, tr);
                if (btr.IsFromExternalReference)
                {
                    if (!btr.IsResolved) { stats.UnresolvedXrefs.Add(btr.Name); continue; }
                    Database? xdb = btr.GetXrefDatabase(false);
                    if (xdb == null) { stats.UnresolvedXrefs.Add(btr.Name); continue; }
                    stats.Expanded[$"xref:{btr.Name}"] = stats.Expanded.GetValueOrDefault($"xref:{btr.Name}") + 1;
                    using Transaction xtr = xdb.TransactionManager.StartOpenCloseTransaction();
                    var xbt = (BlockTable)xtr.GetObject(xdb.BlockTableId, OpenMode.ForRead);
                    var xms = (BlockTableRecord)xtr.GetObject(xbt[BlockTableRecord.ModelSpace], OpenMode.ForRead);
                    // Layers do XREF: a visibilidade vale pelas "xref|layer" do desenho hospedeiro (abrir com o tr do host).
                    if (!WalkIds(hostDb, hostTr, xtr, xms.Cast<ObjectId>(), m, layer, $"{source}xref:{btr.Name}/", btr.Name, depth + 1, opt, visit, stats, hostLayers, handle))
                        return false;
                }
                else
                {
                    stats.Expanded[$"block:{name}"] = stats.Expanded.GetValueOrDefault($"block:{name}") + 1;
                    if (!WalkIds(hostDb, hostTr, tr, btr.Cast<ObjectId>(), m, layer, $"{source}block:{name}/", xrefName, depth + 1, opt, visit, stats, hostLayers, handle))
                        return false;
                    // Atributos são entidades da inserção, não da definição.
                    foreach (ObjectId aid in br.AttributeCollection)
                        if (!VisitOne(tr.GetObject(aid, OpenMode.ForRead) as Entity, xf, layer, $"{source}block:{name}/", handle, opt, visit, stats))
                            return false;
                }
                continue;
            }
            if (!VisitOne(ent, xf, layer, source, handle, opt, visit, stats)) return false;
        }
        return true;
    }

    private static bool VisitOne(Entity? ent, Matrix3d xf, string layer, string source, string handle, Options opt, Func<Item, bool> visit, Stats stats)
    {
        if (ent == null || !ent.Visible) return true;
        if (opt.Types != null && !opt.Types.Contains(ent.GetRXClass().DxfName)) return true;
        if (opt.LayerFilter != null && !opt.LayerFilter(layer)) return true;

        Entity e = ent;
        bool owned = false;
        if (!xf.IsEqualTo(Matrix3d.Identity))
        {
            try { e = ent.GetTransformedCopy(xf); owned = true; }
            catch { stats.Skipped++; return true; }   // ex.: arco com escala não uniforme
        }
        try
        {
            if (opt.Region is { } reg)
            {
                if (!TryExtents(e, Matrix3d.Identity, out Extents3d ex) || !Intersects(ex, reg)) return true;
            }
            return visit(new Item { Entity = e, Owned = owned, Layer = layer, Source = source, Handle = handle });
        }
        finally { if (owned) e.Dispose(); }
    }

    private static bool IsLayerVisible(Database db, Transaction tr, string layer, Dictionary<string, bool> cache)
    {
        if (cache.TryGetValue(layer, out bool v)) return v;
        v = true;
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(layer))
        {
            var l = (LayerTableRecord)tr.GetObject(lt[layer], OpenMode.ForRead);
            v = !l.IsOff && !l.IsFrozen;
        }
        cache[layer] = v;
        return v;
    }

    internal static bool TryExtents(Entity e, Matrix3d xf, out Extents3d ext)
    {
        try
        {
            ext = e.GeometricExtents;
            if (!xf.IsEqualTo(Matrix3d.Identity)) ext.TransformBy(xf);
            return true;
        }
        catch { ext = default; return false; }
    }

    internal static bool Intersects(Extents3d a, Extents3d b) =>
        a.MinPoint.X <= b.MaxPoint.X && a.MaxPoint.X >= b.MinPoint.X &&
        a.MinPoint.Y <= b.MaxPoint.Y && a.MaxPoint.Y >= b.MinPoint.Y;

    internal static string EffectiveName(BlockReference br, Transaction tr)
    {
        try
        {
            if (br.IsDynamicBlock && tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) is BlockTableRecord d) return d.Name;
        }
        catch { /* bloco dinâmico corrompido: usa o nome direto */ }
        return br.Name;
    }

    // =========================================================================================
    // read_geometry
    // =========================================================================================
    public static object Read(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        var opt = new Options
        {
            IncludeBlocks = Json.Bool(p, "include_blocks") ?? true,
            IncludeHidden = Json.Bool(p, "include_hidden") ?? false,
        };
        int max = Math.Clamp(Json.Int(p, "max_items") ?? 300, 1, 3000);

        if (Json.Has(p, "region")) opt.Region = Json.Region(p.GetProperty("region"));
        if (Json.Bool(p, "use_selection") == true)
        {
            PromptSelectionResult psr = doc.Editor.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null)
                throw new InvalidOperationException("Nada selecionado. Peça ao usuário para selecionar os objetos (sem comando ativo) ou use 'region'.");
            opt.Only = psr.Value.GetObjectIds();
        }
        if (Json.StringArray(p, "layers") is { Length: > 0 } pats)
        {
            Regex[] rx = pats.Select(WildcardToRegex).ToArray();
            opt.LayerFilter = l => rx.Any(r => r.IsMatch(l) || r.IsMatch(l.Contains('|') ? l[(l.IndexOf('|') + 1)..] : l));
        }
        if (Json.StringArray(p, "types") is { Length: > 0 } types)
            opt.Types = new HashSet<string>(types.Select(t => t.Trim().ToUpperInvariant()));

        string format = (Json.Str(p, "format") ?? "compact").Trim().ToLowerInvariant();
        if (format is not ("compact" or "full")) throw new InvalidOperationException("'format' deve ser 'compact' ou 'full'.");
        bool full = format == "full";

        var items = new List<object>();
        var byLayerType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var arcRadii = new List<double>();
        // compact: layers e origens viram apelidos (L1, S1) com legenda, para não repetir nomes longos de XREF
        var layerIds = new Dictionary<string, string>(StringComparer.Ordinal);
        var sourceIds = new Dictionary<string, string>(StringComparer.Ordinal);
        string Alias(Dictionary<string, string> d, string key, string prefix) =>
            d.TryGetValue(key, out string? v) ? v : d[key] = prefix + (d.Count + 1);
        int total = 0;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        Stats stats = Walk(db, tr, opt, it =>
        {
            total++;
            string type = it.Entity.GetRXClass().DxfName;
            byLayerType[$"{it.Layer} | {type}"] = byLayerType.GetValueOrDefault($"{it.Layer} | {type}") + 1;
            if (it.Entity is Arc a) arcRadii.Add(a.Radius);
            if (items.Count < max)
            {
                if (full) items.Add(Describe(it));
                else
                {
                    string l = Alias(layerIds, it.Layer, "L");
                    string s = it.Source.Length > 0 ? " " + Alias(sourceIds, it.Source.TrimEnd('/'), "S") : "";
                    string col = ColorTag(it.Entity) is { } cc ? " cor=" + cc : "";
                    items.Add($"{it.Handle} {type} {l}{s}{col} {Compact(it.Entity)}");
                }
            }
            return true;
        });

        bool truncated = total > items.Count || stats.Truncated;
        var res = new Dictionary<string, object?>
        {
            ["drawing_units_declared"] = db.Insunits.ToString(),
            ["unit_guess"] = Units.GuessFromArcs(arcRadii) is { } g ? new { unit = g.Name, reason = g.Reason } : null,
            ["region"] = opt.Region is { } r ? new { min = P2(r.MinPoint), max = P2(r.MaxPoint) } : null,
            ["total_found"] = total,
            ["returned"] = items.Count,
            ["truncated"] = truncated,
            ["counts_by_layer_and_type"] = byLayerType,
            ["expanded_blocks_and_xrefs"] = stats.Expanded,
            ["unresolved_xrefs"] = stats.UnresolvedXrefs,
            ["hidden_skipped"] = stats.Hidden,
        };
        if (full)
        {
            res["note"] = "Coordenadas em WCS, já com a transformação de blocos/XREFs aplicada. 'layer' é a layer efetiva; " +
                          "'source' diz de onde veio (xref:/block:); 'handle' é o objeto do Model que o contém. " +
                          "Nomes de layers e blocos podem não refletir o conteúdo: confira pela geometria.";
        }
        else
        {
            res["legend"] = "Cada item: <handle> <TIPO> <layer> [origem] <geometria>. Coordenadas em WCS (blocos/XREFs já " +
                            "transformados). handle = objeto do Model que contém o item (num XREF/bloco, é a inserção). " +
                            "x,y>x,y = linha; pts= vértices (polilinha; 'arc' = tem trechos curvos); c= centro; r= raio; " +
                            "a= ângulos em graus; box= retângulo envolvente; L= comprimento; A= área; h= altura do texto; cor= cor própria " +
                            "(ausente = ByLayer). " +
                            "Nomes de layers e blocos podem enganar: confira pela geometria. format='full' dá JSON detalhado.";
            res["layers"] = layerIds.ToDictionary(kv => kv.Value, kv => kv.Key);
            if (sourceIds.Count > 0) res["sources"] = sourceIds.ToDictionary(kv => kv.Value, kv => kv.Key);
        }
        if (truncated) res["hint"] = "Há mais itens que os devolvidos: filtre por region/types/layers ou aumente max_items.";
        res["items"] = items;
        return res;
    }

    private static string F(double v) => R(v).ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
    private static string Pt(Point3d q) => $"{F(q.X)},{F(q.Y)}";

    /// <summary>Geometria em uma linha (formato compacto: ~4x menos tokens que o JSON detalhado).</summary>
    internal static string Compact(Entity e)
    {
        string Box() => TryExtents(e, Matrix3d.Identity, out Extents3d ex) ? $"box={Pt(ex.MinPoint)}..{Pt(ex.MaxPoint)}" : "";
        static string Q(string s) => "\"" + s.Replace("\r", "").Replace("\n", "\\n").Replace("\"", "'") + "\"";
        switch (e)
        {
            case Line l:
                return $"{Pt(l.StartPoint)}>{Pt(l.EndPoint)} L={F(l.Length)}";
            case Polyline pl:
            {
                var sb = new System.Text.StringBuilder(pl.Closed ? "closed" : "open");
                bool arcs = false;
                for (int i = 0; i < pl.NumberOfVertices; i++) if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) { arcs = true; break; }
                if (arcs) sb.Append(" arc");
                sb.Append($" L={F(pl.Length)}");
                if (pl.Closed) { try { sb.Append($" A={F(pl.Area)}"); } catch { } }
                sb.Append(" pts=");
                int n = Math.Min(pl.NumberOfVertices, 300);
                for (int i = 0; i < n; i++) { if (i > 0) sb.Append(' '); sb.Append(Pt(pl.GetPoint3dAt(i))); }
                if (pl.NumberOfVertices > 300) sb.Append($" …(+{pl.NumberOfVertices - 300})");
                return sb.ToString();
            }
            case Arc a:
                return $"c={Pt(a.Center)} r={F(a.Radius)} a={F(a.StartAngle * 180 / Math.PI)}..{F(a.EndAngle * 180 / Math.PI)} ends={Pt(a.StartPoint)} {Pt(a.EndPoint)}";
            case Circle c:
                return $"c={Pt(c.Center)} r={F(c.Radius)}";
            case DBText t:
                return $"{Q(t.TextString)} at={Pt(t.Position)} h={F(t.Height)}" + (Math.Abs(t.Rotation) > 1e-9 ? $" rot={F(t.Rotation * 180 / Math.PI)}" : "");
            case MText mt:
                return $"{Q(mt.Text)} at={Pt(mt.Location)} h={F(mt.TextHeight)}";
            case Dimension dim:
                return $"={F(dim.Measurement)}" + (string.IsNullOrEmpty(dim.DimensionText) ? "" : $" text={Q(dim.DimensionText)}") + $" {Box()}";
            case Hatch h:
            {
                string area = "";
                try { area = $" A={F(h.Area)}"; } catch { }
                return $"{h.PatternName}{area} {Box()}";
            }
            case BlockReference br:   // só aparece com include_blocks=false
                return $"block={Q(br.Name)} at={Pt(br.Position)}" + (Math.Abs(br.Rotation) > 1e-9 ? $" rot={F(br.Rotation * 180 / Math.PI)}" : "") +
                       (Math.Abs(br.ScaleFactors.X - 1) > 1e-9 ? $" scale={F(br.ScaleFactors.X)}" : "") + $" {Box()}";
            default:
                return Box();
        }
    }

    /// <summary>Cor do objeto quando NÃO é ByLayer (ACI, R,G,B ou byblock); null = segue a layer.</summary>
    internal static string? ColorTag(Entity e)
    {
        var c = e.Color;
        if (c.IsByLayer) return null;
        if (c.IsByBlock) return "byblock";
        if (c.IsByAci) return c.ColorIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return $"{c.Red},{c.Green},{c.Blue}";
    }

    private static object Describe(Item it)
    {
        Entity e = it.Entity;
        var d = new Dictionary<string, object?>
        {
            ["type"] = e.GetRXClass().DxfName,
            ["layer"] = it.Layer,
            ["handle"] = it.Handle,
        };
        if (it.Source.Length > 0) d["source"] = it.Source.TrimEnd('/');
        if (ColorTag(e) is { } col) d["color"] = col;
        switch (e)
        {
            case Line l:
                d["start"] = P2(l.StartPoint); d["end"] = P2(l.EndPoint); d["length"] = R(l.Length);
                break;
            case Polyline pl:
                d["closed"] = pl.Closed; d["length"] = R(pl.Length);
                if (pl.Closed) { try { d["area"] = R(pl.Area); } catch { } }
                var pts = new List<double[]>();
                bool arcs = false;
                for (int i = 0; i < pl.NumberOfVertices && i < 300; i++)
                {
                    pts.Add(P2(pl.GetPoint3dAt(i)));
                    if (Math.Abs(pl.GetBulgeAt(i)) > 1e-9) arcs = true;
                }
                d["points"] = pts; if (arcs) d["has_arc_segments"] = true;
                if (pl.NumberOfVertices > 300) d["points_truncated"] = true;
                break;
            case Arc a:
                d["center"] = P2(a.Center); d["radius"] = R(a.Radius);
                d["start"] = P2(a.StartPoint); d["end"] = P2(a.EndPoint);
                break;
            case Circle c:
                d["center"] = P2(c.Center); d["radius"] = R(c.Radius);
                break;
            case DBText t:
                d["text"] = t.TextString; d["position"] = P2(t.Position); d["height"] = R(t.Height); d["rotation_deg"] = R(t.Rotation * 180 / Math.PI);
                break;
            case MText mt:
                d["text"] = mt.Text; d["position"] = P2(mt.Location); d["height"] = R(mt.TextHeight);
                break;
            case Dimension dim:
                d["measurement"] = R(dim.Measurement); if (!string.IsNullOrEmpty(dim.DimensionText)) d["text_override"] = dim.DimensionText;
                break;
            case Hatch h:
                d["pattern"] = h.PatternName; try { d["area"] = R(h.Area); } catch { }
                break;
            case BlockReference br:   // só aparece com include_blocks=false
                d["block"] = br.Name; d["position"] = P2(br.Position); d["rotation_deg"] = R(br.Rotation * 180 / Math.PI);
                d["scale"] = new[] { R(br.ScaleFactors.X), R(br.ScaleFactors.Y) };
                break;
        }
        if (TryExtents(e, Matrix3d.Identity, out Extents3d ex)) d["bounds"] = new { min = P2(ex.MinPoint), max = P2(ex.MaxPoint) };
        return d;
    }

    internal static Regex WildcardToRegex(string pattern) =>
        new("^" + Regex.Escape(pattern.Trim()).Replace("\\*", ".*").Replace("\\?", ".") + "$", RegexOptions.IgnoreCase);

    internal static double R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;
    internal static double[] P2(Point3d p) => new[] { R(p.X), R(p.Y) };
}

/// <summary>Unidade do desenho deduzida pela geometria (o INSUNITS declarado costuma estar errado).</summary>
internal static class Units
{
    internal sealed record Guess(string Name, double PerMeter, string Reason);

    public static Guess? FromName(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "m" or "meters" or "metros" or "metro" => new Guess("m", 1, "informada"),
        "cm" or "centimeters" or "centímetros" or "centimetros" => new Guess("cm", 100, "informada"),
        "mm" or "millimeters" or "milímetros" or "milimetros" => new Guess("mm", 1000, "informada"),
        _ => null,
    };

    /// <summary>Raio típico de abertura de porta = 0,6–1,2 m. A mediana dos arcos nessa faixa denuncia a unidade.</summary>
    public static Guess? GuessFromArcs(IReadOnlyCollection<double> radii)
    {
        foreach ((string n, double f) in new[] { ("m", 1.0), ("cm", 100.0), ("mm", 1000.0) })
        {
            var doorish = radii.Where(r => r >= 0.55 * f && r <= 1.3 * f).OrderBy(r => r).ToList();
            if (doorish.Count >= 1 && doorish.Count >= radii.Count / 4.0)
                return new Guess(n, f, $"{doorish.Count} arco(s) com raio típico de porta (mediana {doorish[doorish.Count / 2]:0.###} {n})");
        }
        return null;
    }

    /// <summary>Último recurso: comprimento plausível de parede (0,3–40 m).</summary>
    public static Guess? GuessFromLength(double length)
    {
        if (length >= 0.3 && length <= 40) return new Guess("m", 1, $"comprimento {length:0.##} só é plausível em metros");
        if (length >= 3000) return new Guess("mm", 1000, $"comprimento {length:0} só é plausível em milímetros");
        return null;   // 30..3000 é ambíguo entre cm e mm
    }
}

/// <summary>Leitura de parâmetros JSON (comum às ferramentas novas).</summary>
internal static class Json
{
    public static bool Has(JsonElement p, string n) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(n, out JsonElement v) && v.ValueKind != JsonValueKind.Null;
    public static bool? Bool(JsonElement p, string n) =>
        Has(p, n) && p.GetProperty(n).ValueKind is JsonValueKind.True or JsonValueKind.False ? p.GetProperty(n).GetBoolean() : null;
    public static int? Int(JsonElement p, string n) =>
        Has(p, n) && p.GetProperty(n).ValueKind == JsonValueKind.Number && p.GetProperty(n).TryGetInt32(out int i) ? i : null;
    public static double? Num(JsonElement p, string n) =>
        Has(p, n) && p.GetProperty(n).ValueKind == JsonValueKind.Number ? p.GetProperty(n).GetDouble() : null;
    public static string? Str(JsonElement p, string n) =>
        Has(p, n) && p.GetProperty(n).ValueKind == JsonValueKind.String ? p.GetProperty(n).GetString() : null;
    public static string[]? StringArray(JsonElement p, string n) =>
        Has(p, n) && p.GetProperty(n).ValueKind == JsonValueKind.Array
            ? p.GetProperty(n).EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

    /// <summary>[x, y] ou [x, y, z].</summary>
    public static Point3d Point(JsonElement el, string what)
    {
        if (el.ValueKind != JsonValueKind.Array) throw new InvalidOperationException($"'{what}' deve ser [x, y].");
        double[] v = el.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : double.NaN).ToArray();
        if (v.Length < 2 || v.Length > 3 || v.Any(d => !double.IsFinite(d))) throw new InvalidOperationException($"'{what}' deve ser [x, y] com números.");
        return new Point3d(v[0], v[1], v.Length == 3 ? v[2] : 0);
    }

    public static Point3d Point(JsonElement p, string n, string what) =>
        Has(p, n) ? Point(p.GetProperty(n), what) : throw new InvalidOperationException($"Parâmetro obrigatório ausente: '{what}'.");

    public static Extents3d Region(JsonElement r)
    {
        Point3d a = Point(r, "min", "region.min"), b = Point(r, "max", "region.max");
        return new Extents3d(new Point3d(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), -1e9), new Point3d(Math.Max(a.X, b.X), Math.Max(a.Y, b.Y), 1e9));
    }
}
