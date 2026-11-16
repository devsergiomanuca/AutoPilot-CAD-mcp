using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// generate_elevation: vista (elevação) de UMA parede a partir da planta.
///
/// Entrada: uma linha sobre a FACE da parede, desenhada da ESQUERDA para a DIREITA de quem olha a parede de
/// dentro do ambiente (o ambiente fica à direita do sentido da linha). Pode vir como coordenadas
/// (wall_line) ou como a LINHA que o usuário deixou selecionada.
///
/// Como a parede é entendida (pela GEOMETRIA, porque nomes de layers/blocos não são confiáveis):
///   1. Tudo perto da parede é lido com GeometryReader (entra em XREFs e blocos) e levado para o sistema da
///      parede: s = distância ao longo da parede (0..L), t = distância para dentro da parede (t &lt; 0 = ambiente).
///   2. Trechos de linha paralelos à face e sobre ela (t ≈ 0) = parede cheia. O que sobra entre eles = vão.
///      Se a linha do usuário estiver um pouco fora da face (sem OSNAP), adota a face paralela mais próxima.
///   3. Cada vão é classificado: arco de abertura com raio ≈ largura do vão = PORTA; linhas paralelas dentro da
///      espessura da parede (ou X) = JANELA; nada = VÃO livre.
///   4. Pé-direito: parâmetro height ou o texto "P.D.= 2.60 m" do ambiente mais próximo (lado do ambiente).
///   5. Unidade: parâmetro units ou deduzida (raio de porta 0,6–1,2 m; comprimento plausível).
/// Saída: contorno da parede, aberturas, cotas (corrente horizontal, total, pé-direito, alturas das janelas)
/// e título, ao lado da planta e em layers IA-VISTA*. dry_run=true só analisa (nada é desenhado nem gravado
/// no undo), para a IA confirmar com o usuário antes.
/// </summary>
internal static class Elevation
{
    private sealed record Seg(double S1, double T1, double S2, double T2, string Source);
    /// <summary>Arco no sistema da parede: centro (Cs, Ct), raio e as duas pontas (para achar a folha fechada da porta).</summary>
    private sealed record ArcInfo(double Cs, double Ct, double Radius, double S1, double T1, double S2, double T2, string Source);
    private sealed record TextInfo(double S, double T, string Text);

    private sealed class Opening
    {
        public int Index;
        public double From, To;        // em unidades do desenho, ao longo da parede
        public string Kind = "vão";    // porta | janela | vão
        public string Evidence = "";
        public double Bottom, Top;     // em unidades do desenho (z)
        public string? Label;
    }

    private sealed class Analysis
    {
        public Point3d Start, End;
        public double L;
        public Vector3d U, W;          // U = ao longo da parede; W = para dentro da parede (oposto ao ambiente)
        public Units.Guess Unit = null!;
        public double H;
        public string HeightSource = "";
        public double Thickness;
        public string ThicknessSource = "";
        public double FaceOffset;      // ajuste aplicado na linha (unidades), 0 = estava na face
        public double Coverage;        // fração da parede com face encontrada
        public readonly List<(double A, double B)> Solid = new();
        public readonly List<Opening> Openings = new();
        public readonly List<string> Warnings = new();
        public int Elements;
        public string WallLineSource = "";
    }

    // ------------------------------------------------------------------------------------------
    public static object DryRun(Document doc, JsonElement p)
    {
        Analysis a = Analyze(doc, p);
        return Report(a, drawn: null, origin: null);
    }

    public static object Generate(Document doc, JsonElement p)
    {
        Analysis a = Analyze(doc, p);
        if (a.Coverage < 0.25)
            throw new InvalidOperationException(
                $"Só {a.Coverage:P0} da linha coincide com a face de uma parede: provavelmente a linha não está sobre a parede. " +
                "Nada foi desenhado. Peça ao usuário para redesenhar a linha sobre a face (com OSNAP nos cantos) ou rode com dry_run para inspecionar.");

        Database db = doc.Database;
        string prefix = DrawEntities.ValidLayer(Json.Str(p, "layer_prefix") ?? "IA-VISTA", "layer_prefix");
        string title = Json.Str(p, "title") ?? "VISTA";
        Point3d origin = Json.Has(p, "origin") ? Json.Point(p, "origin", "origin") : AutoOrigin(db, a);

        using Transaction tr = db.TransactionManager.StartTransaction();
        var c = new Canvas(db, tr);
        string lWall = prefix, lOpen = prefix + "-ESQUADRIAS", lDim = prefix + "-COTAS", lTxt = prefix + "-TEXTOS";
        c.Layer(lWall, 7); c.Layer(lOpen, 4); c.Layer(lDim, 1); c.Layer(lTxt, 2);

        double uf = a.Unit.PerMeter;
        double txt = TextHeight(db, a.H);
        Point3d W(double x, double y) => new(origin.X + x, origin.Y + y, 0);
        Point2d W2(double x, double y) => new(origin.X + x, origin.Y + y);

        // contorno da parede + linha de piso
        c.Rect(W2(0, 0), W2(a.L, a.H), lWall);
        c.Line(W(-0.3 * uf, 0), W(a.L + 0.3 * uf, 0), lWall);

        // aberturas
        foreach (Opening o in a.Openings)
        {
            c.Rect(W2(o.From, o.Bottom), W2(o.To, o.Top), lOpen);
            if (o.Kind == "janela")
            {
                double f = Math.Min(0.04 * uf, (o.To - o.From) / 6);
                c.Rect(W2(o.From + f, o.Bottom + f), W2(o.To - f, o.Top - f), lOpen);
            }
            c.Text(W((o.From + o.To) / 2, o.Top + txt * 0.6), o.Label ?? Short(o), txt * 0.8, 0, lTxt, "center");
        }

        // cotas: corrente horizontal, total, pé-direito e alturas das aberturas
        double off = txt * 4;
        var xs = new SortedSet<double> { 0, a.L };
        foreach (Opening o in a.Openings) { xs.Add(o.From); xs.Add(o.To); }
        double[] xa = xs.ToArray();
        for (int i = 0; i + 1 < xa.Length; i++)
            if (xa[i + 1] - xa[i] > 1e-6) c.Dim(W(xa[i], 0), W(xa[i + 1], 0), W(xa[i], -off), 0, lDim);
        if (xa.Length > 2) c.Dim(W(0, 0), W(a.L, 0), W(0, -2 * off), 0, lDim);
        c.Dim(W(0, 0), W(0, a.H), W(-off, 0), Math.PI / 2, lDim);
        foreach (Opening o in a.Openings)
        {
            double x = o.To + off * 0.6;
            if (o.Bottom > 1e-6) c.Dim(W(o.To, 0), W(o.To, o.Bottom), W(x, 0), Math.PI / 2, lDim);
            c.Dim(W(o.To, o.Bottom), W(o.To, o.Top), W(x, o.Bottom), Math.PI / 2, lDim);
        }

        // título
        c.MText(W(a.L / 2, -2 * off - txt * 1.5), title, txt * 1.6, lTxt, AttachmentPoint.TopCenter);

        tr.Commit();
        DrawingEdits.Notify(doc, $"desenhou a vista '{title}' ({a.Openings.Count} abertura(s)) em ({origin.X:0.##}, {origin.Y:0.##})");
        return Report(a, c, origin);
    }

    // ------------------------------------------------------------------------------------------
    private static Analysis Analyze(Document doc, JsonElement p)
    {
        Database db = doc.Database;
        var a = new Analysis();
        (a.Start, a.End, a.WallLineSource) = WallLine(doc, p);
        if (Json.Bool(p, "flip") == true) (a.Start, a.End) = (a.End, a.Start);
        Vector3d d = a.End - a.Start;
        a.L = d.Length;
        if (a.L < 1e-6) throw new InvalidOperationException("A linha da parede tem comprimento zero.");
        a.U = d / a.L;
        a.W = a.U.RotateBy(Math.PI / 2, Vector3d.ZAxis);   // à esquerda do sentido = para dentro da parede

        if (Json.Has(p, "viewer_point"))
        {
            // Se o ponto do observador ficou do lado errado, inverte a linha.
            Point3d v = Json.Point(p, "viewer_point", "viewer_point");
            if ((v - a.Start).DotProduct(a.W) > 0)
            {
                (a.Start, a.End) = (a.End, a.Start);
                a.U = -a.U; a.W = -a.W;
                a.Warnings.Add("A linha foi invertida para que o observador (viewer_point) fique do lado do ambiente.");
            }
        }

        // 1) leitura da geometria numa faixa proporcional ao comprimento (ainda não sabemos a unidade)
        double m = Math.Max(a.L * 0.6, 1e-3);
        var segs = new List<Seg>();
        var arcs = new List<ArcInfo>();
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            var opt = new GeometryReader.Options
            {
                Region = Box(a, -0.15 * a.L, 1.15 * a.L, -m, m),
                Types = new HashSet<string> { "LINE", "LWPOLYLINE", "ARC" },
            };
            GeometryReader.Walk(db, tr, opt, it =>
            {
                a.Elements++;
                Collect(a, it, segs, arcs);
                return true;
            });
        }
        // a própria linha-guia do usuário não é parede
        segs.RemoveAll(s => Math.Abs(s.T1) < 1e-9 && Math.Abs(s.T2) < 1e-9 && Math.Abs(Math.Min(s.S1, s.S2)) < 1e-9 && Math.Abs(Math.Max(s.S1, s.S2) - a.L) < 1e-9);

        // 2) unidade
        a.Unit = Units.FromName(Json.Str(p, "units"))
                 ?? Units.GuessFromArcs(arcs.Where(x => x.Cs > -0.1 * a.L && x.Cs < 1.1 * a.L && Math.Abs(x.Ct) < 0.3 * a.L).Select(x => x.Radius).ToList())
                 ?? Units.GuessFromLength(a.L)
                 ?? throw new InvalidOperationException(
                     $"Não consegui deduzir a unidade do desenho (parede com {a.L:0.###} unidades). Informe units: 'm', 'cm' ou 'mm'.");
        double uf = a.Unit.PerMeter;
        double tol = 0.02 * uf;

        // 3) face da parede. Só ajusta se a linha do usuário NÃO coincide com uma face (cobertura < 50%):
        //    aí adota a paralela MAIS PRÓXIMA (até 30 cm) que cubra pelo menos metade da parede. Sem essa
        //    regra, a face de trás (também paralela, às vezes mais longa) poderia "roubar" o lugar da certa.
        double Cover(double t0) => Merge(segs.Where(s => Math.Abs(s.T1 - t0) <= tol && Math.Abs(s.T2 - t0) <= tol)
                                              .Select(s => (Math.Max(0, Math.Min(s.S1, s.S2)), Math.Min(a.L, Math.Max(s.S1, s.S2))))
                                              .Where(x => x.Item2 > x.Item1), tol / 2).Sum(x => x.B - x.A);
        double cov0 = Cover(0);
        if (cov0 < 0.5 * a.L)
        {
            var near = segs.Where(s => Math.Abs(s.T1 - s.T2) <= tol && Math.Abs(s.T1) > tol && Math.Abs(s.T1) <= 0.30 * uf)
                           .Select(s => Math.Round(s.T1 / (tol / 2)) * (tol / 2)).Distinct()
                           .Select(t0 => (T0: t0, Cv: Cover(t0)))
                           .Where(x => x.Cv >= 0.5 * a.L && x.Cv > cov0 + tol)
                           .OrderBy(x => Math.Abs(x.T0)).ToList();
            if (near.Count > 0)
            {
                double best = near[0].T0;
                a.FaceOffset = best;
                a.Warnings.Add($"A linha estava a {Math.Abs(best) / uf * 100:0.#} cm da face da parede; usei a face encontrada.");
                for (int i = 0; i < segs.Count; i++) segs[i] = segs[i] with { T1 = segs[i].T1 - best, T2 = segs[i].T2 - best };
                arcs = arcs.Select(x => x with { Ct = x.Ct - best, T1 = x.T1 - best, T2 = x.T2 - best }).ToList();
            }
        }
        a.Solid.AddRange(Merge(segs.Where(s => Math.Abs(s.T1) <= tol && Math.Abs(s.T2) <= tol)
                                  .Select(s => (Math.Max(0, Math.Min(s.S1, s.S2)), Math.Min(a.L, Math.Max(s.S1, s.S2))))
                                  .Where(x => x.Item2 > x.Item1), 0.01 * uf));
        a.Coverage = a.Solid.Sum(x => x.B - x.A) / a.L;
        if (a.Coverage < 0.5)
            a.Warnings.Add($"Só {a.Coverage:P0} da linha tem face de parede. Confira se a linha está sobre a face (use OSNAP nos cantos da parede).");

        // 4) espessura: face paralela mais comum entre 5 e 60 cm para dentro da parede
        var backs = segs.Where(s => Math.Abs(s.T1 - s.T2) <= tol && s.T1 > 0.05 * uf && s.T1 <= 0.60 * uf)
                        .GroupBy(s => Math.Round(s.T1 / tol))
                        .Select(g => (T: g.Average(s => s.T1), Len: g.Sum(s => Math.Abs(s.S2 - s.S1))))
                        .OrderByDescending(x => x.Len).ToList();
        if (backs.Count > 0) { a.Thickness = backs[0].T; a.ThicknessSource = "face oposta encontrada"; }
        else { a.Thickness = 0.15 * uf; a.ThicknessSource = "padrão 15 cm (face oposta não encontrada)"; }

        // 5) vãos. Três fontes de evidência, unidas no fim:
        //    a) trechos SEM face de parede;
        //    b) PORTA: arco de raio típico (0,55–1,3 m) com centro na parede e uma ponta ao longo da parede
        //       (a folha fechada) — pega a porta mesmo se houver linha de soleira na face;
        //    c) JANELA: linha(s) paralela(s) no MEIO da espessura — pega a janela desenhada com linhas sobre
        //       as duas faces (que esconderiam o buraco na face).
        double doorH = (Json.Num(p, "door_height") ?? 2.10) * uf;
        double sill = (Json.Num(p, "window_sill") ?? 1.10) * uf;
        double head = (Json.Num(p, "window_head") ?? 2.10) * uf;
        double minGap = 0.25 * uf;
        double band = a.Thickness + tol;
        var cands = new List<(double A, double B, string? Kind, string Ev)>();

        double cur = 0;
        foreach ((double A, double B) s in a.Solid.Append((a.L, a.L)))
        {
            if (s.A - cur >= minGap) cands.Add((cur, s.A, null, "trecho sem face de parede"));
            cur = Math.Max(cur, s.B);
        }
        foreach (ArcInfo x in arcs.Where(x => x.Radius >= 0.55 * uf && x.Radius <= 1.3 * uf
                                           && x.Ct >= -0.1 * uf && x.Ct <= band + 0.1 * uf
                                           && x.Cs >= -0.05 * a.L && x.Cs <= 1.05 * a.L))
        {
            foreach ((double es, double et) in new[] { (x.S1, x.T1), (x.S2, x.T2) })
            {
                if (Math.Abs(et - x.Ct) > 0.1 * uf || Math.Abs(Math.Abs(es - x.Cs) - x.Radius) > 0.1 * uf) continue;
                double da = Math.Max(0, Math.Min(x.Cs, es)), db2 = Math.Min(a.L, Math.Max(x.Cs, es));
                if (db2 - da >= 0.4 * uf)
                    cands.Add((da, db2, "porta", $"arco de abertura r={x.Radius / uf:0.00} m" + (x.Source.Length > 0 ? $" ({x.Source.TrimEnd('/')})" : "")));
            }
        }
        foreach ((double A, double B) mspan in Merge(segs.Where(s => Math.Abs(s.T1 - s.T2) <= tol && s.T1 > 2 * tol && s.T1 < a.Thickness - 2 * tol)
                                                         .Select(s => (Math.Max(0, Math.Min(s.S1, s.S2)), Math.Min(a.L, Math.Max(s.S1, s.S2))))
                                                         .Where(x => x.Item2 - x.Item1 >= 0.3 * uf), tol))
            cands.Add((mspan.A, mspan.B, "janela", "linha(s) no meio da espessura da parede"));

        // união das evidências que se sobrepõem; prioridade porta > janela > (classificar)
        var merged = new List<(double A, double B, string? Kind, List<string> Ev)>();
        foreach (var cd in cands.OrderBy(x => x.A))
        {
            if (merged.Count > 0 && cd.A <= merged[^1].B + tol)
            {
                var last = merged[^1];
                string? k = last.Kind == "porta" || cd.Kind == "porta" ? "porta" : last.Kind ?? cd.Kind;
                last.Ev.Add(cd.Ev);
                merged[^1] = (last.A, Math.Max(last.B, cd.B), k, last.Ev);
            }
            else merged.Add((cd.A, cd.B, cd.Kind, new List<string> { cd.Ev }));
        }

        int idx = 0;
        foreach (var mg in merged.Where(x => x.B - x.A >= minGap))
        {
            var o = new Opening { Index = ++idx, From = mg.A, To = mg.B, Kind = mg.Kind ?? "" };
            double w = mg.B - mg.A;
            if (o.Kind == "")
            {
                var inside = segs.Where(s => Math.Min(s.T1, s.T2) >= -tol && Math.Max(s.T1, s.T2) <= band + tol
                                          && Overlap(Math.Min(s.S1, s.S2), Math.Max(s.S1, s.S2), mg.A, mg.B) >= 0.5 * w).ToList();
                int parallel = inside.Count(s => Math.Abs(s.T1 - s.T2) <= tol && Math.Abs(s.T1) > tol && Math.Abs(s.T1 - a.Thickness) > tol);
                int diagonal = inside.Count(s => Math.Abs(s.T1 - s.T2) > tol && Math.Abs(s.S1 - s.S2) > 0.5 * w);
                if (parallel > 0 || diagonal >= 2)
                {
                    o.Kind = "janela";
                    mg.Ev.Add(parallel > 0 ? $"{parallel} linha(s) paralela(s) dentro da espessura" : "linhas em X no vão");
                }
                else
                {
                    o.Kind = "vão";
                    mg.Ev.Add("sem arco de porta nem linhas de janela");
                }
            }
            (o.Bottom, o.Top) = o.Kind == "janela" ? (sill, head) : (0, doorH);
            o.Evidence = string.Join("; ", mg.Ev.Distinct());
            if (mg.A < tol || mg.B > a.L - tol)
                a.Warnings.Add($"A abertura {o.Index} encosta na ponta da linha: confira se a linha não passou do canto da parede.");
            a.Openings.Add(o);
        }

        // 6) correções informadas (depois do dry_run): kind/sill/head/label por índice
        if (Json.Has(p, "openings") && p.GetProperty("openings").ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement ov in p.GetProperty("openings").EnumerateArray())
            {
                int i = Json.Int(ov, "index") ?? throw new InvalidOperationException("Cada item de 'openings' precisa de 'index' (veja o dry_run).");
                Opening o = a.Openings.FirstOrDefault(x => x.Index == i) ?? throw new InvalidOperationException($"Não existe abertura {i}. Aberturas: 1..{a.Openings.Count}.");
                string? k = Json.Str(ov, "kind")?.Trim().ToLowerInvariant();
                if (k is "parede" or "nenhuma" or "remover") { a.Openings.Remove(o); continue; }
                if (k != null)
                {
                    o.Kind = k switch { "porta" or "door" => "porta", "janela" or "window" => "janela", "vão" or "vao" or "opening" => "vão", _ => throw new InvalidOperationException($"kind '{k}' inválido (porta, janela, vão ou parede).") };
                    if (o.Kind == "janela") { o.Bottom = sill; o.Top = head; } else { o.Bottom = 0; o.Top = doorH; }
                    o.Evidence = "informado pelo usuário";
                }
                if (Json.Num(ov, "sill") is { } sv) o.Bottom = sv * uf;
                if (Json.Num(ov, "bottom") is { } bv) o.Bottom = bv * uf;
                if (Json.Num(ov, "head") is { } hv) o.Top = hv * uf;
                if (Json.Num(ov, "top") is { } tv) o.Top = tv * uf;
                o.Label = Json.Str(ov, "label") ?? o.Label;
            }
        }

        // 7) pé-direito
        if (Json.Num(p, "height") is { } hp)
        {
            a.H = hp * uf; a.HeightSource = "informado";
        }
        else
        {
            (double? h, string src) = FindCeilingLabel(db, a, uf);
            a.H = h ?? throw new InvalidOperationException(
                "Não encontrei o pé-direito (texto 'P.D.' do ambiente). Pergunte ao usuário e informe height em metros (ex.: 2.60).");
            a.HeightSource = src;
        }
        if (a.H <= 0 || a.H > 30 * uf) throw new InvalidOperationException($"Pé-direito fora do razoável: {a.H / uf:0.##} m.");
        foreach (Opening o in a.Openings)
        {
            if (o.Top > a.H) { o.Top = a.H; a.Warnings.Add($"A abertura {o.Index} passava do pé-direito; limitei ao teto."); }
            if (o.Bottom >= o.Top) throw new InvalidOperationException($"Abertura {o.Index}: a base ({o.Bottom / uf:0.##} m) precisa ficar abaixo do topo ({o.Top / uf:0.##} m).");
        }
        return a;
    }

    private static (Point3d, Point3d, string) WallLine(Document doc, JsonElement p)
    {
        if (Json.Has(p, "wall_line"))
        {
            JsonElement wl = p.GetProperty("wall_line");
            return (Json.Point(wl, "start", "wall_line.start"), Json.Point(wl, "end", "wall_line.end"), "wall_line informada");
        }
        if (Json.Bool(p, "use_selection") == true)
        {
            PromptSelectionResult psr = doc.Editor.SelectImplied();
            if (psr.Status == PromptStatus.OK && psr.Value != null)
            {
                using Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
                foreach (ObjectId id in psr.Value.GetObjectIds())
                {
                    Entity? e = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    if (e is Line l) return (Flat(l.StartPoint), Flat(l.EndPoint), $"linha selecionada ({l.Handle})");
                    if (e is Polyline pl && pl.NumberOfVertices == 2) return (Flat(pl.GetPoint3dAt(0)), Flat(pl.GetPoint3dAt(1)), $"polilinha selecionada ({pl.Handle})");
                }
            }
            throw new InvalidOperationException(
                "Nenhuma LINHA selecionada. Peça ao usuário para desenhar uma linha sobre a face da parede, da esquerda para a direita " +
                "de quem olha a parede de dentro do ambiente (use OSNAP nos cantos), deixá-la selecionada e pedir de novo.");
        }
        throw new InvalidOperationException("Informe wall_line {start, end} ou use_selection=true.");
    }

    private static Point3d Flat(Point3d q) => new(q.X, q.Y, 0);

    private static void Collect(Analysis a, GeometryReader.Item it, List<Seg> segs, List<ArcInfo> arcs)
    {
        (double s, double t) ST(Point3d q) { Vector3d v = Flat(q) - a.Start; return (v.DotProduct(a.U), v.DotProduct(a.W)); }
        void AddSeg(Point3d q1, Point3d q2)
        {
            var (s1, t1) = ST(q1); var (s2, t2) = ST(q2);
            segs.Add(new Seg(s1, t1, s2, t2, it.Source));
        }
        void AddArc(Point3d center, double r, Point3d p1, Point3d p2)
        {
            var (cs, ct) = ST(center); var (s1, t1) = ST(p1); var (s2, t2) = ST(p2);
            arcs.Add(new ArcInfo(cs, ct, r, s1, t1, s2, t2, it.Source));
        }
        switch (it.Entity)
        {
            case Line l: AddSeg(l.StartPoint, l.EndPoint); break;
            case Arc ar: AddArc(ar.Center, ar.Radius, ar.StartPoint, ar.EndPoint); break;
            case Polyline pl:
                int n = pl.Closed ? pl.NumberOfVertices : pl.NumberOfVertices - 1;
                for (int i = 0; i < n; i++)
                {
                    try
                    {
                        if (pl.GetSegmentType(i) == SegmentType.Arc)
                        {
                            CircularArc3d c = pl.GetArcSegmentAt(i);
                            AddArc(c.Center, c.Radius, c.StartPoint, c.EndPoint);
                        }
                        else if (pl.GetSegmentType(i) == SegmentType.Line)
                        {
                            LineSegment3d ls = pl.GetLineSegmentAt(i);
                            AddSeg(ls.StartPoint, ls.EndPoint);
                        }
                    }
                    catch { /* segmento degenerado */ }
                }
                break;
        }
    }

    private static Extents3d Box(Analysis a, double s0, double s1, double t0, double t1)
    {
        Point3d[] c =
        {
            a.Start + a.U * s0 + a.W * t0, a.Start + a.U * s1 + a.W * t0,
            a.Start + a.U * s0 + a.W * t1, a.Start + a.U * s1 + a.W * t1,
        };
        return new Extents3d(new Point3d(c.Min(q => q.X), c.Min(q => q.Y), -1e9), new Point3d(c.Max(q => q.X), c.Max(q => q.Y), 1e9));
    }

    private static List<(double A, double B)> Merge(IEnumerable<(double A, double B)> spans, double join)
    {
        var res = new List<(double A, double B)>();
        foreach (var s in spans.OrderBy(x => x.A))
        {
            if (res.Count > 0 && s.A <= res[^1].B + join) res[^1] = (res[^1].A, Math.Max(res[^1].B, s.B));
            else res.Add(s);
        }
        return res;
    }

    private static double Overlap(double a1, double b1, double a2, double b2) => Math.Max(0, Math.Min(b1, b2) - Math.Max(a1, a2));

    private static readonly Regex CeilingRx = new(@"P\s*\.?\s*D\s*\.?\s*[=:]?\s*(\d+(?:[.,]\d+)?)\s*(m|cm|mm)?", RegexOptions.IgnoreCase);

    /// <summary>Texto "P.D.= 2.60 m" mais próximo do meio da parede, do lado do ambiente, até 8 m.</summary>
    private static (double?, string) FindCeilingLabel(Database db, Analysis a, double uf)
    {
        var found = new List<(double Dist, double H, string Text)>();
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var opt = new GeometryReader.Options
        {
            Region = Box(a, -3 * uf, a.L + 3 * uf, -8 * uf, 0.5 * uf),
            Types = new HashSet<string> { "TEXT", "MTEXT", "ATTRIB" },
        };
        Point3d mid = a.Start + a.U * (a.L / 2);
        GeometryReader.Walk(db, tr, opt, it =>
        {
            (string text, Point3d pos) = it.Entity switch
            {
                MText mt => (mt.Text, mt.Location),
                DBText t => (t.TextString, t.Position),
                _ => ("", Point3d.Origin),
            };
            Match mm = CeilingRx.Match(text);
            if (!mm.Success) return true;
            if ((Flat(pos) - a.Start).DotProduct(a.W) > 0.05 * uf) return true;   // do outro lado da parede
            double v = double.Parse(mm.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            string unit = mm.Groups[2].Value.ToLowerInvariant();
            double meters = unit switch { "cm" => v / 100, "mm" => v / 1000, "m" => v, _ => v < 10 ? v : v < 1000 ? v / 100 : v / 1000 };
            found.Add(((Flat(pos) - mid).Length, meters * uf, text.Replace("\r", " ").Replace("\n", " ").Trim()));
            return true;
        });
        if (found.Count == 0) return (null, "");
        var best = found.OrderBy(x => x.Dist).First();
        return (best.H, $"texto do ambiente: \"{best.Text}\"");
    }

    /// <summary>Altura de texto coerente com as cotas do desenho (DIMTXT × DIMSCALE).</summary>
    private static double TextHeight(Database db, double wallHeight)
    {
        double scale = db.Dimscale > 0 ? db.Dimscale : 1;
        double h = db.Dimtxt * scale;
        return h > wallHeight / 200 && h < wallHeight / 5 ? h : wallHeight / 40;
    }

    /// <summary>
    /// Abaixo do que existe na VIZINHANÇA da parede (12 m), alinhada ao início dela. "Abaixo de tudo no
    /// desenho" falhava em folhas reais: um objeto solto a 1 km de distância jogava a vista para longe.
    /// </summary>
    private static Point3d AutoOrigin(Database db, Analysis a)
    {
        double uf = a.Unit.PerMeter;
        double minX = Math.Min(a.Start.X, a.End.X), minY = Math.Min(a.Start.Y, a.End.Y);
        var near = new Extents3d(new Point3d(minX - 12 * uf, minY - 12 * uf, -1e9),
                                 new Point3d(Math.Max(a.Start.X, a.End.X) + 12 * uf, Math.Max(a.Start.Y, a.End.Y) + 12 * uf, 1e9));
        double low = minY;
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            GeometryReader.Walk(db, tr, new GeometryReader.Options { Region = near }, it =>
            {
                if (GeometryReader.TryExtents(it.Entity, Matrix3d.Identity, out Extents3d ex))
                    low = Math.Min(low, Math.Max(ex.MinPoint.Y, near.MinPoint.Y));
                return true;
            });
        }
        return new Point3d(minX, low - a.H - 2.0 * uf, 0);
    }

    private static string Short(Opening o) => o.Kind switch { "porta" => $"P{o.Index}", "janela" => $"J{o.Index}", _ => $"V{o.Index}" };

    private static object Report(Analysis a, Canvas? drawn, Point3d? origin)
    {
        double uf = a.Unit.PerMeter;
        double Mt(double v) => Math.Round(v / uf, 3);
        return new
        {
            action = drawn == null ? "generate_elevation (dry_run: nada foi desenhado)" : "generate_elevation",
            wall_line = new { start = GeometryReader.P2(a.Start), end = GeometryReader.P2(a.End), source = a.WallLineSource },
            units = new { unit = a.Unit.Name, reason = a.Unit.Reason },
            wall_length_m = Mt(a.L),
            ceiling_height_m = Mt(a.H),
            ceiling_height_source = a.HeightSource,
            wall_thickness_m = Mt(a.Thickness),
            wall_thickness_source = a.ThicknessSource,
            face_found_ratio = Math.Round(a.Coverage, 3),
            face_offset_applied_m = Mt(a.FaceOffset),
            elements_read = a.Elements,
            openings = a.Openings.Select(o => new
            {
                index = o.Index,
                kind = o.Kind,
                label = o.Label ?? Short(o),
                from_m = Mt(o.From), to_m = Mt(o.To), width_m = Mt(o.To - o.From),
                bottom_m = Mt(o.Bottom), top_m = Mt(o.Top),
                evidence = o.Evidence,
            }),
            warnings = a.Warnings,
            drawn = drawn == null ? null : new
            {
                origin = origin is { } og ? GeometryReader.P2(og) : null,
                bounds = drawn.Bounds,
                by_type = drawn.Counts,
                layers_created = drawn.CreatedLayers,
            },
            next = drawn == null
                ? "Mostre ao usuário as aberturas, o pé-direito e as alturas. Corrija com 'openings' [{index, kind, sill, head, label}] " +
                  "e/ou 'height'; depois chame de novo sem dry_run para desenhar."
                : "Para desfazer: digite U no AutoCAD (desfaz a vista inteira de uma vez). Use capture_view para conferir.",
        };
    }
}
