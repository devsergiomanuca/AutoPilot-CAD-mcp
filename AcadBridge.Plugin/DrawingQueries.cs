using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadBridge.Plugin;

/// <summary>
/// Consultas SOMENTE LEITURA ao desenho. Todas rodam na thread principal, com o documento travado,
/// dentro de uma OpenCloseTransaction que nunca é confirmada (Commit) — nada é gravado.
/// </summary>
internal static class DrawingQueries
{
    // ---------- ajudantes de formatação ----------
    private static double R(double v) => double.IsFinite(v) ? Math.Round(v, 4) : 0;
    private static double[] P(Point3d p) => new[] { R(p.X), R(p.Y), R(p.Z) };
    private static double Deg(double radians) => R(radians * 180.0 / Math.PI);

    /// <summary>LineWeight025 → "0.25 mm"; valores especiais → "Default"/"ByLayer"/"ByBlock".</summary>
    internal static string Lw(LineWeight lw) => (int)lw switch
    {
        -3 => "Default",
        -2 => "ByBlock",
        -1 => "ByLayer",
        int h => (h / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " mm",
    };

    // ---------- get_drawing_summary ----------
    public static object Summary(Document doc)
    {
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

        // Contagem por tipo SEM abrir as entidades: ObjectId.ObjectClass já sabe o tipo.
        var byType = new SortedDictionary<string, int>(StringComparer.Ordinal);
        int total = 0;
        foreach (ObjectId id in ms)
        {
            total++;
            string n = id.ObjectClass.DxfName;
            byType[n] = byType.TryGetValue(n, out int c) ? c + 1 : 1;
        }

        var layouts = new List<(int order, object info)>();
        var ld = (DBDictionary)tr.GetObject(db.LayoutDictionaryId, OpenMode.ForRead);
        foreach (DBDictionaryEntry e in ld)
        {
            var lay = (Layout)tr.GetObject(e.Value, OpenMode.ForRead);
            var lbtr = (BlockTableRecord)tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
            int n = 0;
            foreach (ObjectId _ in lbtr) n++;
            layouts.Add((lay.TabOrder, new { name = lay.LayoutName, is_model = lay.ModelType, entities = n }));
        }

        int userBlocks = 0;
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (!btr.IsLayout && !btr.IsAnonymous) userBlocks++;
        }

        object? extents = ModelExtents(db, tr, ms, total);
        return new
        {
            document = doc.Name,
            file_path = db.Filename,
            read_only = doc.IsReadOnly,
            unsaved_changes = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
            units = db.Insunits.ToString(),
            measurement = db.Measurement.ToString(),
            current_layer = ((LayerTableRecord)tr.GetObject(db.Clayer, OpenMode.ForRead)).Name,
            current_layout = Convert.ToString(AcApp.GetSystemVariable("CTAB")),
            extents,
            model_space = new { total_entities = total, by_type = byType },
            layouts = layouts.OrderBy(l => l.order).Select(l => l.info).ToList(),
            layer_count = Count(tr, db.LayerTableId),
            block_definitions = userBlocks,
            text_styles = Count(tr, db.TextStyleTableId),
            dimension_styles = Count(tr, db.DimStyleTableId),
        };
    }

    /// <summary>Acima disto, abrir todas as entidades fica caro: usa EXTMIN/EXTMAX e avisa.</summary>
    private const int MaxEntitiesForExtents = 50_000;

    /// <summary>
    /// Extensão do Model calculada pelas ENTIDADES. EXTMIN/EXTMAX (o que o AutoCAD guarda) só são
    /// recalculados em certos momentos (ZOOM E, REGEN...) e num desenho novo chegaram a vir em ±10⁷ mm.
    /// Entidades sem extensão finita (XLINE, RAY, objetos vazios) lançam em GeometricExtents e são puladas.
    /// </summary>
    private static object? ModelExtents(Database db, Transaction tr, BlockTableRecord ms, int total)
    {
        if (total == 0) return null;
        if (total > MaxEntitiesForExtents)
        {
            if (db.Extmin.X > db.Extmax.X) return null;
            return new
            {
                min = P(db.Extmin), max = P(db.Extmax),
                source = "EXTMIN/EXTMAX",
                note = $"Desenho com mais de {MaxEntitiesForExtents} entidades: valores guardados pelo AutoCAD, " +
                       "que podem estar desatualizados (o usuário pode rodar ZOOM → Extents para atualizar).",
            };
        }

        bool any = false;
        var acc = new Extents3d();
        int skipped = 0;
        foreach (ObjectId id in ms)
        {
            if (tr.GetObject(id, OpenMode.ForRead) is not Entity ent || !ent.Visible) continue;
            try
            {
                Extents3d e = ent.GeometricExtents;
                if (any) acc.AddExtents(e); else { acc = e; any = true; }
            }
            catch { skipped++; }
        }
        if (!any) return null;
        return new
        {
            min = P(acc.MinPoint), max = P(acc.MaxPoint),
            size = new[] { R(acc.MaxPoint.X - acc.MinPoint.X), R(acc.MaxPoint.Y - acc.MinPoint.Y), R(acc.MaxPoint.Z - acc.MinPoint.Z) },
            source = "entities",
            skipped_without_extents = skipped,
        };
    }

    private static int Count(Transaction tr, ObjectId tableId)
    {
        int n = 0;
        foreach (ObjectId _ in (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead)) n++;
        return n;
    }

    // ---------- list_layers ----------
    public static object Layers(Document doc, bool includeCounts)
    {
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();

        Dictionary<ObjectId, int>? counts = null;
        if (includeCounts)
        {
            counts = new Dictionary<ObjectId, int>();
            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                if (!btr.IsLayout) continue;   // só Model e layouts de papel
                foreach (ObjectId eid in btr)
                    if (tr.GetObject(eid, OpenMode.ForRead) is Entity ent)
                        counts[ent.LayerId] = counts.TryGetValue(ent.LayerId, out int c) ? c + 1 : 1;
            }
        }

        var list = new List<object>();
        var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
        foreach (ObjectId id in lt)
        {
            var l = (LayerTableRecord)tr.GetObject(id, OpenMode.ForRead);
            string linetype = !l.LinetypeObjectId.IsNull && tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead) is LinetypeTableRecord ltr ? ltr.Name : "";
            list.Add(new
            {
                name = l.Name,
                current = id == db.Clayer,
                off = l.IsOff,
                frozen = l.IsFrozen,
                locked = l.IsLocked,
                plottable = l.IsPlottable,
                color = l.Color.ToString(),
                linetype,
                lineweight = Lw(l.LineWeight),
                xref_dependent = l.IsDependent,
                description = l.Description,
                entities = counts == null ? (int?)null : (counts.TryGetValue(id, out int c) ? c : 0),
            });
        }
        return new { count = list.Count, counts_scope = includeCounts ? "model + layouts" : "not computed", layers = list };
    }

    // ---------- list_blocks ----------
    public static object Blocks(Document doc)
    {
        Database db = doc.Database;
        using Transaction tr = db.TransactionManager.StartOpenCloseTransaction();
        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
        var list = new List<object>();
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)tr.GetObject(id, OpenMode.ForRead);
            if (btr.IsLayout || btr.IsAnonymous) continue;

            int refs = btr.GetBlockReferenceIds(true, false).Count;
            if (btr.IsDynamicBlock)
            {
                // Blocos dinâmicos modificados viram definições anônimas (*U...): somar as inserções delas.
                foreach (ObjectId anonId in btr.GetAnonymousBlockIds())
                {
                    var anon = (BlockTableRecord)tr.GetObject(anonId, OpenMode.ForRead);
                    refs += anon.GetBlockReferenceIds(true, false).Count;
                }
            }

            var attributes = new List<object>();
            if (btr.HasAttributeDefinitions)
                foreach (ObjectId eid in btr)
                    if (eid.ObjectClass.DxfName == "ATTDEF" && tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad)
                        attributes.Add(new { tag = ad.Tag, prompt = ad.Prompt, constant = ad.Constant, default_value = ad.TextString });

            list.Add(new
            {
                name = btr.Name,
                insertions = refs,
                is_dynamic = btr.IsDynamicBlock,
                is_xref = btr.IsFromExternalReference,
                description = btr.Comments,
                units = btr.Units.ToString(),
                attributes,
            });
        }
        return new { count = list.Count, blocks = list };
    }

    // ---------- get_selection ----------
    public static object Selection(Document doc, int maxItems)
    {
        PromptSelectionResult psr = doc.Editor.SelectImplied();
        if (psr.Status != PromptStatus.OK || psr.Value == null)
            return new { count = 0, note = "Nada selecionado. Selecione objetos no AutoCAD (sem comando ativo) e pergunte de novo.", items = Array.Empty<object>() };

        ObjectId[] ids = psr.Value.GetObjectIds();
        using Transaction tr = doc.Database.TransactionManager.StartOpenCloseTransaction();
        var items = new List<object>();
        foreach (ObjectId id in ids.Take(maxItems))
            if (tr.GetObject(id, OpenMode.ForRead) is Entity ent)
                items.Add(Describe(ent, tr));
        return new { count = ids.Length, returned = items.Count, truncated = ids.Length > items.Count, items };
    }

    private static Dictionary<string, object?> Describe(Entity ent, Transaction tr)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = ent.Handle.ToString(),
            ["type"] = ent.GetRXClass().DxfName,
            ["layer"] = ent.Layer,
            ["color"] = ent.Color.ToString(),
            ["linetype"] = ent.Linetype,
        };
        try { Extents3d e = ent.GeometricExtents; d["bounds"] = new { min = P(e.MinPoint), max = P(e.MaxPoint) }; }
        catch { d["bounds"] = null; }

        switch (ent)
        {
            case Line l:
                d["start"] = P(l.StartPoint); d["end"] = P(l.EndPoint);
                d["length"] = R(l.Length); d["angle_deg"] = Deg(l.Angle);
                break;
            case Circle c:
                d["center"] = P(c.Center); d["radius"] = R(c.Radius); d["diameter"] = R(c.Diameter);
                break;
            case Arc a:
                d["center"] = P(a.Center); d["radius"] = R(a.Radius);
                d["start_angle_deg"] = Deg(a.StartAngle); d["end_angle_deg"] = Deg(a.EndAngle); d["length"] = R(a.Length);
                break;
            case Polyline pl:
                d["closed"] = pl.Closed; d["vertices"] = pl.NumberOfVertices; d["length"] = R(pl.Length);
                if (pl.Closed) d["area"] = R(pl.Area);
                d["points"] = Enumerable.Range(0, Math.Min(pl.NumberOfVertices, 50))
                                        .Select(i => { Point2d p = pl.GetPoint2dAt(i); return new[] { R(p.X), R(p.Y) }; }).ToList();
                break;
            case BlockReference br:
                d["block"] = br.IsDynamicBlock && tr.GetObject(br.DynamicBlockTableRecord, OpenMode.ForRead) is BlockTableRecord dyn ? dyn.Name : br.Name;
                d["position"] = P(br.Position); d["rotation_deg"] = Deg(br.Rotation);
                d["scale"] = new[] { R(br.ScaleFactors.X), R(br.ScaleFactors.Y), R(br.ScaleFactors.Z) };
                var atts = new Dictionary<string, string>();
                foreach (ObjectId attId in br.AttributeCollection)
                    if (tr.GetObject(attId, OpenMode.ForRead) is AttributeReference ar) atts[ar.Tag] = ar.TextString;
                d["attributes"] = atts;
                if (br.IsDynamicBlock)
                {
                    var props = new Dictionary<string, string?>();
                    foreach (DynamicBlockReferenceProperty prop in br.DynamicBlockReferencePropertyCollection)
                        if (!string.IsNullOrEmpty(prop.PropertyName) && prop.PropertyName != "Origin")
                            props[prop.PropertyName] = prop.Value?.ToString();
                    d["dynamic_properties"] = props;
                }
                break;
            case DBText t:
                d["text"] = t.TextString; d["height"] = R(t.Height); d["position"] = P(t.Position); d["rotation_deg"] = Deg(t.Rotation);
                break;
            case MText mt:
                d["text"] = mt.Text; d["height"] = R(mt.TextHeight); d["position"] = P(mt.Location);
                break;
            case Dimension dim:
                d["measurement"] = R(dim.Measurement); d["text_override"] = dim.DimensionText; d["style"] = dim.DimensionStyleName;
                break;
            case Hatch h:
                d["pattern"] = h.PatternName;
                try { d["area"] = R(h.Area); } catch { d["area"] = null; }
                break;
        }
        return d;
    }

    // ---------- capture_view ----------
    // width/height pedidos funcionam como MÁXIMO: o AutoCAD preserva a proporção da janela,
    // então o bitmap pode sair menor (ex.: pedido 1280x800 → 1280x498). Devolvemos o tamanho REAL.
    public static object Capture(Document doc, uint width, uint height)
    {
        using Bitmap bmp = doc.CapturePreviewImage(width, height);
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        return new
        {
            mime_type = "image/png",
            width = bmp.Width,
            height = bmp.Height,
            requested_width = width,
            requested_height = height,
            base64 = Convert.ToBase64String(png.ToArray()),
        };
    }
}
