using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// hatch_regions: hachura áreas FECHADAS achando o contorno pela geometria (PlanarFaces), em vez de a IA
/// montar o polígono ponto a ponto a partir de centenas de linhas soltas (que era o que dava errado).
///
/// Por que não o Editor.TraceBoundary do AutoCAD: testado em planta real, ele não enxerga geometria de
/// blocos aninhados/espelhados (devolve vazio) e só considera o que está na tela. O GeometryReader já
/// entrega tudo transformado (blocos e XREFs), então o contorno é calculado aqui.
///
/// Modos:
///  - points: um ponto DENTRO de cada área (ambiente, piso, peça); móveis soltos dentro viram furos;
///  - walls: os pontos saem sozinhos — no meio de cada par de linhas paralelas com distância de espessura
///    de parede. Cada face achada passa por um teste de "parede": 2·área/perímetro ≈ espessura. Face que
///    VAZOU (parede aberta numa ponta) vira uma área larga e é REJEITADA, com a posição, para o usuário
///    fechar a parede ali.
/// Nada é alterado no dry_run; a execução cria as hachuras num único passo de undo.
/// </summary>
internal static class HatchRegions
{
    private const int MaxSeeds = 2000;
    private const int MaxSegments = 30_000;

    private sealed class Found
    {
        public int Index;
        public Point2d Seed;
        public PlanarFaces.Face Face = null!;
        public List<PlanarFaces.Face> Holes = new();
        public string? Rejected;
        public bool Nested;
    }

    private sealed class Result
    {
        public readonly List<Found> Regions = new();
        public readonly List<object> Failed = new();
        public readonly List<string> Warnings = new();
        public int Seeds, Segments, Faces;
        public double Seconds;
        public Units.Guess? Unit;
        public string Mode = "";
    }

    public static object DryRun(Document doc, JsonElement p) => Report(Analyze(doc, p), null, dry: true);

    public static object Run(Document doc, JsonElement p)
    {
        Result r = Analyze(doc, p);
        var ok = r.Regions.Where(x => x.Rejected == null).ToList();
        if (ok.Count == 0)
            throw new InvalidOperationException("Nenhuma área fechada válida para hachurar. Rode com dry_run para ver o que falhou. Nada foi alterado.");
        string layer = DrawEntities.ValidLayer(Json.Str(p, "layer") ?? "IA-HACHURA", "layer");
        string pattern = (Json.Str(p, "pattern") ?? "SOLID").Trim().ToUpperInvariant();
        double scale = Json.Num(p, "scale") ?? 1;
        Color? color = Json.Has(p, "color") ? DrawingEdits.ParseColor(p.GetProperty("color")) : null;

        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartTransaction();
        var c = new Canvas(db, tr);
        c.Layer(layer, 8);
        foreach (Found f in ok)
            c.Hatch(f.Face.Pts, pattern, scale, layer, color, f.Holes.Select(h => (IReadOnlyList<Point2d>)h.Pts));
        tr.Commit();
        DrawingEdits.Notify(doc, $"hachurou {ok.Count} área(s) na layer {layer}");
        return Report(r, c, dry: false);
    }

    // ------------------------------------------------------------------------------------------
    private static Result Analyze(Document doc, JsonElement p)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Database db = doc.Database;
        var r = new Result();
        string mode = (Json.Str(p, "mode") ?? (Json.Has(p, "points") ? "points" : "walls")).Trim().ToLowerInvariant();
        if (mode is not ("points" or "walls")) throw new InvalidOperationException("'mode' deve ser 'points' ou 'walls'.");
        r.Mode = mode;
        string pattern = (Json.Str(p, "pattern") ?? "SOLID").Trim().ToUpperInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(pattern, @"^[A-Z0-9_\-]+$")) throw new InvalidOperationException($"'pattern' inválido ('{pattern}').");
        if (Json.Has(p, "color")) DrawingEdits.ParseColor(p.GetProperty("color"));   // valida antes
        if (Json.Has(p, "layer")) DrawEntities.ValidLayer(Json.Str(p, "layer") ?? "", "layer");

        // região de trabalho
        Extents3d? region = null;
        if (Json.Has(p, "region")) region = Json.Region(p.GetProperty("region"));
        else if (Json.Bool(p, "use_selection") == true)
        {
            PromptSelectionResult psr = doc.Editor.SelectImplied();
            if (psr.Status != PromptStatus.OK || psr.Value == null)
                throw new InvalidOperationException("Nada selecionado. Peça ao usuário para selecionar a parte do desenho (ex.: as paredes de um apartamento) ou use 'region'.");
            using Transaction t0 = db.TransactionManager.StartOpenCloseTransaction();
            foreach (ObjectId id in psr.Value.GetObjectIds())
                if (t0.GetObject(id, OpenMode.ForRead) is Entity e && GeometryReader.TryExtents(e, Matrix3d.Identity, out Extents3d ex))
                { if (region is { } rg) { rg.AddExtents(ex); region = rg; } else region = ex; }
        }

        var userSeeds = new List<Point2d>();
        if (mode == "points")
        {
            if (!Json.Has(p, "points") || p.GetProperty("points").ValueKind != JsonValueKind.Array)
                throw new InvalidOperationException("Informe 'points': um ponto [x, y] DENTRO de cada área a hachurar.");
            foreach (JsonElement e in p.GetProperty("points").EnumerateArray()) { Point3d q = Json.Point(e, "points[]"); userSeeds.Add(new Point2d(q.X, q.Y)); }
            if (userSeeds.Count == 0) throw new InvalidOperationException("'points' está vazia.");
        }
        else if (region == null)
            throw new InvalidOperationException("No modo 'walls' informe 'region' {min, max} ou use_selection=true (o usuário seleciona a parte da planta). Desenhos inteiros têm paredes demais e objetos que não são parede.");

        // geometria (entra em blocos/XREFs)
        var (segs, straight, radii, tags, sources) = Collect(db, p, region);
        r.Segments = segs.Count;
        if (segs.Count == 0) throw new InvalidOperationException("Não há linhas nessa região (confira 'region' e 'layers').");
        if (segs.Count > MaxSegments) throw new InvalidOperationException($"Linhas demais na região ({segs.Count}; máx. {MaxSegments}). Use uma região menor ou filtre por 'layers'.");

        r.Unit = Units.FromName(Json.Str(p, "units")) ?? Units.GuessFromArcs(radii);
        double uf = r.Unit?.PerMeter ?? 0;
        if (mode == "walls" && r.Unit == null)
            throw new InvalidOperationException("Não consegui deduzir a unidade (sem arcos de porta na região). Informe units: 'm', 'cm' ou 'mm'.");

        // tolerância de fusão de vértices: 1 mm (ou um milésimo do tamanho, sem unidade)
        double size = Math.Max(segs.Max(s => Math.Max(s.A.X, s.B.X)) - segs.Min(s => Math.Min(s.A.X, s.B.X)),
                               segs.Max(s => Math.Max(s.A.Y, s.B.Y)) - segs.Min(s => Math.Min(s.A.Y, s.B.Y)));
        double tol = uf > 0 ? 0.001 * uf : Math.Max(size * 1e-6, 1e-6);
        var faces = new PlanarFaces(segs, tol, tags);
        r.Faces = faces.Bounded.Count;

        List<Point2d> seeds;
        double maxT = 0;
        if (mode == "walls")
        {
            double minT = (Json.Num(p, "min_thickness") ?? 0.07) * uf;
            maxT = (Json.Num(p, "max_thickness") ?? 0.40) * uf;
            if (minT <= 0 || maxT <= minT) throw new InvalidOperationException("min_thickness/max_thickness inválidos (em metros, min < max).");
            seeds = WallSeeds(straight, minT, maxT, uf);
            if (seeds.Count == 0)
                throw new InvalidOperationException($"Não achei pares de linhas paralelas com espessura de parede ({minT / uf * 100:0}–{maxT / uf * 100:0} cm) nessa região. Confira a região, a unidade (units) ou min/max_thickness.");
        }
        else seeds = userSeeds;
        if (seeds.Count > MaxSeeds) throw new InvalidOperationException($"Pontos demais ({seeds.Count}; máx. {MaxSeeds}). Divida a região.");
        r.Seeds = seeds.Count;

        var seen = new HashSet<PlanarFaces.Face>();
        foreach (Point2d s in seeds)
        {
            PlanarFaces.Face? f = faces.FaceAt(s);
            if (f == null)
            {
                if (mode == "points") r.Failed.Add(new { seed = new[] { GeometryReader.R(s.X), GeometryReader.R(s.Y) }, reason = "área não fechada (ou o ponto está fora de qualquer área): há uma abertura no contorno" });
                continue;
            }
            if (!seen.Add(f)) continue;   // várias sementes na mesma face
            var found = new Found { Index = r.Regions.Count + 1, Seed = s, Face = f };
            if (mode == "walls")
            {
                double ratio = 2 * f.Area / f.Perimeter;   // ≈ espessura para uma massa de parede
                if (ratio > maxT * 1.5)
                    found.Rejected = $"contorno largo demais para parede (≈{ratio / uf * 100:0} cm de largura média, área {f.Area / (uf * uf):0.##} m²): a parede deve estar ABERTA perto deste ponto e o contorno vazou para o ambiente";
            }
            else found.Holes = faces.IslandsOf(f);
            r.Regions.Add(found);
        }

        // Esquadrias e pilares costumam ser BLOCOS dentro do bloco/desenho que tem as paredes: as faixas entre as
        // linhas de uma janela têm "espessura de parede" e passariam no teste acima. Face cujo contorno vem de
        // uma origem ANINHADA dentro de outra origem que tem paredes = provável esquadria/pilar → rejeitada.
        if (mode == "walls" && !(Json.Bool(p, "include_nested") ?? false))
        {
            // Área de "parede" por origem. Um bloco aninhado é esquadria/pilar só se contribui MENOS que o bloco de
            // paredes logo acima dele (janelas somam faixas pequenas; um apartamento dentro do pavimento, dezenas de m²).
            var areaByTag = r.Regions.Where(x => x.Rejected == null && x.Face.DominantTag >= 0)
                                     .GroupBy(x => x.Face.DominantTag).ToDictionary(g => g.Key, g => g.Sum(x => x.Face.Area));
            bool IsAncestor(int w, int t) => w != t && sources[t].Length > sources[w].Length && sources[t].StartsWith(sources[w], StringComparison.Ordinal);
            bool Nested(int t)
            {
                if (t < 0) return false;
                int parent = areaByTag.Keys.Where(w => IsAncestor(w, t)).OrderByDescending(w => sources[w].Length).DefaultIfEmpty(-1).First();
                return parent >= 0 && areaByTag.GetValueOrDefault(t) < areaByTag[parent];
            }
            foreach (Found x in r.Regions.Where(x => x.Rejected == null))
            {
                int t = x.Face.DominantTag;
                if (Nested(t))
                {
                    string name = sources[t].TrimEnd('/');
                    name = name[(name.LastIndexOf('/') + 1)..];
                    x.Rejected = $"contorno de um bloco dentro do bloco das paredes ({name}): provável esquadria ou pilar. Use include_nested=true para hachurar também";
                    x.Nested = true;
                }
            }
        }
        r.Seconds = clock.Elapsed.TotalSeconds;
        return r;
    }

    /// <summary>Segmentos 2D da região (arcos e círculos viram cordas). 'straight' = só os retos (para achar paredes).</summary>
    private static (List<(Point2d A, Point2d B)> All, List<(Point2d A, Point2d B)> Straight, List<double> Radii, List<int> Tags, List<string> Sources) Collect(Database db, JsonElement p, Extents3d? region)
    {
        var all = new List<(Point2d, Point2d)>();
        var tags = new List<int>();
        var sources = new List<string>();
        int curTag = 0;
        void AddSeg((Point2d, Point2d) s) { all.Add(s); tags.Add(curTag); }
        var straight = new List<(Point2d, Point2d)>();
        var radii = new List<double>();
        static Point2d P(Point3d q) => new(q.X, q.Y);
        void Chords(Point3d c, double r, double a0, double a1)
        {
            if (a1 <= a0) a1 += 2 * Math.PI;
            int n = Math.Clamp((int)Math.Ceiling((a1 - a0) / (Math.PI / 12)), 2, 48);
            Point2d prev = new(c.X + r * Math.Cos(a0), c.Y + r * Math.Sin(a0));
            for (int i = 1; i <= n; i++)
            {
                double a = a0 + (a1 - a0) * i / n;
                var q = new Point2d(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
                AddSeg((prev, q)); prev = q;
            }
        }
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var opt = new GeometryReader.Options { Region = region, Types = new HashSet<string> { "LINE", "LWPOLYLINE", "ARC", "CIRCLE" } };
        if (Json.StringArray(p, "layers") is { Length: > 0 } pats)
        {
            var rx = pats.Select(GeometryReader.WildcardToRegex).ToArray();
            opt.LayerFilter = l => rx.Any(x => x.IsMatch(l) || x.IsMatch(l.Contains('|') ? l[(l.IndexOf('|') + 1)..] : l));
        }
        GeometryReader.Walk(db, tr, opt, it =>
        {
            curTag = sources.IndexOf(it.Source);
            if (curTag < 0) { sources.Add(it.Source); curTag = sources.Count - 1; }
            return Visit(it);
        });
        bool Visit(GeometryReader.Item it)
        {
            switch (it.Entity)
            {
                case Line l:
                    if (l.Length > 0) { AddSeg((P(l.StartPoint), P(l.EndPoint))); straight.Add((P(l.StartPoint), P(l.EndPoint))); }
                    break;
                case Arc a:
                {
                    radii.Add(a.Radius);
                    // pelas pontas, não por StartAngle/EndAngle: num bloco espelhado a normal vira -Z e os ângulos invertem
                    double s0 = Math.Atan2(a.StartPoint.Y - a.Center.Y, a.StartPoint.X - a.Center.X);
                    double s1 = Math.Atan2(a.EndPoint.Y - a.Center.Y, a.EndPoint.X - a.Center.X);
                    if (a.Normal.Z >= 0) Chords(a.Center, a.Radius, s0, s1); else Chords(a.Center, a.Radius, s1, s0);
                    break;
                }
                case Circle c:
                    Chords(c.Center, c.Radius, 0, 2 * Math.PI);
                    break;
                case Polyline pl:
                    int n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                    for (int i = 0; i < n; i++)
                    {
                        try
                        {
                            if (pl.GetSegmentType(i) == SegmentType.Line)
                            {
                                var ls = pl.GetLineSegmentAt(i);
                                AddSeg((P(ls.StartPoint), P(ls.EndPoint))); straight.Add((P(ls.StartPoint), P(ls.EndPoint)));
                            }
                            else if (pl.GetSegmentType(i) == SegmentType.Arc)
                            {
                                CircularArc3d ca = pl.GetArcSegmentAt(i);
                                // ângulos no plano XY a partir das pontas (o arco pode estar invertido pela transformação)
                                double s0 = Math.Atan2(ca.StartPoint.Y - ca.Center.Y, ca.StartPoint.X - ca.Center.X);
                                double s1 = Math.Atan2(ca.EndPoint.Y - ca.Center.Y, ca.EndPoint.X - ca.Center.X);
                                bool ccw = ca.Normal.Z >= 0;
                                if (ccw) Chords(ca.Center, ca.Radius, s0, s1); else Chords(ca.Center, ca.Radius, s1, s0);
                            }
                        }
                        catch { /* segmento degenerado */ }
                    }
                    break;
            }
            return all.Count <= MaxSegments + 1;
        }
        return (all, straight, radii, tags, sources);
    }

    /// <summary>Sementes no meio de pares de linhas paralelas com distância de espessura de parede.</summary>
    private static List<Point2d> WallSeeds(List<(Point2d A, Point2d B)> segs, double minT, double maxT, double uf)
    {
        var clean = segs.Where(s => s.A.GetDistanceTo(s.B) >= 0.10 * uf).ToList();
        var seeds = new List<Point2d>();
        double angTol = Math.Sin(1.0 * Math.PI / 180);
        for (int i = 0; i < clean.Count; i++)
        {
            Vector2d u = (clean[i].B - clean[i].A).GetNormal();
            double li = clean[i].A.GetDistanceTo(clean[i].B);
            var nrm = new Vector2d(-u.Y, u.X);
            for (int j = i + 1; j < clean.Count; j++)
            {
                Vector2d v = (clean[j].B - clean[j].A).GetNormal();
                if (Math.Abs(u.X * v.Y - u.Y * v.X) > angTol) continue;                      // não paralelas
                double d = (clean[j].A - clean[i].A).DotProduct(nrm);
                if (Math.Abs(d) < minT || Math.Abs(d) > maxT) continue;
                double s1 = (clean[j].A - clean[i].A).DotProduct(u), s2 = (clean[j].B - clean[i].A).DotProduct(u);
                double lo = Math.Max(0, Math.Min(s1, s2)), hi = Math.Min(li, Math.Max(s1, s2));
                if (hi - lo < Math.Max(Math.Abs(d), 0.10 * uf)) continue;                  // sobreposição curta demais
                seeds.Add(clean[i].A + u * ((lo + hi) / 2) + nrm * (d / 2));
            }
        }
        var uniq = new List<Point2d>();
        foreach (Point2d s in seeds) if (!uniq.Any(q => q.GetDistanceTo(s) < 0.05 * uf)) uniq.Add(s);
        return uniq;
    }

    private static object Report(Result r, Canvas? drawn, bool dry)
    {
        double a2 = r.Unit is { } u ? u.PerMeter * u.PerMeter : 0;
        var ok = r.Regions.Where(x => x.Rejected == null).ToList();
        var bad = r.Regions.Where(x => x.Rejected != null).ToList();
        object Box(PlanarFaces.Face f) => new { min = new[] { GeometryReader.R(f.Box.MinPoint.X), GeometryReader.R(f.Box.MinPoint.Y) }, max = new[] { GeometryReader.R(f.Box.MaxPoint.X), GeometryReader.R(f.Box.MaxPoint.Y) } };
        return new
        {
            action = dry ? "hatch_regions (dry_run: nada foi desenhado)" : "hatch_regions",
            mode = r.Mode,
            units = r.Unit is { } uu ? new { unit = uu.Name, reason = uu.Reason } : null,
            segments_read = r.Segments,
            closed_faces = r.Faces,
            seeds = r.Seeds,
            seconds = Math.Round(r.Seconds, 1),
            regions_ok = ok.Count,
            total_area_m2 = a2 > 0 ? Math.Round(ok.Sum(f => f.Face.Area) / a2, 3) : (double?)null,
            // lista curta: o importante é a contagem, a área e o que foi REJEITADO
            regions_sample = ok.Take(r.Mode == "points" ? 50 : 10).Select(f => new
            {
                index = f.Index,
                area = Math.Round(f.Face.Area, 4),
                area_m2 = a2 > 0 ? Math.Round(f.Face.Area / a2, 3) : (double?)null,
                holes = f.Holes.Count > 0 ? f.Holes.Count : (int?)null,
                box = Box(f.Face),
            }),
            regions_extent = ok.Count > 0 ? new
            {
                min = new[] { GeometryReader.R(ok.Min(f => f.Face.Box.MinPoint.X)), GeometryReader.R(ok.Min(f => f.Face.Box.MinPoint.Y)) },
                max = new[] { GeometryReader.R(ok.Max(f => f.Face.Box.MaxPoint.X)), GeometryReader.R(ok.Max(f => f.Face.Box.MaxPoint.Y)) },
            } : null,
            rejected_nested_blocks = bad.Any(f => f.Nested)
                ? bad.Where(f => f.Nested).GroupBy(f => f.Rejected!).Select(g => new { count = g.Count(), reason = g.Key })
                : null,
            rejected_open = bad.Any(f => !f.Nested)
                ? bad.Where(f => !f.Nested).Take(20).Select(f => new { near = new[] { GeometryReader.R(f.Seed.X), GeometryReader.R(f.Seed.Y) }, reason = f.Rejected, box = Box(f.Face) })
                : null,
            failed = r.Failed.Count > 0 ? r.Failed : null,
            warnings = r.Warnings.Count > 0 ? r.Warnings : null,
            drawn = drawn == null ? null : new { by_type = drawn.Counts, layers_created = drawn.CreatedLayers, bounds = drawn.Bounds },
            next = dry
                ? "Diga ao usuário quantas áreas serão hachuradas e mostre as REJEITADAS (paredes abertas: ele pode fechar com uma linha e rodar de novo). Com o ok, chame sem dry_run (mesmos parâmetros + pattern/color/layer)."
                : "Para desfazer: digite U no AutoCAD (desfaz todas as hachuras de uma vez). Confira com zoom_to + capture_view.",
        };
    }
}
