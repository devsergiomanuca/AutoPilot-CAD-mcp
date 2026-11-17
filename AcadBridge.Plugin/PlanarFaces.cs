using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadBridge.Plugin;

/// <summary>
/// Faces fechadas de um conjunto de segmentos 2D (o que o HATCH faz ao "clicar dentro"), calculadas por
/// nós mesmos: o Editor.TraceBoundary do AutoCAD não enxerga geometria de blocos aninhados/espelhados,
/// que é onde as plantas reais costumam estar.
///
/// Algoritmo clássico de arranjo planar:
///  1. vértices fundidos numa grade de tolerância;
///  2. cada segmento é quebrado em todos os cruzamentos/encostos com os outros;
///  3. pontas soltas (grau 1) são podadas repetidamente: não fecham nada;
///  4. em cada vértice as arestas são ordenadas por ângulo; percorrendo meias-arestas e virando sempre "o
///     mais à direita possível", cada ciclo é uma face. Área com sinal > 0 = face limitada; &lt; 0 = contorno
///     externo de um grupo de linhas (vira "ilha" de outra face).
/// </summary>
internal sealed class PlanarFaces
{
    public sealed class Face
    {
        public List<Point2d> Pts = new();
        public double Area;          // com sinal
        public double Perimeter;
        public Extents2d Box;
        public Dictionary<int, double> TagLength = new();   // comprimento do contorno por origem (tag)
        public int DominantTag => TagLength.Count == 0 ? -1 : TagLength.OrderByDescending(kv => kv.Value).First().Key;
    }

    public readonly List<Face> Bounded = new();
    public readonly List<Face> Outer = new();   // contornos externos (área negativa), para achar ilhas
    public int Vertices, Edges;

    public PlanarFaces(IReadOnlyList<(Point2d A, Point2d B)> segs, double tol, IReadOnlyList<int>? tags = null)
    {
        // 1) vértices fundidos numa grade
        var pts = new List<Point2d>();
        var grid = new Dictionary<(long, long), int>();
        int V(Point2d p)
        {
            long gx = (long)Math.Round(p.X / tol), gy = (long)Math.Round(p.Y / tol);
            for (long dx = -1; dx <= 1; dx++)
                for (long dy = -1; dy <= 1; dy++)
                    if (grid.TryGetValue((gx + dx, gy + dy), out int id) && pts[id].GetDistanceTo(p) <= tol) return id;
            pts.Add(p);
            grid[(gx, gy)] = pts.Count - 1;
            return pts.Count - 1;
        }

        // 2) quebra nos cruzamentos (com pré-filtro por caixa)
        int n = segs.Count;
        var cuts = new List<double>[n];
        var box = new (double x0, double y0, double x1, double y1)[n];
        for (int i = 0; i < n; i++)
        {
            cuts[i] = new List<double> { 0, 1 };
            var (a, b) = segs[i];
            box[i] = (Math.Min(a.X, b.X) - tol, Math.Min(a.Y, b.Y) - tol, Math.Max(a.X, b.X) + tol, Math.Max(a.Y, b.Y) + tol);
        }
        var order = Enumerable.Range(0, n).OrderBy(i => box[i].x0).ToArray();
        for (int oi = 0; oi < n; oi++)
        {
            int i = order[oi];
            for (int oj = oi + 1; oj < n; oj++)
            {
                int j = order[oj];
                if (box[j].x0 > box[i].x1) break;
                if (box[j].y0 > box[i].y1 || box[j].y1 < box[i].y0) continue;
                Intersect(segs[i], segs[j], tol, cuts[i], cuts[j]);
            }
        }

        // arestas únicas
        var edges = new HashSet<(int, int)>();
        var edgeTag = new Dictionary<(int, int), int>();   // de qual objeto/bloco veio cada aresta (o primeiro)
        for (int i = 0; i < n; i++)
        {
            var (a, b) = segs[i];
            int prev = -1;
            foreach (double t in cuts[i].Distinct().OrderBy(x => x))
            {
                int v = V(new Point2d(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t));
                if (prev >= 0 && prev != v)
                {
                    var key = prev < v ? (prev, v) : (v, prev);
                    if (edges.Add(key) && tags != null) edgeTag[key] = tags[i];
                }
                prev = v;
            }
        }

        // 3) adjacência e poda das pontas soltas
        var adj = new Dictionary<int, HashSet<int>>();
        foreach (var (u, v) in edges)
        {
            if (!adj.TryGetValue(u, out var su)) adj[u] = su = new HashSet<int>();
            if (!adj.TryGetValue(v, out var sv)) adj[v] = sv = new HashSet<int>();
            su.Add(v); sv.Add(u);
        }
        var stack = new Stack<int>(adj.Where(kv => kv.Value.Count <= 1).Select(kv => kv.Key));
        while (stack.Count > 0)
        {
            int v = stack.Pop();
            if (!adj.TryGetValue(v, out var nb) || nb.Count > 1) continue;
            foreach (int w in nb.ToList())
            {
                adj[w].Remove(v);
                if (adj[w].Count <= 1) stack.Push(w);
            }
            adj.Remove(v);
        }
        Vertices = adj.Count;
        Edges = adj.Values.Sum(s => s.Count) / 2;

        // 4) vizinhos ordenados por ângulo (anti-horário) e percurso das faces
        var sorted = adj.ToDictionary(kv => kv.Key,
            kv => kv.Value.OrderBy(w => Math.Atan2(pts[w].Y - pts[kv.Key].Y, pts[w].X - pts[kv.Key].X)).ToArray());
        var used = new HashSet<(int, int)>();
        foreach (var kv in sorted)
        {
            foreach (int first in kv.Value)
            {
                if (used.Contains((kv.Key, first))) continue;
                var cyc = new List<int>();
                int u = kv.Key, v = first, guard = 0;
                while (used.Add((u, v)) && guard++ < 200_000)
                {
                    cyc.Add(u);
                    int[] around = sorted[v];
                    int k = Array.IndexOf(around, u);
                    int w = around[(k - 1 + around.Length) % around.Length];   // vira o máximo à direita... face à esquerda
                    u = v; v = w;
                }
                if (cyc.Count < 3) continue;
                var f = new Face { Pts = cyc.Select(id => pts[id]).ToList() };
                if (tags != null)
                    for (int ci = 0; ci < cyc.Count; ci++)
                    {
                        int a2 = cyc[ci], b2 = cyc[(ci + 1) % cyc.Count];
                        if (edgeTag.TryGetValue(a2 < b2 ? (a2, b2) : (b2, a2), out int tg))
                            f.TagLength[tg] = f.TagLength.GetValueOrDefault(tg) + pts[a2].GetDistanceTo(pts[b2]);
                    }
                f.Area = SignedArea(f.Pts);
                f.Perimeter = Perim(f.Pts);
                f.Box = new Extents2d(f.Pts.Min(q => q.X), f.Pts.Min(q => q.Y), f.Pts.Max(q => q.X), f.Pts.Max(q => q.Y));
                if (f.Area > tol * tol) Bounded.Add(f);
                else if (f.Area < -tol * tol) Outer.Add(f);
            }
        }
    }

    private static void Intersect((Point2d A, Point2d B) s, (Point2d A, Point2d B) o, double tol, List<double> cs, List<double> co)
    {
        Vector2d r = s.B - s.A, q = o.B - o.A;
        double lr = r.Length, lq = q.Length;
        if (lr < 1e-12 || lq < 1e-12) return;
        double den = r.X * q.Y - r.Y * q.X;
        Vector2d w = o.A - s.A;
        if (Math.Abs(den) < 1e-12 * lr * lq)
        {
            // paralelos: só importa se colineares (sobrepostos) → quebra nas pontas um do outro
            if (Math.Abs(w.X * r.Y - w.Y * r.X) / lr > tol) return;
            foreach (Point2d p in new[] { o.A, o.B })
            {
                double t = (p - s.A).DotProduct(r) / (lr * lr);
                if (t > 0 && t < 1) cs.Add(t);
            }
            foreach (Point2d p in new[] { s.A, s.B })
            {
                double t = (p - o.A).DotProduct(q) / (lq * lq);
                if (t > 0 && t < 1) co.Add(t);
            }
            return;
        }
        double ts = (w.X * q.Y - w.Y * q.X) / den;
        double to = (w.X * r.Y - w.Y * r.X) / den;
        double es = tol / lr, eo = tol / lq;   // tolerância para "encostar" (T, pontas quase tocando)
        if (ts < -es || ts > 1 + es || to < -eo || to > 1 + eo) return;
        if (ts > es && ts < 1 - es) cs.Add(ts);
        if (to > eo && to < 1 - eo) co.Add(to);
    }

    public static double SignedArea(IReadOnlyList<Point2d> p)
    {
        double a = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) a += (p[j].X * p[i].Y) - (p[i].X * p[j].Y);
        return a / 2;
    }

    private static double Perim(IReadOnlyList<Point2d> p)
    {
        double l = 0;
        for (int i = 0, j = p.Count - 1; i < p.Count; j = i++) l += p[i].GetDistanceTo(p[j]);
        return l;
    }

    public static bool Contains(IReadOnlyList<Point2d> poly, Point2d q)
    {
        bool inside = false;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            Point2d a = poly[i], b = poly[j];
            if ((a.Y > q.Y) != (b.Y > q.Y) && q.X < (b.X - a.X) * (q.Y - a.Y) / (b.Y - a.Y) + a.X) inside = !inside;
        }
        return inside;
    }

    /// <summary>Menor face limitada que contém o ponto (o "clicar dentro" do HATCH).</summary>
    public Face? FaceAt(Point2d q) =>
        Bounded.Where(f => q.X >= f.Box.MinPoint.X && q.X <= f.Box.MaxPoint.X && q.Y >= f.Box.MinPoint.Y && q.Y <= f.Box.MaxPoint.Y && Contains(f.Pts, q))
               .OrderBy(f => f.Area).FirstOrDefault();

    /// <summary>Ilhas: grupos de linhas soltos DENTRO da face (móveis num ambiente), para virar furo na hachura.</summary>
    public List<Face> IslandsOf(Face f) =>
        Outer.Where(o => -o.Area < f.Area
                         && o.Box.MinPoint.X >= f.Box.MinPoint.X && o.Box.MaxPoint.X <= f.Box.MaxPoint.X
                         && o.Box.MinPoint.Y >= f.Box.MinPoint.Y && o.Box.MaxPoint.Y <= f.Box.MaxPoint.Y
                         && o.Pts.All(p => Contains(f.Pts, p)))
             .ToList();
}
