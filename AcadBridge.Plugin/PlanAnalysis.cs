using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// analyze_plan: calcula UMA VEZ o "mapa" da planta numa região — ambientes (nome e área pelos textos, contorno
/// pela geometria), paredes de cada ambiente com espessura e aberturas, portas (largura, entre quais ambientes,
/// para qual lado abrem) — e guarda em memória. As outras ferramentas passam a aceitar NOMES ("BWC", "R3", parede
/// "W2" ou "janela") no lugar de coordenadas, o que poupa ao modelo ler centenas de linhas e fazer contas.
///
/// Reaproveita o que já foi validado em desenhos reais:
///  - contornos: o arranjo planar do hatch_regions (entra em blocos/XREFs, portas de giro contam como fechadas);
///  - aberturas de cada parede: a mesma análise da vista (porta / janela / vão, com a evidência).
/// O que a própria IA desenhou (layers IA-*) é ignorado: móveis e cotas não viram paredes.
/// O mapa é refeito sozinho quando o desenho muda (objetos fora das layers IA-*).
/// </summary>
internal static class PlanAnalysis
{
    internal sealed class Wall
    {
        public string Id = "";
        public Point2d Start, End;      // já orientada: da esquerda para a direita de quem olha de dentro do ambiente
        public double LengthM, ThicknessM;
        public string Side = "";        // em cima / embaixo / esquerda / direita (na tela, sem giro de vista)
        public List<Elevation.ProbeOpening> Openings = new();
        public List<string> OpeningIds = new();
    }

    internal sealed class Room
    {
        public string Id = "", Name = "";
        public double AreaM2;
        public double? TextAreaM2;
        public Point2d Inside;
        public PlanarFaces.Face Face = null!;
        public readonly List<Wall> Walls = new();
    }

    internal sealed class Door
    {
        public string Id = "";
        public double WidthM;
        public Point2d Hinge;
        public Room? A, B, SwingInto;
    }

    internal sealed class Map
    {
        public long Version;
        public string Params = "{}";
        public Extents3d Region;
        public Units.Guess Unit = null!;
        public readonly List<Room> Rooms = new();
        public readonly List<Door> Doors = new();
        public readonly List<string> Warnings = new();
        public int Segments, Windows;
        public double Seconds;
    }

    private const int MaxSegments = 30_000;
    private const int MaxProbes = 120;

    // ------------------------------------------------------------------------------------------
    // Versão do desenho: sobe quando uma entidade fora das layers IA-* é criada, alterada ou apagada.
    // Chave = ponteiro nativo do banco: doc.Database pode devolver um invólucro .NET novo a cada acesso,
    // então uma tabela por objeto .NET "esquecia" o mapa entre uma chamada e outra.
    private sealed class Tracker { public long Version; }
    private static readonly Dictionary<IntPtr, Tracker> Trackers = new();
    private static readonly Dictionary<IntPtr, Map> Maps = new();
    private static bool _cleanupHooked;

    private static Tracker Track(Database db)
    {
        if (!_cleanupHooked)
        {
            _cleanupHooked = true;
            Application.DocumentManager.DocumentToBeDestroyed += (_, e) =>
            {
                try { IntPtr k = e.Document.Database.UnmanagedObject; Trackers.Remove(k); Maps.Remove(k); } catch { }
            };
        }
        if (Trackers.TryGetValue(db.UnmanagedObject, out Tracker? t)) return t;
        var tr = new Tracker();
        void Bump(DBObject o)
        {
            try
            {
                if (o is Entity en && !en.Layer.StartsWith("IA-", StringComparison.OrdinalIgnoreCase)) tr.Version++;
            }
            catch { tr.Version++; }   // objeto apagado/inacessível: na dúvida, invalida
        }
        db.ObjectAppended += (_, e) => Bump(e.DBObject);
        db.ObjectErased += (_, e) => Bump(e.DBObject);
        db.ObjectModified += (_, e) => Bump(e.DBObject);
        Trackers[db.UnmanagedObject] = tr;
        return tr;
    }

    // ------------------------------------------------------------------------------------------
    public static object Run(Document doc, JsonElement p)
    {
        Map m = Analyze(doc, p);
        Maps[doc.Database.UnmanagedObject] = m;
        return Report(m, cached: false);
    }

    private static Map Analyze(Document doc, JsonElement p)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Database db = doc.Database;
        Tracker tracker = Track(db);
        var m = new Map { Params = p.GetRawText(), Region = RegionOf(doc, p) };

        // 1) unidade (arcos de porta), depois a geometria com portas fechadas e textos
        var probe = new HatchRegions.Extra { SkipIa = true, DoorMax = -1 };
        var (_, _, radii0, _, _) = HatchRegions.Collect(db, p, m.Region, probe);
        m.Unit = Units.FromName(Json.Str(p, "units")) ?? Units.GuessFromArcs(radii0)
                 ?? throw new InvalidOperationException("Não consegui deduzir a unidade (sem arcos de porta na região). Informe units: 'm', 'cm' ou 'mm'.");
        double uf = m.Unit.PerMeter, a2 = uf * uf;
        var extra = new HatchRegions.Extra { SkipIa = true, AllTexts = true, DoorMin = 0.55 * uf, DoorMax = 1.3 * uf };
        var (segs, _, _, tags, _) = HatchRegions.Collect(db, p, m.Region, extra);
        m.Segments = segs.Count;
        if (segs.Count == 0) throw new InvalidOperationException("Não há linhas nessa região (confira 'region').");
        if (segs.Count > MaxSegments)
            throw new InvalidOperationException($"Linhas demais na região ({segs.Count}; máx. {MaxSegments}). Use uma região menor (um pavimento ou um apartamento).");
        var faces = new PlanarFaces(segs, 0.001 * uf, tags);

        // 2) ambientes: faces com texto dentro (nome/área) + faces grandes sem texto
        double edge = 0.002 * uf;
        bool InsideRegion(PlanarFaces.Face f) =>
            f.Box.MinPoint.X > m.Region.MinPoint.X + edge && f.Box.MinPoint.Y > m.Region.MinPoint.Y + edge &&
            f.Box.MaxPoint.X < m.Region.MaxPoint.X - edge && f.Box.MaxPoint.Y < m.Region.MaxPoint.Y - edge;
        bool Roomish(PlanarFaces.Face f) =>
            f.Area / a2 >= 1.0 && f.Area / a2 <= 400 && 2 * f.Area / f.Perimeter >= 0.35 * uf && InsideRegion(f);

        var texts = new Dictionary<PlanarFaces.Face, List<(Point2d C, string T)>>();
        foreach ((Point2d c, string t) in extra.Texts)
            if (faces.FaceAt(c) is { } f && Roomish(f))
                (texts.TryGetValue(f, out var l) ? l : texts[f] = new()).Add((c, t));

        var rooms = new List<Room>();
        foreach (var (f, ts) in texts)
        {
            var ordered = ts.OrderByDescending(x => x.C.Y).ThenBy(x => x.C.X).ToList();
            string name = string.Join(" / ", ordered.Select(x => CleanName(x.T)).Where(s => s.Length > 0).Distinct());
            double? ta = ordered.Select(x => HatchRegions.AreaValue(x.T)).FirstOrDefault(v => v != null);
            rooms.Add(new Room { Face = f, Name = name.Length > 0 ? name : "SEM NOME", TextAreaM2 = ta, AreaM2 = f.Area / a2, Inside = ordered[0].C });
        }
        foreach (PlanarFaces.Face f in faces.Bounded)
            if (!texts.ContainsKey(f) && Roomish(f) && f.Area / a2 >= 4 && 2 * f.Area / f.Perimeter >= 0.6 * uf && InteriorPoint(f, uf) is { } ip)
                rooms.Add(new Room { Face = f, Name = "SEM NOME", AreaM2 = f.Area / a2, Inside = ip });
        rooms = rooms.OrderByDescending(r => Math.Round((r.Face.Box.MinPoint.Y + r.Face.Box.MaxPoint.Y) / 2 / uf, 0))
                     .ThenBy(r => r.Face.Box.MinPoint.X).ToList();
        for (int i = 0; i < rooms.Count; i++) rooms[i].Id = $"R{i + 1}";
        m.Rooms.AddRange(rooms);
        if (rooms.Count == 0)
            m.Warnings.Add("Nenhum ambiente fechado com texto ou com mais de 4 m² nessa região. Confira se a região cobre a planta inteira (ambientes cortados pela borda ficam de fora).");

        Room? RoomAt(Point2d q) { PlanarFaces.Face? f = faces.FaceAt(q); return f == null ? null : rooms.FirstOrDefault(r => r.Face == f); }
        // primeiro ambiente andando a partir de q na direção n (atravessa espessura de parede, batente, soleira)
        Room? RoomToward(Point2d q, Vector2d n)
        {
            foreach (double s in new[] { 0.15, 0.3, 0.45, 0.6, 0.8 })
                if (RoomAt(q + n * s * uf) is { } r) return r;
            return null;
        }

        // 3) portas: a linha dobradiça→ponta que separa dois ambientes é o vão; o lado do arco diz para onde abre
        foreach (var (c, pa, pb, r) in extra.DoorArcs)
        {
            Room? x = null, y = null;
            // o vão é a linha da folha FECHADA: a ponta encosta no batente (logo depois dela não há ambiente);
            // a folha aberta termina dentro do ambiente. Só nessa linha se anda "através" da parede.
            bool InJamb(Point2d e) => RoomAt(e + (e - c).GetNormal() * 0.08 * uf) == null;
            var lines = new[] { pa, pb }.OrderByDescending(InJamb).ToArray();
            for (int li = 0; li < lines.Length; li++)
            {
                Point2d e = lines[li];
                Vector2d d = (e - c).GetNormal(); var n = new Vector2d(-d.Y, d.X);
                Point2d mid = c + (e - c) / 2;
                bool doorway = li == 0 && InJamb(e) && !InJamb(lines[1]);
                Room? r1 = doorway ? RoomToward(mid, n) : RoomAt(mid + n * 0.25 * uf);
                Room? r2 = doorway ? RoomToward(mid, -n) : RoomAt(mid - n * 0.25 * uf);
                if (r1 != r2 || doorway) { x = r1; y = r2; break; }
            }
            if (x == null && y == null) continue;
            Vector2d bis = ((pa - c).GetNormal() + (pb - c).GetNormal());
            Room? swing = bis.Length > 1e-9 ? RoomAt(c + bis.GetNormal() * 0.6 * r) : null;
            m.Doors.Add(new Door { WidthM = r / uf, Hinge = c, A = x, B = y, SwingInto = swing });
        }
        for (int i = 0; i < m.Doors.Count; i++) m.Doors[i].Id = $"P{i + 1}";

        // 4) paredes de cada ambiente + aberturas (mesma análise da vista)
        if (Json.Bool(p, "walls") != false)
        {
            int probes = 0, win = 0;
            foreach (Room room in rooms)
            {
                int k = 0;
                foreach (var (s, e) in WallLines(room.Face, uf))
                {
                    var w = new Wall { Id = $"W{++k}", Start = s, End = e, LengthM = s.GetDistanceTo(e) / uf, Side = SideOf(room, s, e) };
                    if (probes++ < MaxProbes)
                    {
                        try
                        {
                            Elevation.Probe pr = Elevation.ProbeWall(doc, s, e, room.Inside, m.Unit.Name);
                            w.Start = new Point2d(pr.Start.X, pr.Start.Y); w.End = new Point2d(pr.End.X, pr.End.Y);
                            // o "fechamento" da porta (linha dobradiça→ponta) contorna o ambiente, mas não é parede
                            if (pr.Openings.Count == 1 && pr.Openings[0].Kind == "porta"
                                && pr.Openings[0].ToM - pr.Openings[0].FromM >= 0.85 * w.LengthM) { k--; continue; }
                            w.ThicknessM = pr.ThicknessM;
                            w.Openings = pr.Openings;
                            foreach (var o in pr.Openings)
                                w.OpeningIds.Add(o.Kind == "janela" ? $"J{++win}" : "");
                        }
                        catch (Exception ex) { m.Warnings.Add($"{room.Id}.{w.Id}: não analisei as aberturas ({ex.Message})"); }
                    }
                    room.Walls.Add(w);
                }
            }
            m.Windows = win;
            if (probes > MaxProbes) m.Warnings.Add($"Só as primeiras {MaxProbes} paredes tiveram as aberturas analisadas (região grande).");
        }

        m.Seconds = clock.Elapsed.TotalSeconds;
        m.Version = tracker.Version;
        return m;
    }

    /// <summary>Paredes = trechos retos do contorno agrupados por reta (≥ 40 cm); recortes de batente/nicho rasos são descartados.</summary>
    private static List<(Point2d S, Point2d E)> WallLines(PlanarFaces.Face f, double uf)
    {
        var groups = new List<(Vector2d D, double C, double S0, double S1, double Len, int First)>();
        double angTol = Math.Sin(Math.PI / 180);
        int n = f.Pts.Count;
        for (int i = 0; i < n; i++)
        {
            Point2d a = f.Pts[i], b = f.Pts[(i + 1) % n];
            double len = a.GetDistanceTo(b);
            if (len < 0.01 * uf) continue;
            Vector2d d = (b - a) / len;
            if (d.X < -1e-9 || (Math.Abs(d.X) <= 1e-9 && d.Y < 0)) d = -d;
            var nn = new Vector2d(-d.Y, d.X);
            double c = nn.X * a.X + nn.Y * a.Y, sa = d.X * a.X + d.Y * a.Y, sb = d.X * b.X + d.Y * b.Y;
            int gi = groups.FindIndex(g => Math.Abs(g.D.X * d.Y - g.D.Y * d.X) < angTol && Math.Abs(g.C - c) <= 0.03 * uf);
            if (gi >= 0)
            {
                var g = groups[gi];
                groups[gi] = (g.D, g.C, Math.Min(g.S0, Math.Min(sa, sb)), Math.Max(g.S1, Math.Max(sa, sb)), g.Len + len, g.First);
            }
            else groups.Add((d, c, Math.Min(sa, sb), Math.Max(sa, sb), len, i));
        }
        var keep = groups.Where(g => g.Len >= 0.4 * uf).ToList();
        keep = keep.Where(g => !keep.Any(h => !h.Equals(g) && Math.Abs(h.D.X * g.D.Y - h.D.Y * g.D.X) < angTol
                                               && Math.Abs(h.C - g.C) <= 0.35 * uf && h.Len > g.Len
                                               && Math.Min(h.S1, g.S1) - Math.Max(h.S0, g.S0) >= 0.5 * (g.S1 - g.S0)))
                   .OrderBy(g => g.First).ToList();
        return keep.Select(g =>
        {
            var nn = new Vector2d(-g.D.Y, g.D.X);
            Point2d P(double s) => new(g.D.X * s + nn.X * g.C, g.D.Y * s + nn.Y * g.C);
            return (P(g.S0), P(g.S1));
        }).ToList();
    }

    private static string SideOf(Room r, Point2d s, Point2d e)
    {
        Point2d mid = s + (e - s) / 2;
        Vector2d v = mid - r.Inside;
        return Math.Abs(v.X) > Math.Abs(v.Y) ? (v.X > 0 ? "direita" : "esquerda") : (v.Y > 0 ? "em cima" : "embaixo");
    }

    private static Point2d? InteriorPoint(PlanarFaces.Face f, double uf)
    {
        double cx = f.Pts.Average(q => q.X), cy = f.Pts.Average(q => q.Y);
        var c = new Point2d(cx, cy);
        if (PlanarFaces.Contains(f.Pts, c)) return c;
        for (int i = 0; i < f.Pts.Count; i++)
        {
            Point2d a = f.Pts[i], b = f.Pts[(i + 1) % f.Pts.Count];
            if (a.GetDistanceTo(b) < 0.5 * uf) continue;
            Vector2d d = (b - a).GetNormal();
            Point2d q = a + (b - a) / 2 + new Vector2d(-d.Y, d.X) * 0.3 * uf;   // à esquerda do contorno anti-horário = dentro
            if (PlanarFaces.Contains(f.Pts, q)) return q;
        }
        return null;
    }

    private static readonly Regex NoiseLine = new(@"^\s*((A|ÁREA|AREA)\s*[=:].*|P\s*\.?\s*D\s*\.?.*|[\d.,\s]+(m|cm|mm|m²|m2)?)\s*$", RegexOptions.IgnoreCase);

    private static string CleanName(string t)
    {
        IEnumerable<string> lines = t.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Where(l => !NoiseLine.IsMatch(l));
        return Regex.Replace(string.Join(" ", lines), @"\s+", " ").Trim().ToUpperInvariant();
    }

    private static Extents3d RegionOf(Document doc, JsonElement p)
    {
        if (Json.Has(p, "region")) return Json.Region(p.GetProperty("region"));
        if (Json.Bool(p, "use_selection") == true)
        {
            PromptSelectionResult psr = doc.Editor.SelectImplied();
            if (psr.Status == PromptStatus.OK && psr.Value != null)
            {
                Extents3d? reg = null;
                using Transaction t0 = doc.Database.TransactionManager.StartOpenCloseTransaction();
                foreach (ObjectId id in psr.Value.GetObjectIds())
                    if (t0.GetObject(id, OpenMode.ForRead) is Entity e && GeometryReader.TryExtents(e, Matrix3d.Identity, out Extents3d ex))
                    { if (reg is { } rg) { rg.AddExtents(ex); reg = rg; } else reg = ex; }
                if (reg is { } r) return r;
            }
            throw new InvalidOperationException("Nada selecionado. Peça ao usuário para selecionar a planta (ou o apartamento) ou informe 'region'.");
        }
        throw new InvalidOperationException("Informe 'region' {min, max} cobrindo a planta (um pavimento ou apartamento) ou use_selection=true.");
    }

    // ------------------------------------------------------------------------------------------
    private static string N(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);
    private static string Pt(Point2d q) => $"{GeometryReader.R(q.X).ToString(CultureInfo.InvariantCulture)},{GeometryReader.R(q.Y).ToString(CultureInfo.InvariantCulture)}";

    private static object Report(Map m, bool cached)
    {
        double uf = m.Unit.PerMeter;
        string Who(Room? r) => r == null ? "fora" : $"{r.Id} {r.Name}";
        return new
        {
            action = "analyze_plan",
            units = new { unit = m.Unit.Name, reason = m.Unit.Reason },
            region = new { min = new[] { GeometryReader.R(m.Region.MinPoint.X), GeometryReader.R(m.Region.MinPoint.Y) }, max = new[] { GeometryReader.R(m.Region.MaxPoint.X), GeometryReader.R(m.Region.MaxPoint.Y) } },
            seconds = Math.Round(m.Seconds, 1),
            segments_read = m.Segments,
            legend = "ambiente: id nome | área calculada (área do texto) | largura x altura do retângulo envolvente | ponto interno. " +
                     "parede: id lado comprimento espessura | aberturas (de–até em m a partir da ESQUERDA de quem olha a parede de dentro). " +
                     "porta: id largura | ambientes ligados | para onde abre.",
            rooms = m.Rooms.Select(r =>
                $"{r.Id} {r.Name} | {N(r.AreaM2)} m²" + (r.TextAreaM2 is { } ta ? $" (texto {N(ta)})" : "") +
                $" | {N((r.Face.Box.MaxPoint.X - r.Face.Box.MinPoint.X) / uf)} x {N((r.Face.Box.MaxPoint.Y - r.Face.Box.MinPoint.Y) / uf)} m | {Pt(r.Inside)}"),
            walls = m.Rooms.SelectMany(r => r.Walls.Select(w =>
                $"{r.Id}.{w.Id} {w.Side} {N(w.LengthM)} m" + (w.ThicknessM > 0 ? $" esp {N(w.ThicknessM)}" : "") +
                (w.Openings.Count == 0 ? "" : " | " + string.Join("; ", w.Openings.Select((o, i) => $"{(w.OpeningIds[i].Length > 0 ? w.OpeningIds[i] + " " : "")}{o.Kind} {N(o.ToM - o.FromM)} m ({N(o.FromM)}–{N(o.ToM)})"))))),
            doors = m.Doors.Select(d => $"{d.Id} {N(d.WidthM)} m | {Who(d.A)} ↔ {Who(d.B)} | abre para {(d.SwingInto == null ? "?" : d.SwingInto.Id)}"),
            windows = m.Windows,
            warnings = m.Warnings.Count > 0 ? m.Warnings : null,
            next = "Use os ids/nomes nas outras ferramentas, sem coordenadas: hatch_regions {rooms:['R1','BWC']}, zoom_to {room}, " +
                   "read_geometry {room}, generate_elevation {room, wall:'W2' | 'janela' | 'em cima'}. O mapa fica guardado e é " +
                   "recalculado sozinho se a planta mudar.",
        };
    }

    // ------------------------------------------------------------------------------------------
    // Nomes → coordenadas para as outras ferramentas.

    private static readonly HashSet<string> Expandable = new(StringComparer.Ordinal) { "hatch_regions", "zoom_to", "read_geometry", "generate_elevation" };

    public static bool NeedsExpand(string method, JsonElement p) =>
        Expandable.Contains(method) && p.ValueKind == JsonValueKind.Object && (Json.Has(p, "room") || Json.Has(p, "rooms"));

    public static JsonElement Expand(Document doc, string method, JsonElement p)
    {
        Map m = Current(doc);
        double uf = m.Unit.PerMeter;
        JsonObject o = JsonNode.Parse(p.GetRawText())!.AsObject();
        JsonArray Arr(Point2d q) => new(JsonValue.Create(q.X), JsonValue.Create(q.Y));
        JsonObject Box(Room r, double margin) => new()
        {
            ["min"] = Arr(new Point2d(r.Face.Box.MinPoint.X - margin, r.Face.Box.MinPoint.Y - margin)),
            ["max"] = Arr(new Point2d(r.Face.Box.MaxPoint.X + margin, r.Face.Box.MaxPoint.Y + margin)),
        };

        switch (method)
        {
            case "hatch_regions":
            {
                var names = new List<string>();
                if (o["rooms"] is JsonArray ra) names.AddRange(ra.Select(x => x?.ToString() ?? ""));
                if (o["room"] is JsonNode rn) names.Add(rn.ToString());
                var pts = new JsonArray();
                foreach (string nm in names) pts.Add(Arr(FindRoom(m, nm).Inside));
                o.Remove("rooms"); o.Remove("room");
                o["points"] = pts;
                o["mode"] = "points";
                break;
            }
            case "zoom_to":
            case "read_geometry":
            {
                Room r = FindRoom(m, o["room"]?.ToString() ?? "");
                o.Remove("room");
                o["region"] = Box(r, (method == "read_geometry" ? 0.4 : 0.2) * uf);
                break;
            }
            case "generate_elevation":
            {
                Room r = FindRoom(m, o["room"]?.ToString() ?? "");
                Wall w = FindWall(r, o["wall"]?.ToString());
                o.Remove("room"); o.Remove("wall");
                o["wall_line"] = new JsonObject { ["start"] = Arr(w.Start), ["end"] = Arr(w.End) };
                o["viewer_point"] = Arr(r.Inside);
                o["units"] ??= m.Unit.Name;
                if (o["title"] == null) o["title"] = $"VISTA - {r.Name} ({w.Id})";
                break;
            }
        }
        using JsonDocument jd = JsonDocument.Parse(o.ToJsonString());
        return jd.RootElement.Clone();
    }

    /// <summary>Mapa guardado deste desenho; refeito com os mesmos parâmetros se a planta mudou.</summary>
    private static Map Current(Document doc)
    {
        IntPtr key = doc.Database.UnmanagedObject;
        if (!Maps.TryGetValue(key, out Map? m))
            throw new InvalidOperationException("Ainda não há mapa da planta neste desenho. Chame analyze_plan com a região da planta (ou do apartamento) antes de usar nomes de ambientes.");
        if (Track(doc.Database).Version != m.Version)
        {
            using JsonDocument jd = JsonDocument.Parse(m.Params);
            m = Analyze(doc, jd.RootElement);
            Maps[key] = m;
        }
        return m;
    }

    private static string Norm(string s)
    {
        string d = s.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (char ch in d) if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(ch);
        return Regex.Replace(sb.ToString().ToUpperInvariant(), @"[^A-Z0-9]+", " ").Trim();
    }

    private static Room FindRoom(Map m, string key)
    {
        string k = Norm(key);
        if (k.Length == 0) throw new InvalidOperationException("Informe o ambiente (id como 'R3' ou nome como 'BWC').");
        Room? byId = m.Rooms.FirstOrDefault(r => Norm(r.Id) == k);
        if (byId != null) return byId;
        var exact = m.Rooms.Where(r => Norm(r.Name) == k).ToList();
        var hits = exact.Count > 0 ? exact : m.Rooms.Where(r => Norm(r.Name).Contains(k)).ToList();
        if (hits.Count == 1) return hits[0];
        string all = string.Join(", ", m.Rooms.Select(r => $"{r.Id} {r.Name}"));
        if (hits.Count > 1)
            throw new InvalidOperationException($"'{key}' corresponde a mais de um ambiente: {string.Join(", ", hits.Select(r => $"{r.Id} {r.Name}"))}. Use o id. Nada foi feito.");
        throw new InvalidOperationException($"Ambiente '{key}' não encontrado no mapa. Ambientes: {all}. Nada foi feito.");
    }

    private static Wall FindWall(Room r, string? key)
    {
        string list = string.Join("; ", r.Walls.Select(w => $"{w.Id} {w.Side} {N(w.LengthM)} m" + (w.Openings.Count > 0 ? " (" + string.Join(", ", w.Openings.Select(o => o.Kind)) + ")" : "")));
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException($"Informe 'wall' para {r.Id} {r.Name}: {list}.");
        string k = Norm(key);
        if (Regex.IsMatch(k, @"^(R\d+ )?W?\d+$"))
        {
            string id = "W" + Regex.Match(k, @"(\d+)$").Groups[1].Value;
            return r.Walls.FirstOrDefault(w => w.Id == id) ?? throw new InvalidOperationException($"{r.Id} não tem a parede {id}. Paredes: {list}.");
        }
        var hits = r.Walls.Where(w => Norm(w.Side) == k || w.Openings.Any(o => Norm(o.Kind) == k)).OrderByDescending(w => w.LengthM).ToList();
        if (hits.Count == 1) return hits[0];
        // "em cima" com uma parede de 3,5 m e um recorte de 0,5 m: o usuário quer a parede, não o recorte
        if (hits.Count > 1 && hits[0].LengthM >= 2 * hits[1].LengthM) return hits[0];
        throw new InvalidOperationException(hits.Count == 0
            ? $"Não achei a parede '{key}' em {r.Id} {r.Name}. Paredes: {list}."
            : $"'{key}' corresponde a {hits.Count} paredes de {r.Id}: {list}. Use o id (W1, W2...).");
    }
}
