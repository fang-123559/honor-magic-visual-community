using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public static class DesktopRefreshTests
{
    public static object Run(string root)
    {
        string fixture=Path.GetFullPath(Path.Combine(root,"evidence/desktop-refresh/2026-10-19","fixture-"+Guid.NewGuid().ToString("N"))),desktop=Path.Combine(fixture,"desktop"),outside=Path.Combine(fixture,"outside");Directory.CreateDirectory(desktop);Directory.CreateDirectory(outside);
        string a=Path.Combine(desktop,"A.txt"),b=Path.Combine(desktop,"B.txt"),hidden=Path.Combine(desktop,"hidden.txt"),virtualTarget="::{645FF040-5081-101B-9F08-00AA002F954E}",extra="::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
        File.WriteAllText(a,"Owned A");File.WriteAllText(b,"Owned B");File.WriteAllText(hidden,"Owned hidden");File.SetAttributes(hidden,File.GetAttributes(hidden)|FileAttributes.Hidden);
        var first=new DesktopItem{Name="A",Target=a,FromDesktop=true};var second=new DesktopItem{Name="B",Target=b,FromDesktop=true};var virtualItem=new DesktopItem{Name="回收站",Target=virtualTarget,IsVirtual=true,FromDesktop=true};var box=new Box{Id="box",Name="保留"};
        var state=new LayoutState{Boards=[new(){Id="main",Name="主屏",RootItems=[first,second,virtualItem],Boxes=[box],Grid=new(){Columns=12,Rows=9,NativeInitialized=true}}]};var board=state.Boards[0];
        board.Grid!.Placements=new(){[DesktopGridActions.ItemKey(first)]=new(6,4,1,1),[DesktopGridActions.ItemKey(second)]=new(2,6,1,1),[DesktopGridActions.ItemKey(virtualItem)]=new(8,2,1,1),[DesktopGridActions.FolderKey(box)]=new(0,0,2,2)};
        var store=new StateStore(Path.Combine(fixture,"state"));store.Save(state);var checks=new List<object>();void Check(string name,bool pass){checks.Add(new{name,pass});if(!pass)throw new InvalidDataException(name);}
        string[] Physical()=>DesktopEnumerationGuard.ReadPhysicalTargets([desktop]);
        string before=JsonSerializer.Serialize(state,StateStore.Json);byte[] disk=File.ReadAllBytes(store.FilePath);
        void Reject(string name,string[] raw,string[] shown){bool refused=false;try{DesktopEnumerationGuard.Validate(raw,shown,Physical());}catch(DesktopEnumerationUnavailableException){refused=true;}Check(name,refused&&before==JsonSerializer.Serialize(state,StateStore.Json)&&disk.SequenceEqual(File.ReadAllBytes(store.FilePath)));}
        Check("physical visibility check excludes hidden desktop files",Physical().ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals([a,b]));
        Reject("full Shell plus transient empty Explorer cannot clear layout",[a,b,virtualTarget,extra],[]);
        Reject("both providers temporarily empty cannot clear existing filesystem items",[],[]);
        Reject("partial Explorer cannot clear one existing anchor",[a,b,virtualTarget],[a,virtualTarget]);
        Reject("matching partial providers cannot hide a remaining filesystem item",[a],[a]);
        Reject("partial Shell cannot accept a complete Explorer view",[a],[a,b,virtualTarget]);
        DesktopEnumerationGuard.Validate([a,b,virtualTarget,extra],[a,b,virtualTarget],Physical());
        var next=StateStore.Copy(state);DesktopGridActions.Reconcile(next,[first,second,virtualItem],[]);
        Check("returning full snapshot after empty or partial snapshots preserves every anchor",JsonSerializer.Serialize(next,StateStore.Json)==before);
        for(int i=0;i<12;i++){Reject("transient episode keeps layout cycle "+i,[a,b,virtualTarget],[]);DesktopEnumerationGuard.Validate([a,b,virtualTarget],[a,b,virtualTarget],Physical());}
        File.Move(a,Path.Combine(outside,"A.txt"));DesktopEnumerationGuard.Validate([b,virtualTarget],[b,virtualTarget],Physical());
        next=StateStore.Copy(state);DesktopGridActions.Reconcile(next,[second,virtualItem],[]);store.Save(next);
        Check("real source removal still removes only its membership",!next.Boards[0].RootItems.Any(item=>item.Target==a)&&next.Boards[0].RootItems.Count==2);
        Check("real deletion does not relocate surviving root and folder anchors",next.Boards[0].Grid!.Placements[DesktopGridActions.ItemKey(second)]==new GridRect(2,6,1,1)&&next.Boards[0].Grid!.Placements[DesktopGridActions.ItemKey(virtualItem)]==new GridRect(8,2,1,1)&&next.Boards[0].Grid!.Placements[DesktopGridActions.FolderKey(box)]==new GridRect(0,0,2,2));
        DesktopEnumerationGuard.Validate([b,virtualTarget],[b],Physical());
        UnifiedDesktopGrid.PruneUnshownRoots(next,new HashSet<string>([virtualTarget],StringComparer.OrdinalIgnoreCase));DesktopGridActions.Reconcile(next,[second],[]);
        Check("intentional hiding of a virtual icon is still accepted",next.Boards[0].RootItems.Count==1&&!next.Boards[0].Grid!.Placements.ContainsKey(DesktopGridActions.ItemKey(virtualItem)));
        File.Move(b,Path.Combine(outside,"B.txt"));DesktopEnumerationGuard.Validate([extra],[],Physical());DesktopGridActions.Reconcile(next,[],[]);
        Check("a genuinely empty desktop is accepted while its large folder remains",next.Boards[0].RootItems.Count==0&&next.Boards[0].Grid!.Placements[DesktopGridActions.FolderKey(box)]==new GridRect(0,0,2,2));
        return new{allPass=true,checks,fixture,scope="Owned filesystem and actual grid reconciliation for repeated full-empty-full, partial, real deletion and legitimate zero-item snapshots; no live Explorer refresh or user layout writes"};
    }
}
