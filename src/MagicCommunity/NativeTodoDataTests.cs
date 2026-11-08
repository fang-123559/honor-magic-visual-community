using System.IO;
using System.Text.Json;
namespace MagicCommunity;
internal static class NativeTodoDataTests{
    internal static void Run(string root,Action<string,bool> check){
        var empty=JsonSerializer.SerializeToElement(new{});
        var a=JsonSerializer.SerializeToElement(new{items=new[]{new{id="same",text="每一天",order=3,done=false,doneAt=0,extension="preserve"}},nextOrder=4,viewPreference="keep"});
        var b=JsonSerializer.SerializeToElement(new{items=new[]{new{id="same",text="指定日期",order=1,done=false,doneAt=0,date="2026-11-04",priority="urgent"}},nextOrder=2});
        var catalog=DesktopCardCatalog.Load(Path.Combine(root,"widgets/desktop/card-catalog.json"));
        var state=new LayoutState{SchemaVersion=8,Boards=[new(){Id="todo-a",Name="自有甲",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}},new(){Id="todo-b",Name="自有乙",Grid=new(){Columns=10,Rows=8,NativeInitialized=true}}]};
        var first=DesktopCards.Add(state,state.Boards[0],catalog,"system.todo",4,2,a);
        var second=DesktopCards.Add(state,state.Boards[1],catalog,"system.todo",4,2,b);
        string original=JsonSerializer.Serialize(state,StateStore.Json);
        var shared=NativeTodoCards.Shared(state);var data=NativeTodoCards.Read(shared);
        check("shared todo merges old lists without modifying them",data.Items.Count==2&&original==JsonSerializer.Serialize(state,StateStore.Json));
        check("shared todo remaps duplicate legacy identity and keeps both entries",data.Items.Select(i=>i.Id).Distinct().Count()==2&&data.Items.Select(i=>i.Text).SequenceEqual(new[]{"每一天","指定日期"}));
        check("shared todo duplicate remapping is deterministic",shared.GetRawText()==NativeTodoCards.Shared(state).GetRawText());
        check("todo optional date validation preserves leap dates",NativeTodoCards.DateValue("2024-02-29")=="2024-02-29"&&NativeTodoCards.DateValue("2023-02-29")==""&&NativeTodoCards.DateValue("2200-01-01")=="");
        var invalid=JsonSerializer.SerializeToElement(new{items=new[]{new{id="i",text="保留",order=1,date="bad",priority="strange",extension="keep"}},nextOrder=2});
        var parsed=NativeTodoCards.Read(invalid);var normalized=NativeTodoCards.Write(invalid,parsed.Items,parsed.NextOrder);
        var row=normalized.GetProperty("items")[0];check("bad optional fields removed without dropping item or unknown fields",row.GetProperty("text").GetString()=="保留"&&row.GetProperty("extension").GetString()=="keep"&&!row.TryGetProperty("date",out _)&&!row.TryGetProperty("priority",out _));
        string directory=Path.Combine(root,"artifacts/todo-migration-"+Guid.NewGuid().ToString("N"));var store=new StateStore(directory);store.Save(state);byte[] before=File.ReadAllBytes(store.FilePath);
        NativeTodoCards.StoreShared(state,shared);store.Save(state);
        check("shared todo migration creates exact immutable pre-v9 backup",Directory.GetFiles(directory,"*.before-shared-todo-v9-*.json").Any(file=>File.ReadAllBytes(file).SequenceEqual(before)));
        check("migration leaves only one durable task source",state.SharedTodos.HasValue&&state.SchemaVersion==9&&state.Boards.SelectMany(DesktopCards.Items).All(c=>!c.Settings.TryGetProperty("items",out _)&&c.Settings.GetProperty("source").GetString()=="shared-v1")&&first.Settings.GetProperty("viewPreference").GetString()=="keep");
        var loaded=new StateStore(directory).Load();check("shared todo survives save and reload",loaded.SharedTodos.HasValue&&NativeTodoCards.Read(loaded.SharedTodos.Value).Items.Count==2);
        var changed=data.Items.Select(i=>i with{Done=true}).ToArray();var next=NativeTodoCards.Write(shared,changed,data.NextOrder);NativeTodoCards.StoreShared(loaded,next);check("one edit updates authoritative source without daily copies",NativeTodoCards.Read(loaded.SharedTodos!.Value).Items.All(i=>i.Done)&&NativeTodoCards.Read(loaded.SharedTodos!.Value).Items.Count==2);
        bool refused=false;try{NativeTodoCards.Write(empty,[new("big",new string('汉',8192),1,false,0)],2);}catch(LayoutEditException){refused=true;}check("shared UTF8 storage bound is enforced",refused);
        var many=JsonSerializer.SerializeToElement(new{items=Enumerable.Range(0,300).Select(i=>new{id="i"+i,text="x",order=i+1}),nextOrder=301});
        var tooMany=new LayoutState{SchemaVersion=8,Boards=[new(){Id="a",Cards=[new(){Type="system.todo",Settings=many},new(){Type="system.todo",Settings=many}]}]};string preserved=JsonSerializer.Serialize(tooMany,StateStore.Json);refused=false;try{NativeTodoCards.Shared(tooMany);}catch(LayoutEditException){refused=true;}
        check("oversized merge refuses without truncating or migrating",refused&&tooMany.SharedTodos is null&&preserved==JsonSerializer.Serialize(tooMany,StateStore.Json));
    }
}
