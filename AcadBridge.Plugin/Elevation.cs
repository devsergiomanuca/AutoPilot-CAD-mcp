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

    /// <summary>Área de acabamento (pintura, revestimento, painel) no plano da parede, com chamada de texto.</summary>
    private sealed class Finish
    {
        public int Index;
        public string Label = "";
        public double From, To, Bottom, Top;   // unidades do desenho
        public Autodesk.AutoCAD.Colors.Color? Color;
        public string Pattern = "SOLID";
        public double Scale = 1;
    }

    private sealed class Analysis
    {
        public double? Ceiling;        // forro (unidades); null = sem forro
        public double CeilingT;
        public double? Baseboard;      // rodapé (unidades)
        public bool SideWalls = true;  // paredes laterais em corte
        public double SideT;
        public readonly List<Finish> Finishes = new();
        public double Top => Ceiling ?? H;   // até onde a parede é vista
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

    internal sealed record ProbeOpening(string Kind, double FromM, double ToM, string Evidence);
    internal sealed record Probe(Point3d Start, Point3d End, double ThicknessM, double Coverage, List<ProbeOpening> Openings);

    /// <summary>
    /// Mesma análise da vista, só para LER uma parede (usada pelo analyze_plan): espessura e aberturas
    /// (porta/janela/vão) ao longo da linha, já orientada pelo observador (de dentro do ambiente).
    /// </summary>
    internal static Probe ProbeWall(Document doc, Point2d start, Point2d end, Point2d viewer, string unit)
    {
        string F(double v) => v.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        string json = $"{{\"wall_line\":{{\"start\":[{F(start.X)},{F(start.Y)}],\"end\":[{F(end.X)},{F(end.Y)}]}}," +
                      $"\"viewer_point\":[{F(viewer.X)},{F(viewer.Y)}],\"units\":\"{unit}\",\"height\":2.8}}";
        using JsonDocument jd = JsonDocument.Parse(json);
        Analysis a = Analyze(doc, jd.RootElement);
        double uf = a.Unit.PerMeter;
        return new Probe(a.Start, a.End, a.Thickness / uf, a.Coverage,
                         a.Openings.Select(o => new ProbeOpening(o.Kind, o.From / uf, o.To / uf, o.Evidence)).ToList());
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
        string lCut = prefix + "-CORTE", lFin = prefix + "-ACABAMENTOS";
        c.Layer(lWall, 7); c.Layer(lOpen, 4); c.Layer(lDim, 1); c.Layer(lTxt, 2);

        double uf = a.Unit.PerMeter;
        double txt = TextHeight(db, a.H);
        c.FitDims(Math.Max(a.L, a.H), txt);   // cotas com o mesmo tamanho dos textos da vista (DIMSCALE de outra escala não estoura)
        Point3d W(double x, double y) => new(origin.X + x, origin.Y + y, 0);
        Point2d W2(double x, double y) => new(origin.X + x, origin.Y + y);
        Point2d[] RectPts(double x0, double y0, double x1, double y1) => new[] { W2(x0, y0), W2(x1, y0), W2(x1, y1), W2(x0, y1) };
        double sideT = a.SideWalls ? a.SideT : 0;
        // ANSI31 com ~3 cm entre linhas, em qualquer unidade. Com MEASUREMENT=English o AutoCAD usa o acad.pat (em
        // polegadas: 0,125 entre linhas, 25,4× mais denso que o acadiso.pat), e a hachura parecia sólida.
        double cutScale = 0.01 * uf * (db.Measurement == MeasurementValue.Metric ? 1 : 25.4);

        // acabamentos primeiro: hachuras criadas antes ficam por baixo das linhas
        if (a.Finishes.Count > 0) c.Layer(lFin, 8);
        foreach (Finish f in a.Finishes)
            c.Hatch(RectPts(f.From, f.Bottom, f.To, f.Top), f.Pattern, f.Pattern == "SOLID" ? 1 : f.Scale * cutScale * 10, lFin, f.Color,
                    Holes(a, f, W2).Select(h => (IReadOnlyList<Point2d>)h));

        // contorno da parede (até a laje) + linha de piso
        c.Rect(W2(0, 0), W2(a.L, a.H), lWall);
        c.Line(W(-sideT - 0.3 * uf, 0), W(a.L + sideT + 0.3 * uf, 0), lWall);

        // paredes laterais em corte (convenção de vista interna) e forro em corte
        if (a.SideWalls || a.Ceiling != null) c.Layer(lCut, 8);
        if (a.SideWalls)
        {
            foreach ((double x0, double x1) in new[] { (-sideT, 0.0), (a.L, a.L + sideT) })
            {
                c.Rect(W2(x0, 0), W2(x1, a.H), lCut);
                c.Hatch(RectPts(x0, 0, x1, a.H), "ANSI31", cutScale, lCut);
            }
        }
        if (a.Ceiling is { } ceil)
        {
            double t = Math.Min(a.CeilingT, a.H - ceil);
            c.Rect(W2(0, ceil), W2(a.L, ceil + t), lCut);
            if (t > 1e-9) c.Hatch(RectPts(0, ceil, a.L, ceil + t), "SOLID", 1, lCut);
            c.Text(W(a.L / 2, ceil + t + txt * 0.4), "FORRO", txt * 0.7, 0, lTxt, "center");
        }

        // rodapé: só onde há parede até o piso (portas e vãos interrompem)
        if (a.Baseboard is { } bbh)
        {
            double cur = 0;
            foreach (Opening o in a.Openings.Where(o => o.Bottom < bbh).OrderBy(o => o.From))
            {
                if (o.From - cur > 1e-6) c.Line(W(cur, bbh), W(o.From, bbh), lWall);
                cur = Math.Max(cur, o.To);
            }
            if (a.L - cur > 1e-6) c.Line(W(cur, bbh), W(a.L, bbh), lWall);
        }

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

        // cotas horizontais: corrente (aberturas + limites dos acabamentos) e total
        double off = txt * 4;
        var xs = new SortedSet<double> { 0, a.L };
        foreach (Opening o in a.Openings) { xs.Add(o.From); xs.Add(o.To); }
        foreach (Finish f in a.Finishes) { xs.Add(f.From); xs.Add(f.To); }
        double[] xa = Dedup(xs, 0.005 * uf);
        for (int i = 0; i + 1 < xa.Length; i++) c.Dim(W(xa[i], 0), W(xa[i + 1], 0), W(xa[i], -off), 0, lDim);
        if (xa.Length > 2) c.Dim(W(0, 0), W(a.L, 0), W(0, -2 * off), 0, lDim);

        // cotas verticais à direita: corrente (acabamentos, forro) e total até a laje
        var zs = new SortedSet<double> { 0, a.H };
        if (a.Ceiling is { } cz) zs.Add(cz);
        foreach (Finish f in a.Finishes) { zs.Add(f.Bottom); zs.Add(f.Top); }
        double[] za = Dedup(zs, 0.005 * uf);
        double xr = a.L + sideT + off;
        for (int i = 0; i + 1 < za.Length; i++) c.Dim(W(a.L + sideT, za[i]), W(a.L + sideT, za[i + 1]), W(xr, za[i]), Math.PI / 2, lDim);
        if (za.Length > 2) c.Dim(W(a.L + sideT, 0), W(a.L + sideT, a.H), W(xr + off, 0), Math.PI / 2, lDim);

        // alturas das aberturas, junto de cada uma
        foreach (Opening o in a.Openings)
        {
            double x = o.To + off * 0.6;
            if (o.Bottom > 1e-6) c.Dim(W(o.To, 0), W(o.To, o.Bottom), W(x, 0), Math.PI / 2, lDim);
            c.Dim(W(o.To, o.Bottom), W(o.To, o.Top), W(x, o.Bottom), Math.PI / 2, lDim);
        }

        // chamadas dos acabamentos: texto à direita das cotas, empilhado de cima para baixo sem sobrepor
        double tx = xr + (za.Length > 2 ? 2 : 1) * off;
        double nextY = double.MaxValue;
        foreach (Finish f in a.Finishes.Where(f => f.Label.Length > 0).OrderByDescending(f => (f.Bottom + f.Top) / 2))
        {
            (double px, double py) = LeaderPoint(a, f);
            int lines = 1 + f.Label.Count(ch2 => ch2 == '\n') + f.Label.Length / 40;
            double ty = Math.Min(py, nextY);
            c.Leader(new[] { W(px, py), W(tx - txt * 0.5, ty) }, lTxt);
            c.MText(W(tx, ty), f.Label.Replace("\n", "\\P"), txt * 0.8, lTxt, AttachmentPoint.MiddleLeft, txt * 25);
            nextY = ty - txt * (1.6 * lines + 1.2);
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

        // 8) forro, rodapé e paredes laterais em corte
        if (Json.Num(p, "ceiling_height") is { } ch)
        {
            if (ch <= 0) throw new InvalidOperationException("ceiling_height (forro) deve ser > 0.");
            if (ch * uf > a.H + 1e-9)
                throw new InvalidOperationException($"O forro ({ch:0.##} m) está acima do pé-direito ({a.H / uf:0.##} m). Confira os dois com o usuário (height = laje, ceiling_height = forro).");
            if (ch * uf < a.H - 1e-6) a.Ceiling = ch * uf;
        }
        a.CeilingT = (Json.Num(p, "ceiling_thickness") ?? 0.02) * uf;
        if (a.CeilingT <= 0 || a.CeilingT > 0.5 * uf) throw new InvalidOperationException("ceiling_thickness deve estar entre 0 e 0,5 m.");
        if (Json.Num(p, "baseboard") is { } bb)
        {
            if (bb <= 0 || bb > 0.5) throw new InvalidOperationException("baseboard (rodapé) deve estar entre 0 e 0,5 m.");
            a.Baseboard = bb * uf;
        }
        a.SideWalls = Json.Bool(p, "side_walls") ?? true;
        a.SideT = Json.Num(p, "side_wall_thickness") is { } st ? st * uf : a.Thickness;
        if (a.SideT <= 0 || a.SideT > 1 * uf) throw new InvalidOperationException("side_wall_thickness deve estar entre 0 e 1 m.");

        foreach (Opening o in a.Openings)
        {
            if (o.Top > a.Top) { o.Top = a.Top; a.Warnings.Add($"A abertura {o.Index} passava do {(a.Ceiling != null ? "forro" : "pé-direito")}; limitei."); }
            if (o.Bottom >= o.Top) throw new InvalidOperationException($"Abertura {o.Index}: a base ({o.Bottom / uf:0.##} m) precisa ficar abaixo do topo ({o.Top / uf:0.##} m).");
        }

        // 9) acabamentos: [{label, from, to, bottom, top, color, pattern, scale}] em metros a partir do início da parede e do piso
        if (Json.Has(p, "finishes") && p.GetProperty("finishes").ValueKind == JsonValueKind.Array)
        {
            short[] palette = { 51, 31, 131, 171, 211, 91, 11, 151 };
            int fi = 0;
            foreach (JsonElement fe in p.GetProperty("finishes").EnumerateArray())
            {
                fi++;
                string where = $"finishes[{fi}]";
                var f = new Finish
                {
                    Index = fi,
                    Label = (Json.Str(fe, "label") ?? "").Trim(),
                    From = (Json.Num(fe, "from") ?? 0) * uf,
                    To = Json.Num(fe, "to") is { } tv ? tv * uf : a.L,
                    Bottom = Json.Num(fe, "bottom") is { } bv ? bv * uf : a.Baseboard ?? 0,
                    Top = Json.Num(fe, "top") is { } tpv ? tpv * uf : a.Top,
                    Pattern = (Json.Str(fe, "pattern") ?? "SOLID").Trim().ToUpperInvariant(),
                    Scale = Json.Num(fe, "scale") ?? 1,
                };
                double eps = 0.005 * uf;
                if (f.From < -eps || f.To > a.L + eps || f.To - f.From <= eps)
                    throw new InvalidOperationException($"{where}: 'from' e 'to' devem estar entre 0 e {a.L / uf:0.###} m (comprimento da parede), com from < to.");
                if (f.Bottom < -eps || f.Top > a.Top + eps || f.Top - f.Bottom <= eps)
                    throw new InvalidOperationException($"{where}: 'bottom' e 'top' devem estar entre 0 e {a.Top / uf:0.###} m ({(a.Ceiling != null ? "forro" : "pé-direito")}), com bottom < top.");
                f.From = Math.Max(0, f.From); f.To = Math.Min(a.L, f.To);
                f.Bottom = Math.Max(0, f.Bottom); f.Top = Math.Min(a.Top, f.Top);
                if (!Regex.IsMatch(f.Pattern, @"^[A-Z0-9_\-]+$")) throw new InvalidOperationException($"{where}: 'pattern' inválido ('{f.Pattern}'). Ex.: SOLID, ANSI31, AR-CONC.");
                f.Color = Json.Has(fe, "color") ? DrawingEdits.ParseColor(fe.GetProperty("color"))
                                                : Autodesk.AutoCAD.Colors.Color.FromColorIndex(Autodesk.AutoCAD.Colors.ColorMethod.ByAci, palette[(fi - 1) % palette.Length]);
                a.Finishes.Add(f);
            }
        }
        return a;
    }

    /// <summary>Aberturas que cortam a área (para virar furo na hachura), já recortadas ao retângulo.</summary>
    private static IEnumerable<Point2d[]> Holes(Analysis a, Finish f, Func<double, double, Point2d> w)
    {
        foreach (Opening o in a.Openings)
        {
            double x0 = Math.Max(f.From, o.From), x1 = Math.Min(f.To, o.To);
            double y0 = Math.Max(f.Bottom, o.Bottom), y1 = Math.Min(f.Top, o.Top);
            if (x1 - x0 <= 1e-9 || y1 - y0 <= 1e-9) continue;
            yield return new[] { w(x0, y0), w(x1, y0), w(x1, y1), w(x0, y1) };
        }
    }

    /// <summary>Ponto dentro da área e fora das aberturas, para a seta da chamada.</summary>
    private static (double X, double Y) LeaderPoint(Analysis a, Finish f)
    {
        double y = (f.Bottom + f.Top) / 2;
        foreach (double k in new[] { 0.5, 0.25, 0.75, 0.1, 0.9 })
        {
            double x = f.From + (f.To - f.From) * k;
            if (!a.Openings.Any(o => x > o.From && x < o.To && y > o.Bottom && y < o.Top)) return (x, y);
        }
        return ((f.From + f.To) / 2, f.Top - (f.Top - f.Bottom) * 0.1);
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

    /// <summary>Valores ordenados sem vizinhos quase iguais (evita cota de 0,3 mm entre duas bordas).</summary>
    private static double[] Dedup(IEnumerable<double> sorted, double tol)
    {
        var res = new List<double>();
        foreach (double v in sorted) if (res.Count == 0 || v - res[^1] > tol) res.Add(v);
        return res.ToArray();
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
        // Procura um lugar VAZIO perto da parede: primeiro abaixo, depois à direita, à esquerda e acima, afastando
        // aos poucos. "Abaixo do que existe a 12 m" caía em cima de hachuras grandes (paisagismo) que passavam da
        // vizinhança. Objetos enormes (contorno do terreno, eixos) não contam: tudo ficaria "ocupado".
        double uf = a.Unit.PerMeter;
        double minX = Math.Min(a.Start.X, a.End.X), minY = Math.Min(a.Start.Y, a.End.Y);
        double maxX = Math.Max(a.Start.X, a.End.X), maxY = Math.Max(a.Start.Y, a.End.Y);
        double w = a.L + 5.0 * uf, h = a.H + 3.0 * uf;       // vista + cotas à direita/abaixo + chamadas + título
        double reach = 60 * uf;
        var near = new Extents3d(new Point3d(minX - reach, minY - reach, -1e9), new Point3d(maxX + reach, maxY + reach, 1e9));
        var boxes = new List<Extents3d>();
        using (Transaction tr = db.TransactionManager.StartOpenCloseTransaction())
        {
            GeometryReader.Walk(db, tr, new GeometryReader.Options { Region = near }, it =>
            {
                if (GeometryReader.TryExtents(it.Entity, Matrix3d.Identity, out Extents3d ex) &&
                    !(ex.MaxPoint.X - ex.MinPoint.X > 50 * uf && ex.MaxPoint.Y - ex.MinPoint.Y > 50 * uf))   // só ignora o que é enorme nas DUAS direções
                    boxes.Add(ex);
                return boxes.Count < 200_000;
            });
        }
        // canto inferior esquerdo da área livre → origem da vista (a vista ocupa x0+1..x0+1+L, y0+2..y0+2+H)
        bool Free(double x0, double y0) => !boxes.Any(b => b.MinPoint.X < x0 + w && b.MaxPoint.X > x0 && b.MinPoint.Y < y0 + h && b.MaxPoint.Y > y0);
        double step = 2.0 * uf;
        for (int k = 0; k * step <= reach; k++)
        {
            double d = k * step;
            var tries = new (double X, double Y)[]
            {
                (minX - 1 * uf, minY - 1 * uf - h - d),     // abaixo
                (maxX + 1 * uf + d, minY - 2 * uf),         // à direita
                (minX - 1 * uf - w - d, minY - 2 * uf),     // à esquerda
                (minX - 1 * uf, maxY + 1 * uf + d),         // acima
            };
            foreach ((double x0, double y0) in tries)
                if (Free(x0, y0)) return new Point3d(x0 + 1 * uf, y0 + 2 * uf, 0);
        }
        return new Point3d(minX, minY - reach - a.H, 0);    // nada livre por perto: abaixo de tudo
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
            ceiling_m = a.Ceiling is { } cm ? Mt(cm) : (double?)null,
            baseboard_m = a.Baseboard is { } bm ? Mt(bm) : (double?)null,
            side_walls_cut = a.SideWalls ? new { thickness_m = Mt(a.SideT) } : null,
            finishes = a.Finishes.Select(f => new
            {
                index = f.Index, label = f.Label,
                from_m = Mt(f.From), to_m = Mt(f.To), bottom_m = Mt(f.Bottom), top_m = Mt(f.Top),
                pattern = f.Pattern, color = f.Color?.ColorNameForDisplay,
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
                  "e/ou 'height'. Pergunte também (se ele ainda não disse): forro (ceiling_height), rodapé (baseboard) e os " +
                  "acabamentos de cada trecho da parede (finishes: label, from/to, bottom/top em m, color). Depois chame de novo " +
                  "sem dry_run para desenhar."
                : "Para desfazer: digite U no AutoCAD (desfaz a vista inteira de uma vez). Use capture_view para conferir.",
        };
    }
}
