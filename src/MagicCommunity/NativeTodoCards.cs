using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
namespace MagicCommunity;

internal sealed class NativeTodoCards : IDisposable
{
    private static readonly JsonSerializerOptions Wire=new(){Encoder=System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    internal sealed record Item(string Id,string Text,long Order,bool Done,long DoneAt,string Date="",string Priority="normal",JsonElement? Original=null);
    internal sealed record State(List<Item> Items,long NextOrder);
    private sealed record Edit(JsonElement Before,string After,string Description);
    private readonly LinkedList<Edit> history=new();private int historyBytes;
    private Window? editor;
    internal static bool IsTodo(string type)=>type is "system.todo" or "system.week";
    internal static string DateValue(string? text)=>text is {Length:10}&&DateOnly.TryParseExact(text,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out var date)&&date.Year is >=1900 and <=2199?text:"";
    internal static string PriorityValue(string? value)=>value is "urgent" or "relaxed"?value:"normal";
    internal static State Read(JsonElement settings){
        DesktopCards.ValidateSettings(settings);var items=new List<Item>();var ids=new HashSet<string>();long maximum=0;
        if(settings.TryGetProperty("items",out var values)){
            if(values.ValueKind!=JsonValueKind.Array||values.GetArrayLength()>512)throw new LayoutEditException("待办数据数量无效。");
            foreach(var value in values.EnumerateArray()){
                if(value.ValueKind!=JsonValueKind.Object||!value.TryGetProperty("id",out var identity)||identity.ValueKind is not (JsonValueKind.String or JsonValueKind.Number))throw new LayoutEditException("待办标识无效。");
                string id=identity.ValueKind==JsonValueKind.String?identity.GetString()??"":identity.GetRawText();
                if(!value.TryGetProperty("text",out var t)||t.ValueKind!=JsonValueKind.String||!value.TryGetProperty("order",out var o)||o.ValueKind!=JsonValueKind.Number||!o.TryGetInt64(out long order))throw new LayoutEditException("待办数据格式无效。");
                string text=t.GetString()??"";
                if(id.Length is <1 or >64||!ids.Add(id)||text.Length>8192||order<0||order>=9007199254740991)throw new LayoutEditException("待办数据格式无效。");
                bool done=value.TryGetProperty("done",out var d)&&d.ValueKind==JsonValueKind.True;
                long at=value.TryGetProperty("doneAt",out var time)&&time.ValueKind==JsonValueKind.Number&&time.TryGetInt64(out long timestamp)&&timestamp>0?timestamp:0;
                string Optional(string key)=>value.TryGetProperty(key,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
                items.Add(new(id,text,order,done,at,DateValue(Optional("date")),PriorityValue(Optional("priority")),value));maximum=Math.Max(maximum,order);
            }
        }
        long next=settings.TryGetProperty("nextOrder",out var n)&&n.ValueKind==JsonValueKind.Number&&n.TryGetInt64(out long supplied)&&supplied>maximum&&supplied<9007199254740991?supplied:maximum+1;
        return new(items,next);
    }
    internal static JsonElement Write(JsonElement original,IEnumerable<Item> items,long nextOrder){
        var node=JsonNode.Parse(original.GetRawText())!.AsObject();var values=new JsonArray();
        foreach(var item in items){
            var value=item.Original is {} old?JsonNode.Parse(old.GetRawText())!.AsObject():new JsonObject();
            value["id"]=item.Id;value["text"]=item.Text;value["order"]=item.Order;value["done"]=item.Done;value["doneAt"]=item.DoneAt;
            if(DateValue(item.Date).Length>0)value["date"]=item.Date;else value.Remove("date");
            if(PriorityValue(item.Priority)!="normal")value["priority"]=item.Priority;else value.Remove("priority");
            values.Add(value);
        }
        node["items"]=values;node["nextOrder"]=nextOrder;var result=JsonSerializer.SerializeToElement(node,Wire);Read(result);
        if(Encoding.UTF8.GetByteCount(result.GetRawText())>15000)throw new LayoutEditException("待办内容已达到卡片存储上限，请减少文字后保存。");return result;
    }
    internal static JsonElement Shared(LayoutState state){
        if(state.SharedTodos is {} shared){Read(shared);return shared;}
        var items=new List<Item>();var used=new HashSet<string>(StringComparer.Ordinal);long order=1;
        foreach(var board in state.Boards)foreach(var card in DesktopCards.Items(board).Where(c=>IsTodo(c.Type))){
            foreach(var item in Read(card.Settings).Items.OrderBy(i=>i.Order)){
                string id=item.Id;if(!used.Add(id)){id=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(board.Id+"/"+card.Id+"/"+item.Id)))[..32].ToLowerInvariant();if(!used.Add(id))throw new LayoutEditException("旧待办清单存在无法合并的标识冲突，原内容保留。");}
                items.Add(item with{Id=id,Order=order++});if(items.Count>512)throw new LayoutEditException("原有待办合计超过 512 项，尚未迁移；请先精简原清单。");
            }
        }
        return Write(JsonSerializer.SerializeToElement(new{schemaVersion=1}),items,order);
    }
    internal static void StoreShared(LayoutState state,JsonElement data){
        Read(data);if(Encoding.UTF8.GetByteCount(data.GetRawText())>15000)throw new LayoutEditException("共享待办超过存储上限。");
        state.SharedTodos=data.Clone();state.SchemaVersion=Math.Max(state.SchemaVersion,9);
        foreach(var card in state.Boards.SelectMany(DesktopCards.Items).Where(c=>IsTodo(c.Type))){
            var view=JsonNode.Parse(card.Settings.GetRawText())!.AsObject();view.Remove("items");view.Remove("nextOrder");view["source"]="shared-v1";card.Settings=JsonSerializer.SerializeToElement(view,Wire);
        }
    }
    private static string Fingerprint(JsonElement settings)=>JsonNode.Parse(settings.GetRawText())!.ToJsonString(Wire);
    private static DesktopCard Card(AppController controller,string board,string id){var card=DesktopCards.Require(controller.State.Boards.Single(b=>b.Id==board),id);if(!IsTodo(card.Type))throw new LayoutEditException("待办卡片已经变化。");return card;}
    private void Remember(Edit edit){history.AddLast(edit);historyBytes+=Encoding.UTF8.GetByteCount(edit.Before.GetRawText())+Encoding.UTF8.GetByteCount(edit.After);while((history.Count>40||historyBytes>2*1024*1024)&&history.First is {} first){historyBytes-=Encoding.UTF8.GetByteCount(first.Value.Before.GetRawText())+Encoding.UTF8.GetByteCount(first.Value.After);history.RemoveFirst();}}
    private void Apply(AppController controller,string board,string id,Func<JsonElement,JsonElement> change,string description){
        Card(controller,board,id);var before=Shared(controller.State);var after=change(before);if(Fingerprint(before)==Fingerprint(after))return;
        controller.Commit(state=>{if(Fingerprint(Shared(state))!=Fingerprint(before))throw new LayoutEditException("总清单已变化，请重试。");StoreShared(state,after);},description);
        Remember(new(before,Fingerprint(after),description));controller.SetStatus(description);
    }
    internal void Toggle(AppController controller,string board,string id,string itemId,bool done)=>Apply(controller,board,id,settings=>{
        var data=Read(settings);int index=data.Items.FindIndex(x=>x.Id==itemId);if(index<0)throw new LayoutEditException("待办事项已变化，请重新操作。");
        var item=data.Items[index];if(item.Done==done)return settings;data.Items[index]=item with{Done=done,DoneAt=done?DateTimeOffset.UtcNow.ToUnixTimeMilliseconds():0};return Write(settings,data.Items,data.NextOrder);
    },done?Read(Shared(controller.State)).Items.FirstOrDefault(x=>x.Id==itemId)?.Date.Length==0?"已完成 · 本周 7 天里的同一条全部移除":"已完成 · 只移除这一天":"已移回待办");
    internal void Clear(AppController controller,string board,string id)=>Apply(controller,board,id,settings=>{var data=Read(settings);return data.Items.Any(x=>x.Done)?Write(settings,data.Items.Where(x=>!x.Done),data.NextOrder):settings;},"清除已完成待办");
    internal void Undo(AppController controller,string board,string id){
        Card(controller,board,id);if(history.Last is not {} node)return;var entry=node.Value;
        if(Fingerprint(Shared(controller.State))!=entry.After)throw new LayoutEditException("总清单已由其他操作修改，不能覆盖撤销。");
        controller.Commit(state=>StoreShared(state,entry.Before),"已撤销："+entry.Description);
        historyBytes-=Encoding.UTF8.GetByteCount(entry.Before.GetRawText())+Encoding.UTF8.GetByteCount(entry.After);history.RemoveLast();controller.SetStatus("已撤销："+entry.Description);
    }
    internal object Projection(LayoutState state){
        JsonObject node;try{var shared=Shared(state);node=JsonNode.Parse(shared.GetRawText())!.AsObject();node["canUndo"]=history.Last is {} last&&last.Value.After==Fingerprint(shared);node["hasCompleted"]=Read(shared).Items.Any(i=>i.Done);}
        catch(Exception e)when(e is not OutOfMemoryException){node=new(){["error"]=e.Message,["items"]=new JsonArray(),["canUndo"]=false,["hasCompleted"]=false};}
        var today=DateOnly.FromDateTime(DateTime.Today);node["todayDate"]=today.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture);var days=new JsonArray();var first=today.AddDays(-(int)today.DayOfWeek);var lunar=new ChineseLunisolarCalendar();
        for(int i=0;i<7;i++){var date=first.AddDays(i);string label="";try{var time=date.ToDateTime(TimeOnly.MinValue);int d=lunar.GetDayOfMonth(time);label=d==10?"初十":d==20?"二十":d==30?"三十":(d<10?"初":d<20?"十":"廿")+"一二三四五六七八九"[(d-1)%10];if(d==1){int m=lunar.GetMonth(time),leap=lunar.GetLeapMonth(lunar.GetYear(time));label=(m==leap?"闰":"")+"正二三四五六七八九十冬腊"[m-(leap>0&&m>=leap?2:1)]+"月";}}catch(ArgumentOutOfRangeException){}
            days.Add(new JsonObject{["date"]=date.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture),["day"]=date.Day,["weekday"]=(int)date.DayOfWeek,["lunar"]=label});}
        node["weekDays"]=days;return node;
    }
    private static string PriorityLabel(string priority)=>priority=="urgent"?"紧急":priority=="relaxed"?"宽松":"正常";
    private static Brush PriorityBrush(string priority)=>new SolidColorBrush(priority=="urgent"?Color.FromRgb(192,69,60):priority=="relaxed"?Color.FromRgb(58,107,191):Color.FromRgb(63,143,94));
    internal void Show(AppController controller,string board,string id,bool completed=false,string focus="",string date=""){
        Card(controller,board,id);date=DateValue(date);if(editor is {} old){old.Activate();return;}
        var window=new CommunityWindow{Title="待办总清单",Width=540,Height=560,MinWidth=420,MinHeight=360,ShowInTaskbar=false,WindowStartupLocation=WindowStartupLocation.CenterScreen,FontFamily=CommunityFont.Family,FontSize=12};
        editor=window;var theme=new NativeWpfTheme();theme.Apply(window);window.Background=theme.Background;window.Foreground=theme.Ink;
        var root=new DockPanel{Margin=new Thickness(18)};window.Content=root;var top=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(top,Dock.Top);root.Children.Add(top);
        var bottom=new StackPanel();DockPanel.SetDock(bottom,Dock.Bottom);root.Children.Add(bottom);
        var list=new StackPanel();var scroll=new ScrollViewer{Content=list,VerticalScrollBarVisibility=ScrollBarVisibility.Hidden,Margin=new Thickness(0,12,0,12)};root.Children.Add(scroll);
        var error=new TextBlock{TextWrapping=TextWrapping.Wrap,Foreground=theme.Muted,Margin=new(0,8,0,8)};
        bool doneView=completed,building=false,rebuildPending=false;string addPriority="normal";var pending=new Dictionary<string,string>();Action? rebuild=null;Action? flush=null;
        void Attempt(Action action){try{action();error.Text="";}catch(Exception e)when(e is not OutOfMemoryException){error.Text=e.Message;}}
        void Change(string itemId,Func<Item,Item?> edit,string description)=>Apply(controller,board,id,settings=>{var data=Read(settings);int at=data.Items.FindIndex(x=>x.Id==itemId);if(at<0)throw new LayoutEditException("条目已变化。");var next=edit(data.Items[at]);if(next is null)data.Items.RemoveAt(at);else data.Items[at]=next;return Write(settings,data.Items,data.NextOrder);},description);
        void SaveText(string itemId){if(!pending.Remove(itemId,out string? text))return;Attempt(()=>{if(string.IsNullOrWhiteSpace(text))throw new LayoutEditException("待办文字不能为空。");Change(itemId,item=>item with{Text=text.Trim()},"修改待办文字");});}
        var addRow=new DockPanel();bottom.Children.Add(addRow);var addButton=new Button{Content="添加",Padding=new(10,5,10,5),Margin=new(6,0,0,0)};DockPanel.SetDock(addButton,Dock.Right);addRow.Children.Add(addButton);
        var priority=new StackPanel{Orientation=Orientation.Horizontal,Margin=new(0,0,6,0),VerticalAlignment=VerticalAlignment.Center};DockPanel.SetDock(priority,Dock.Left);addRow.Children.Add(priority);
        var priorityDots=new Dictionary<string,Button>();var priorityText=new TextBlock{Width=30,FontSize=10,VerticalAlignment=VerticalAlignment.Center,Margin=new(3,0,0,0)};
        void SetPriority(string value){addPriority=value;priorityText.Text=PriorityLabel(value);foreach(var pair in priorityDots){pair.Value.FontSize=pair.Key==value?18:12;pair.Value.Opacity=pair.Key==value?1:.42;}}
        void Cycle(int direction){string[] choices=["normal","urgent","relaxed"];SetPriority(choices[(Array.IndexOf(choices,addPriority)+direction+3)%3]);}
        foreach(string value in new[]{"normal","urgent","relaxed"}){var dot=new Button{Content="●",Width=17,Height=30,Padding=new(0),BorderThickness=new(0),Background=Brushes.Transparent,Foreground=PriorityBrush(value),ToolTip=PriorityLabel(value)};priorityDots[value]=dot;priority.Children.Add(dot);dot.Click+=(_,e)=>{SetPriority(value);e.Handled=true;};}
        priority.Children.Add(priorityText);priorityText.MouseLeftButtonUp+=(_,_)=>Cycle(1);priority.PreviewMouseWheel+=(_,e)=>{Cycle(e.Delta>=0?-1:1);e.Handled=true;};SetPriority("normal");
        var add=new TextBox{MaxLength=8192,MinHeight=30,ToolTip=date.Length==0?"添加不分日期的待办":$"添加 {date} 的待办"};addRow.Children.Add(add);bottom.Children.Add(error);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};bottom.Children.Add(actions);var undo=new Button{Content="撤销",Margin=new(0,0,8,0),Padding=new(12,6,12,6)};var close=new Button{Content="完成",IsCancel=true,Padding=new(16,6,16,6)};actions.Children.Add(undo);actions.Children.Add(close);
        void QueueBuild(){if(rebuildPending||!window.IsLoaded)return;rebuildPending=true;window.Dispatcher.BeginInvoke(new Action(()=>{rebuildPending=false;rebuild?.Invoke();}));}
        rebuild=()=>{
            if(building)return;building=true;double offset=scroll.VerticalOffset;string selected=(Keyboard.FocusedElement as TextBox)?.Tag as string??"";int caret=(Keyboard.FocusedElement as TextBox)?.CaretIndex??0;list.Children.Clear();
            try{
                var data=Read(Shared(controller.State));undo.IsEnabled=history.Count>0;var entries=(doneView?data.Items.Where(x=>x.Done).OrderByDescending(x=>x.DoneAt):data.Items.Where(x=>!x.Done).OrderBy(x=>x.Order)).ToArray();
                foreach(var item in entries){
                    var row=new DockPanel{Margin=new(0,0,0,8)};var check=new CheckBox{IsChecked=item.Done,VerticalAlignment=VerticalAlignment.Center,Margin=new(0,0,7,0)};DockPanel.SetDock(check,Dock.Left);row.Children.Add(check);
                    var mark=new Button{Content="▌",Foreground=PriorityBrush(item.Priority),Width=20,Padding=new(0),Margin=new(0,0,5,0),ToolTip=PriorityLabel(item.Priority)};DockPanel.SetDock(mark,Dock.Left);row.Children.Add(mark);
                    var remove=new Button{Content="×",Width=26,Margin=new(5,0,0,0),ToolTip="删除待办"};DockPanel.SetDock(remove,Dock.Right);row.Children.Add(remove);
                    var picker=new DatePicker{Width=110,Margin=new(5,0,0,0),SelectedDate=item.Date.Length>0?DateTime.ParseExact(item.Date,"yyyy-MM-dd",CultureInfo.InvariantCulture):null,ToolTip="不选日期表示每天；可清空日期"};DockPanel.SetDock(picker,Dock.Right);row.Children.Add(picker);
                    var text=new TextBox{Text=pending.GetValueOrDefault(item.Id,item.Text),MaxLength=8192,MinHeight=30,Tag=item.Id};row.Children.Add(text);list.Children.Add(row);
                    text.TextChanged+=(_,_)=>{if(!building)pending[item.Id]=text.Text;};text.LostKeyboardFocus+=(_,_)=>{if(!building)SaveText(item.Id);};
                    check.Click+=(_,_)=>Attempt(()=>Toggle(controller,board,id,item.Id,check.IsChecked==true));
                    remove.Click+=(_,_)=>Attempt(()=>{pending.Remove(item.Id);Change(item.Id,_=>null,"删除「"+item.Text+"」");});
                    mark.Click+=(_,_)=>Attempt(()=>Change(item.Id,current=>current with{Priority=current.Priority=="normal"?"urgent":current.Priority=="urgent"?"relaxed":"normal"},"修改「"+item.Text+"」优先级"));
                    picker.SelectedDateChanged+=(_,_)=>{if(!building)Attempt(()=>Change(item.Id,current=>current with{Date=DateValue(picker.SelectedDate?.ToString("yyyy-MM-dd",CultureInfo.InvariantCulture))},"修改待办日期"));};
                    if(item.Id==(selected.Length>0?selected:focus)){text.Loaded+=(_,_)=>{text.Focus();text.CaretIndex=Math.Min(caret>0?caret:text.Text.Length,text.Text.Length);};}
                }
                if(entries.Length==0)list.Children.Add(new TextBlock{Text=doneView?"还没有已完成事项":"还没有待办事项",Margin=new(0,16,0,16),Foreground=theme.Muted});
                error.Text=$"{data.Items.Count(x=>!x.Done)} 项待办 · 新条目"+(date.Length>0?"属于 "+date:"不分日期（每天）");
            }catch(Exception e)when(e is not OutOfMemoryException){error.Text=e.Message;}finally{building=false;focus="";window.Dispatcher.BeginInvoke(new Action(()=>scroll.ScrollToVerticalOffset(offset)));}
        };
        flush=()=>{foreach(string itemId in pending.Keys.ToArray())SaveText(itemId);};
        foreach(bool view in new[]{false,true}){var button=new Button{Content=view?"已完成":"待办",Padding=new(12,6,12,6),Margin=new(0,0,8,0)};top.Children.Add(button);button.Click+=(_,_)=>{flush();doneView=view;rebuild();};}
        var clear=new Button{Content="清除已完成",Padding=new(8,6,8,6)};top.Children.Add(clear);clear.Click+=(_,_)=>Attempt(()=>Clear(controller,board,id));
        void Add(){if(string.IsNullOrWhiteSpace(add.Text))return;string text=add.Text.Trim();Attempt(()=>Apply(controller,board,id,settings=>{var data=Read(settings);if(data.Items.Count>=512)throw new LayoutEditException("待办数量已达到上限。");return Write(settings,data.Items.Append(new(Guid.NewGuid().ToString("N"),text,data.NextOrder,false,0,date,addPriority)),data.NextOrder+1);},"添加「"+text+"」"));if(error.Text.Length==0){add.Clear();doneView=false;QueueBuild();add.Focus();}}
        addButton.Click+=(_,_)=>Add();add.KeyDown+=(_,e)=>{if(e.Key==Key.Enter){e.Handled=true;Add();}};
        undo.Click+=(_,_)=>Attempt(()=>Undo(controller,board,id));close.Click+=(_,_)=>{flush();window.Close();};
        window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Z&&Keyboard.Modifiers==ModifierKeys.Control&&Keyboard.FocusedElement is not TextBox){e.Handled=true;Attempt(()=>Undo(controller,board,id));}};
        void Changed()=>QueueBuild();controller.Changed+=Changed;
        window.Closed+=(_,_)=>{controller.Changed-=Changed;editor=null;pending.Clear();};rebuild();window.Show();window.Activate();
    }
    public void Dispose(){if(editor is {} window)window.Dispatcher.BeginInvoke(new Action(window.Close));editor=null;history.Clear();historyBytes=0;}
}
