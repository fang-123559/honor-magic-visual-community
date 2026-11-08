using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MagicCommunity;

internal static class NativeSystemBarDefaults
{
    internal static bool IsToolbarMusic(string module) => module is "magic.music" or "magic.lyrics" or "magic.player";

    // Prefer the former lyric position. Preserve its id/section/order and every
    // unrelated module; the Dock keeps its own media player.
    internal static string ToolbarMusicOwner(JsonElement toolbar)
    {
        var rows=new[]{"left","center","right"}.SelectMany(s=>NativeSystemPanel.Array(NativeSystemPanel.Object(toolbar,"sections"),s))
            .Where(r=>NativeSystemPanel.Text(r,"kind")=="module"&&IsToolbarMusic(NativeSystemPanel.Text(r,"module")));
        return rows.OrderBy(r=>NativeSystemPanel.Text(r,"module") switch{"magic.music"=>0,"magic.lyrics"=>1,_=>2})
            .Select(r=>NativeSystemPanel.Text(r,"id")).FirstOrDefault()??"";
    }

    internal static void NormalizeToolbarMusic(JsonObject configuration)
    {
        if(configuration["toolbar"] is not JsonObject toolbar)return;
        string owner=ToolbarMusicOwner(JsonSerializer.SerializeToElement(toolbar));
        foreach(string section in new[]{"left","center","right"}){
            if(toolbar["sections"]?[section] is not JsonArray rows)continue;
            for(int i=rows.Count-1;i>=0;i--){var row=rows[i];
                if(row?["kind"]?.GetValue<string>()!="module"||!IsToolbarMusic(row["module"]?.GetValue<string>()??""))continue;
                if(row["id"]?.GetValue<string>()==owner)row["module"]="magic.music";else rows.RemoveAt(i);
            }
        }
    }

    internal static JsonObject CreateInstalledConfiguration()
    {
        static JsonObject Sections() => new() { ["left"] = new JsonArray(), ["center"] = new JsonArray(), ["right"] = new JsonArray() };
        static JsonObject Module(string id, string module) => new() { ["id"] = id, ["kind"] = "module", ["module"] = module };

        var dock = Sections();
        dock["left"]!.AsArray().Add(Module("default-dock-start", "magic.start"));
        dock["left"]!.AsArray().Add(new JsonObject { ["id"] = "default-dock-explorer", ["kind"] = "application",
            ["displayName"] = "文件资源管理器", ["target"] = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"),
            ["aumid"] = "", ["pinned"] = true, ["allowPin"] = true });
        dock["right"]!.AsArray().Add(Module("default-dock-recycle", "magic.recycle"));
        dock["right"]!.AsArray().Add(Module("default-dock-tray", "magic.tray"));
        var config = new JsonObject {
            ["protocol"] = "Magic.System.Configuration/1", ["schemaVersion"] = 1, ["revision"] = 1,
            ["toolbar"] = new JsonObject { ["sections"] = Sections() },
            ["dock"] = new JsonObject { ["locked"] = false, ["sections"] = dock, ["appearance"] = new JsonObject {
                ["enabled"] = true, ["edge"] = "Bottom", ["autoHide"] = "OnOverlap", ["outerMargin"] = 8, ["innerPadding"] = 8,
                ["iconSize"] = 40, ["itemGap"] = 8, ["widthMode"] = "MinContent", ["showDelayMs"] = 100, ["hideDelayMs"] = 800 } },
            ["preferences"] = new JsonObject { ["language"] = "zh-CN", ["reduceMotion"] = false }
        };
        RestoreToolbar(config, "toolbar");
        return config;
    }

    // Reset only the requested native surface. Keep pinned applications and
    // external compatibility entries in their original sections and order.
    internal static void RestoreToolbar(JsonObject configuration,string surface)
    {
        if(surface!="toolbar")throw new InvalidDataException("此入口仅恢复顶栏默认值。");
        var bar=configuration["toolbar"]!.AsObject();var previous=bar["sections"]!.AsObject();
        string[] sections=["left","center","right"];
        var modules=new Dictionary<string,JsonObject>(StringComparer.Ordinal);
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(string name in new[]{"toolbar","dock"})foreach(string group in sections)foreach(var node in configuration[name]!["sections"]![group]!.AsArray())ids.Add(node!["id"]!.GetValue<string>());
        foreach(string group in sections)foreach(var node in previous[group]!.AsArray())if(node?["kind"]?.GetValue<string>()=="module")modules.TryAdd(node["module"]!.GetValue<string>(),node.AsObject());
        string[][] defaults=[["magic.user"],["magic.clock"],["magic.tray","magic.audio","magic.keyboard","magic.bluetooth","magic.network","magic.battery","magic.notifications","magic.quick-settings","magic.power-menu"]];
        var next=new JsonObject();
        for(int i=0;i<sections.Length;i++){
            var rows=new JsonArray();next[sections[i]]=rows;
            foreach(string module in defaults[i]){
                if(modules.TryGetValue(module,out var old)){rows.Add(old.DeepClone());continue;}
                string id="default-toolbar-"+module;while(!ids.Add(id))id="default-toolbar-"+Guid.NewGuid().ToString("N");
                rows.Add(new JsonObject{["id"]=id,["kind"]="module",["module"]=module});
            }
            foreach(var node in previous[sections[i]]!.AsArray())if(node?["kind"]?.GetValue<string>() is "application" or "compatibility")rows.Add(node.DeepClone());
        }
        bar["sections"]=next;bar["locked"]=false;
        bar["appearance"]=new JsonObject{["enabled"]=true,["edge"]="Top",["autoHide"]="Never",["outerMargin"]=0,["innerPadding"]=8,["itemSize"]=16,["showDelayMs"]=100,["hideDelayMs"]=300};
    }
}
