using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using UXLabelshop.Models;
using UXLabelshop.Rendering;

namespace UXLabelshop.Services;

public record TemplateListItem(long Id, string Code, string Name, string Status, string Content)
{
    public override string ToString() => Name;
}

public record TemplateRow(long Id, string Code, string Name, decimal WidthMm, decimal HeightMm,
    string Status, DateTime UpdatedAt);

public class TemplateService
{
    private readonly IServiceProvider _sp;

    public TemplateService(IServiceProvider sp) => _sp = sp;

    /// <summary>已发布模板（扫码打印用）。</summary>
    public List<TemplateListItem> GetPublished()
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var query = from t in db.LabelTemplates
                    join v in db.TemplateVers on t.CurrentVerId equals v.Id
                    where t.Status == "published"
                    orderby t.Code
                    select new TemplateListItem(t.Id, t.Code, t.Name, t.Status, v.Content);
        return query.ToList();
    }

    /// <summary>全部模板（模板管理用）。</summary>
    public List<TemplateListItem> GetAll()
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var query = from t in db.LabelTemplates
                    join v in db.TemplateVers on t.CurrentVerId equals v.Id into vs
                    from v in vs.DefaultIfEmpty()
                    orderby t.Code
                    select new TemplateListItem(t.Id, t.Code, t.Name, t.Status, v != null ? v.Content : "");
        return query.ToList();
    }

    /// <summary>
    /// 加载模板：画布取当前版本 content，变量一律以 label_template_var 子表为准。
    /// 子表尚无记录（新模板/迁移前）时回退到 content 里内嵌的 variables。
    /// </summary>
    public TemplateDoc LoadDoc(long templateId)
    {
        var item = GetAll().FirstOrDefault(t => t.Id == templateId);
        if (item == null) throw new InvalidOperationException("模板不存在");
        if (string.IsNullOrWhiteSpace(item.Content)) throw new InvalidOperationException("模板没有内容版本");
        var doc = TemplateDoc.Parse(item.Content);

        // 版本 content 里的 name/id 是历史快照，可能与 label_template 表的权威值不一致
        // （早期复制模板时直接照抄了源模板 content，编辑器会显示旧名字，一保存还会把表名改回旧值）。
        // 这里一律以表为准，顺带修复已存在的旧副本。
        doc.Name = item.Name;
        doc.Id = item.Code;

        // 子表是唯一权威来源：迁移在启动时已完成；新模板本来就没有变量。
        doc.Variables = GetVariables(templateId);
        return doc;
    }

    /// <summary>变量列表（子表权威），按 sort_no 排序。</summary>
    public List<TemplateVar> GetVariables(long templateId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        return GetVariablesWithin(db, templateId);
    }

    private static List<TemplateVar> GetVariablesWithin(AppDbContext db, long templateId) =>
        db.TemplateVars
            .Where(v => v.TemplateId == templateId)
            .OrderBy(v => v.SortNo).ThenBy(v => v.Id)
            .ToList()
            .Select(ToTemplateVar)
            .ToList();

    /// <summary>
    /// 全量替换某模板的变量定义（新增/编辑/删除/排序共用）。
    /// 只写子表，不动画布 content；版本快照与打印快照仍会内嵌当时的完整变量。
    /// </summary>
    public void SaveVariables(long templateId, List<TemplateVar> vars)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var old = db.TemplateVars.Where(v => v.TemplateId == templateId).ToList();
        db.TemplateVars.RemoveRange(old);

        for (var i = 0; i < vars.Count; i++)
        {
            var v = vars[i];
            if (string.IsNullOrWhiteSpace(v.Key)) continue;
            db.TemplateVars.Add(new LabelTemplateVar
            {
                TemplateId = templateId,
                Key = v.Key.Trim(),
                Label = v.Label,
                Short = v.Short,
                Source = string.IsNullOrWhiteSpace(v.Source) ? "scan" : v.Source!.Trim(),
                DefaultValue = v.DefaultValue,
                Formula = v.Formula,
                Sample = v.Sample,
                Value = v.Value,
                SortNo = i,
                ExtraJson = SerializeExtra(v.Extra),
            });
        }
        db.SaveChanges();
    }

    private static string? SerializeExtra(Dictionary<string, JsonElement>? extra)
    {
        if (extra == null || extra.Count == 0) return null;
        return JsonSerializer.Serialize(extra, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static TemplateVar ToTemplateVar(LabelTemplateVar e)
    {
        var v = new TemplateVar
        {
            Key = e.Key,
            Label = e.Label,
            Short = e.Short,
            Source = e.Source,
            DefaultValue = e.DefaultValue,
            Formula = e.Formula,
            Sample = e.Sample,
            Value = e.Value,
            SortNo = e.SortNo,
        };
        if (!string.IsNullOrWhiteSpace(e.ExtraJson))
        {
            try
            {
                v.Extra = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(e.ExtraJson);
            }
            catch (JsonException) { }
        }
        return v;
    }

    public List<TemplateRow> GetRows()
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        return db.LabelTemplates
            .OrderBy(t => t.Code)
            .Select(t => new TemplateRow(t.Id, t.Code, t.Name, t.WidthMm, t.HeightMm, t.Status, t.UpdatedAt))
            .ToList();
    }

    public string LoadContent(long templateId)
    {
        var item = GetAll().FirstOrDefault(t => t.Id == templateId)
                   ?? throw new InvalidOperationException("模板不存在");
        return item.Content;
    }

    public void SetStatus(long templateId, string status)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var t = db.LabelTemplates.Single(x => x.Id == templateId);
        t.Status = status;
        db.SaveChanges();
    }

    /// <summary>新建空白模板（草稿 + 首个版本）。</summary>
    public long CreateTemplate(string code, string name, double widthMm, double heightMm, int dpi,
        long? userId, string? remark = null)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        if (db.LabelTemplates.Any(t => t.Code == code))
            throw new InvalidOperationException($"模板编码 {code} 已存在");

        var doc = new TemplateDoc
        {
            Id = code,
            WidthMm = widthMm,
            HeightMm = heightMm,
            Dpi = dpi,
            Name = name,
        };
        doc.Elements.Add(new TemplateElement
        {
            Type = "text", X = 2, Y = 2, W = 30, H = 5, Text = "文本", SizeMm = 3, Align = "left", Valign = "middle",
        });

        var tpl = new LabelTemplate
        {
            Code = code,
            Name = name,
            WidthMm = (decimal)widthMm,
            HeightMm = (decimal)heightMm,
            Dpi = (short)dpi,
            Status = "draft",
            Source = "manual",
            Remark = remark,
            CreatedBy = userId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        db.LabelTemplates.Add(tpl);
        db.SaveChanges();

        var ver = new LabelTemplateVer
        {
            TemplateId = tpl.Id,
            Ver = 1,
            Content = doc.ToJson(),
            ChangeNote = "新建模板",
            CreatedBy = userId,
            CreatedAt = DateTime.Now,
        };
        db.TemplateVers.Add(ver);
        db.SaveChanges();

        tpl.CurrentVerId = ver.Id;
        db.SaveChanges();

        return tpl.Id;
    }

    /// <summary>复制模板（内容原样复制，状态 draft）。</summary>
    public long CopyTemplate(long sourceId, string newCode, string newName, long? userId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var src = db.LabelTemplates.Single(t => t.Id == sourceId);
        if (db.LabelTemplates.Any(t => t.Code == newCode))
            throw new InvalidOperationException($"模板编码 {newCode} 已存在");

        var content = LoadContent(sourceId);
        // 复制的是「画布」，但 content 里还内嵌着源模板的 name/id，必须换成新模板的，
        // 否则编辑器打开副本显示的是旧名字，一保存还会把 label_template.name 改回旧值。
        var copied = TemplateDoc.Parse(content);
        copied.Name = newName;
        copied.Id = newCode;
        content = copied.ToJson();

        var srcVars = GetVariables(sourceId);
        var tpl = new LabelTemplate
        {
            Code = newCode,
            Name = newName,
            WidthMm = src.WidthMm,
            HeightMm = src.HeightMm,
            Dpi = src.Dpi,
            Status = "draft",
            Source = src.Source,
            SourceFile = src.SourceFile,
            Remark = $"复制自 {src.Code}",
            CreatedBy = userId,
            CreatedAt = DateTime.Now,
            UpdatedAt = DateTime.Now,
        };
        db.LabelTemplates.Add(tpl);
        db.SaveChanges();

        var ver = new LabelTemplateVer
        {
            TemplateId = tpl.Id,
            Ver = 1,
            Content = content,
            ChangeNote = $"复制自 {src.Code}",
            CreatedBy = userId,
            CreatedAt = DateTime.Now,
        };
        db.TemplateVers.Add(ver);
        db.SaveChanges();
        tpl.CurrentVerId = ver.Id;
        db.SaveChanges();

        // 变量定义一并复制到新模板（子表权威），否则复制出来的模板会丢变量
        foreach (var v in srcVars)
            db.TemplateVars.Add(new LabelTemplateVar
            {
                TemplateId = tpl.Id,
                Key = v.Key,
                Label = v.Label,
                Short = v.Short,
                Source = v.Source,
                DefaultValue = v.DefaultValue,
                Formula = v.Formula,
                Sample = v.Sample,
                Value = v.Value,
                SortNo = v.SortNo,
                ExtraJson = SerializeExtra(v.Extra),
            });
        db.SaveChanges();
        return tpl.Id;
    }

    /// <summary>
    /// 保存新版本（版本号 +1 并设为当前版本）。
    /// 画布由 doc 提供；版本快照内嵌「子表当前变量」以保证历史可复现，
    /// 但不反向写子表（变量唯一写入口是 SaveVariables）。
    /// </summary>
    public void SaveVersion(long templateId, TemplateDoc doc, long? userId, string? note = null)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var tpl = db.LabelTemplates.Single(t => t.Id == templateId);
        var maxVer = db.TemplateVers.Where(v => v.TemplateId == templateId).Max(v => (int?)v.Ver) ?? 0;

        var originalVars = doc.Variables;
        string content;
        try
        {
            doc.Variables = GetVariablesWithin(db, templateId);
            content = doc.ToJson();
        }
        finally
        {
            doc.Variables = originalVars;
        }

        var ver = new LabelTemplateVer
        {
            TemplateId = templateId,
            Ver = maxVer + 1,
            Content = content,
            ChangeNote = note,
            CreatedBy = userId,
            CreatedAt = DateTime.Now,
        };
        db.TemplateVers.Add(ver);
        db.SaveChanges();

        tpl.CurrentVerId = ver.Id;
        tpl.WidthMm = (decimal)doc.WidthMm;
        tpl.HeightMm = (decimal)doc.HeightMm;
        tpl.Dpi = (short)doc.Dpi;
        tpl.UpdatedAt = DateTime.Now;
        if (!string.IsNullOrEmpty(doc.Name)) tpl.Name = doc.Name!;
        db.SaveChanges();
    }

    /// <summary>列出某变量被引用的位置（画布正文 / 扫码规则 / 其他变量公式），供删除前确认。</summary>
    public List<string> FindVariableReferences(long templateId, string key)
    {
        var refs = new List<string>();
        var doc = LoadDoc(templateId);
        foreach (var el in doc.Elements)
        {
            var hit = CountRefs(el.Text, key) + CountRefs(el.Src, key);
            if (hit > 0) refs.Add($"画布 {el.Type} 元素「{Short(el.Text ?? el.Src)}」（{hit} 处）");
        }
        foreach (var v in doc.Variables)
        {
            if (v.Key == key || !v.IsComputed) continue;
            if (FormulaEngine.Dependencies(v.Formula).Contains(key, StringComparer.OrdinalIgnoreCase))
                refs.Add($"变量 {v.Key} 的公式");
        }

        using var db = _sp.GetRequiredService<AppDbContext>();
        foreach (var r in db.ScanRules.Where(x => x.TemplateId == templateId).ToList())
        {
            var hit = CountJsonValue(r.Mapping, key) + CountJsonValue(r.SeqMap, key);
            if (hit > 0) refs.Add($"扫码规则「{r.Name}」映射（{hit} 处）");
        }
        return refs;
    }

    /// <summary>
    /// 改变量 key 时级联替换：画布正文 {{旧}}→{{新}}、扫码规则映射里的变量 key。
    /// 其他变量公式的引用由调用方（变量管理窗口）在内存中一并改好后统一 SaveVariables。
    /// 画布发生变化会生成一个新版本（快照）。返回被替换的处数。
    /// </summary>
    public int ApplyVariableRename(long templateId, string oldKey, string newKey, long? userId)
    {
        var total = 0;
        var doc = LoadDoc(templateId);
        var canvasChanged = false;
        foreach (var el in doc.Elements)
        {
            if (!string.IsNullOrEmpty(el.Text))
            {
                var next = VariableResolver.RenameRefs(el.Text, oldKey, newKey);
                if (next != el.Text) { el.Text = next; canvasChanged = true; total++; }
            }
            if (!string.IsNullOrEmpty(el.Src))
            {
                var next = VariableResolver.RenameRefs(el.Src, oldKey, newKey);
                if (next != el.Src) { el.Src = next; canvasChanged = true; total++; }
            }
        }
        if (canvasChanged)
            SaveVersion(templateId, doc, userId, $"变量 {oldKey} → {newKey}（正文引用同步）");

        using var db = _sp.GetRequiredService<AppDbContext>();
        foreach (var r in db.ScanRules.Where(x => x.TemplateId == templateId).ToList())
        {
            var changed = false;
            var mapping = r.Mapping;
            var seqMap = r.SeqMap;
            if (ReplaceJsonValue(ref mapping, oldKey, newKey)) { r.Mapping = mapping; changed = true; total++; }
            if (ReplaceJsonValue(ref seqMap, oldKey, newKey)) { r.SeqMap = seqMap; changed = true; total++; }
            if (changed) r.UpdatedAt = DateTime.Now;
        }
        db.SaveChanges();
        return total;
    }

    private static int CountRefs(string? text, string key)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        return Regex.Matches(text, @"\{\{\s*" + Regex.Escape(key) + @"\s*\}\}", RegexOptions.IgnoreCase).Count;
    }

    private static string Short(string? s)
    {
        var t = (s ?? "").Replace("\n", " ").Trim();
        return t.Length > 24 ? t[..24] + "…" : t;
    }

    private static int CountJsonValue(string? json, string key)
    {
        if (string.IsNullOrWhiteSpace(json)) return 0;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            return node == null ? 0 : CountNode(node, key);
        }
        catch { return 0; }
    }

    private static int CountNode(System.Text.Json.Nodes.JsonNode node, string key)
    {
        var n = 0;
        if (node is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var s) &&
            string.Equals(s, key, StringComparison.OrdinalIgnoreCase)) n++;
        else if (node is System.Text.Json.Nodes.JsonObject jo)
            foreach (var kv in jo) if (kv.Value != null) n += CountNode(kv.Value, key);
        else if (node is System.Text.Json.Nodes.JsonArray ja)
            foreach (var x in ja) if (x != null) n += CountNode(x, key);
        return n;
    }

    private static bool ReplaceJsonValue(ref string? json, string oldKey, string newKey)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;
        try
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json);
            if (node == null) return false;
            var changed = ReplaceNode(node, oldKey, newKey);
            if (changed) json = node.ToJsonString();
            return changed;
        }
        catch { return false; }
    }

    private static bool ReplaceNode(System.Text.Json.Nodes.JsonNode node, string oldKey, string newKey)
    {
        var changed = false;
        if (node is System.Text.Json.Nodes.JsonObject jo)
        {
            foreach (var kv in jo.ToList())
            {
                if (kv.Value is System.Text.Json.Nodes.JsonValue jv && jv.TryGetValue<string>(out var s) &&
                    string.Equals(s, oldKey, StringComparison.OrdinalIgnoreCase))
                {
                    jo[kv.Key] = newKey;
                    changed = true;
                }
                else if (kv.Value != null && ReplaceNode(kv.Value, oldKey, newKey)) changed = true;
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray ja)
        {
            for (var i = 0; i < ja.Count; i++)
                if (ja[i] != null && ReplaceNode(ja[i]!, oldKey, newKey)) changed = true;
        }
        return changed;
    }

    /// <summary>仅草稿模板可删除。</summary>
    public bool DeleteDraft(long templateId)
    {
        using var db = _sp.GetRequiredService<AppDbContext>();
        var tpl = db.LabelTemplates.Single(t => t.Id == templateId);
        if (tpl.Status != "draft") return false;

        var vers = db.TemplateVers.Where(v => v.TemplateId == templateId).ToList();
        db.TemplateVers.RemoveRange(vers);
        var vars = db.TemplateVars.Where(v => v.TemplateId == templateId).ToList();
        db.TemplateVars.RemoveRange(vars);
        db.SaveChanges();
        db.LabelTemplates.Remove(tpl);
        db.SaveChanges();
        return true;
    }
}
