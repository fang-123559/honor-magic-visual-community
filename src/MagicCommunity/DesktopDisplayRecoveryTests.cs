using System.IO;
using System.Text.Json;

namespace MagicCommunity;

// Owned state and files only. Never reads/writes the installed desktop layout.
internal static class DesktopDisplayRecoveryTests
{
    internal static object Run(string root)
    {
        string folder = Path.Combine(root, "artifacts", "display-recovery-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var results = new List<object>();
        void Expect(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        void Check(string name, Action test) { test(); results.Add(new { name, pass = true }); }
        Display Screen(string id, bool primary = false, string hardware = "hardware-b", uint dpi = 96, int width = 1920) =>
            new(id, primary ? "主屏" : "副屏", id, 0, 0, width, 1080, primary) { PixelWidth = width, PixelHeight = 1080, Dpi = dpi, HardwareIdentity = hardware };
        var primary = Screen("a", true, "hardware-a"); var secondary = Screen("b");
        LayoutState Fixture()
        {
            var state = new LayoutState { SchemaVersion = 7, Boards = [new() { Id = "a", Name = "主屏" }, new() { Id = "b", Name = "副屏" }] };
            var b = state.Boards[1];
            b.RootItems.Add(new() { Name = "source.txt", Target = Path.Combine(folder, "source.txt"), FromDesktop = true });
            b.Boxes.Add(new() { Name = "工作", Items = [new() { Name = "nested.txt", Target = Path.Combine(folder, "nested.txt") }] });
            b.Cards = [new() { Type = "system.todo", Columns = 4, Rows = 2, Settings = JsonSerializer.SerializeToElement(new { text = "retain setting" }) }];
            foreach (var board in state.Boards) DesktopDisplayRecovery.Pack(board, board.Id == "a" ? primary : secondary, []);
            b.Grid!.Placements[DesktopGridActions.ItemKey(b.RootItems[0])] = new(7, 5, 1, 1);
            DesktopDisplayRecovery.Reconcile(state, [primary, secondary]); StateStore.Validate(state); return state;
        }
        LayoutState Disconnected() { var state = Fixture(); DesktopDisplayRecovery.Reconcile(state, [primary]); return state; }
        string Anchors(Board b) => JsonSerializer.Serialize(b.Pages, StateStore.Json);
        void Valid(LayoutState state) { StateStore.Validate(state); foreach (var b in state.Boards.Where(b => b.Grid is not null)) Expect(DesktopGridActions.Expected(b).Count == DesktopPages.Grids(b).Sum(g => g.Placements.Count) + b.Unplaced.Count, "Content lost"); }
        File.WriteAllText(Path.Combine(folder, "source.txt"), "original bytes"); File.WriteAllText(Path.Combine(folder, "nested.txt"), "nested bytes");

        Check("new-displays-have-no-generated-default-folders-after-five-cycles", () => {
            var state = new LayoutState();
            for (int i = 0; i < 5; i++) {
                DesktopDisplayRecovery.Reconcile(state, [primary, secondary]);
                Expect(state.Boards.Count == 2 && state.Boards.All(b => b.Boxes.Count == 0), "Default folders regenerated");
                DesktopDisplayRecovery.Reconcile(state, [primary]);
            }
            Valid(state);
        });
        Check("user-created-folder-with-former-default-name-is-not-hidden-or-removed", () => {
            var state = Fixture(); var board = state.Boards[1]; var box = new Box { Name = "常用" }; board.Boxes.Add(box);
            DesktopGridActions.ReconcileBoard(board);
            DesktopDisplayRecovery.Reconcile(state, [primary]); DesktopDisplayRecovery.Reconcile(state, [primary, secondary]);
            Expect(board.Boxes.Contains(box) && DesktopPages.Position(board, DesktopGridActions.FolderKey(box)) is not null, "User folder removed by name"); Valid(state);
        });

        Check("primary-session-return-without-edid-retains-pages-and-active-page",()=>{
            var state=Fixture();var board=state.Boards[0];
            board.Pages!.Add(new(){Grid=new(){Columns=board.Grid!.Columns,Rows=board.Grid.Rows,Bounded=true,NativeInitialized=true}});
            board.ActivePageId=board.Pages[1].Id;board.Paged=true;
            string before=JsonSerializer.Serialize(board,StateStore.Json);
            DesktopDisplayRecovery.Reconcile(state,[]);
            DesktopDisplayRecovery.Reconcile(state,[primary with{HardwareIdentity=""},secondary]);
            Expect(JsonSerializer.Serialize(board,StateStore.Json)==before,"Session return repacked primary pages");Valid(state);
        });
        Check("disconnect-retains-all-pages-and-reference-bytes", () => { var s = Fixture(); string before = Anchors(s.Boards[1]); DesktopDisplayRecovery.Reconcile(s, [primary]); Expect(Anchors(s.Boards[1]) == before && DesktopDisplayRecovery.Offline(s, [primary]).Count() == 1, "Disconnect moved content"); Valid(s); });
        Check("same-hardware-resolution-dpi-restores-exact-anchors", () => { var s = Disconnected(); string before = Anchors(s.Boards[1]); DesktopDisplayRecovery.Reconcile(s, [primary, secondary]); Expect(Anchors(s.Boards[1]) == before && !DesktopDisplayRecovery.Offline(s, [primary, secondary]).Any(), "Exact restore changed anchors"); Valid(s); });
        foreach (var target in new[] { secondary with { HardwareIdentity = "new-monitor" }, secondary with { HardwareIdentity = "" } })
            Check("changed-identity-or-projection-defaults-" + target.HardwareIdentity + "-" + target.Dpi + "-" + target.PixelWidth, () => { var s = Disconnected(); var key = DesktopGridActions.ItemKey(s.Boards[1].RootItems[0]); DesktopDisplayRecovery.Reconcile(s, [primary, target]); Expect(DesktopPages.Position(s.Boards[1], key) != new GridRect(7, 5, 1, 1), "Changed display restored stale anchor"); Valid(s); });
        foreach(var target in new[]{secondary with{Dpi=144},secondary with{Dpi=0},secondary with{PixelWidth=2560}})
            Check("same-hardware-projection-change-preserves-pages-"+target.Dpi+"-"+target.PixelWidth,()=>{var state=Disconnected();string before=JsonSerializer.Serialize(state.Boards[1],StateStore.Json);DesktopDisplayRecovery.Reconcile(state,[primary,target]);Expect(JsonSerializer.Serialize(state.Boards[1],StateStore.Json)==before,"Same monitor geometry repacked layout");Valid(state);});
        Check("three-primary-resume-cycles-retain-pages-icons-cards-and-custom-grid",()=>{
            var state=Fixture();var board=state.Boards[0];board.Paged=true;
            board.IconGrid=new(){Columns=board.Grid!.Columns,Rows=board.Grid.Rows,RefWidth=1920,RefHeight=1080};board.SortSettings=new(){Mode="custom"};
            board.RootItems.Add(new(){Name="primary-owned",Target=Path.Combine(folder,"primary-owned")});
            board.Grid.Placements[DesktopGridActions.ItemKey(board.RootItems[0])]=new(7,5,1,1);
            board.Cards=[new(){Type="system.clock",Columns=2,Rows=2}];
            DesktopPages.Add(board,false);board.Grid!.Placements[DesktopCards.Key(board.Cards[0])]=new(3,4,2,2);
            string before=JsonSerializer.Serialize(board,StateStore.Json);
            for(int cycle=0;cycle<3;cycle++){
                DesktopDisplayRecovery.Reconcile(state,[]);
                var transient=primary with{HardwareIdentity="",Dpi=0,PixelWidth=1024,PixelHeight=768};
                DesktopDisplayRecovery.Reconcile(state,[transient,secondary]);
                DesktopGridLayout.ResizeWithoutMoving(board,DesktopGridLayout.Project(board,transient,1));
                DesktopDisplayRecovery.Reconcile(state,[primary,secondary]);
                DesktopGridLayout.ResizeWithoutMoving(board,DesktopGridLayout.Project(board,primary,1));
                Expect(JsonSerializer.Serialize(board,StateStore.Json)==before,"Resume geometry overwrote a primary page/card/grid anchor");Valid(state);
            }
        });
        Check("connector-reuse-without-intermediate-disconnect-does-not-trust-old-monitor", () => { var s = Fixture(); var b = s.Boards[1]; var key = DesktopGridActions.ItemKey(b.RootItems[0]); DesktopDisplayRecovery.Reconcile(s, [primary, secondary with { HardwareIdentity = "swapped-in-dock" }]); Expect(DesktopPages.Position(b, key) != new GridRect(7, 5, 1, 1), "Reused connector trusted old display"); Valid(s); });
        Check("connector-change-with-unique-edid-preserves-board-and-anchors", () => { var s = Disconnected(); var old = s.Boards[1]; string before = Anchors(old); DesktopDisplayRecovery.Reconcile(s, [primary, secondary with { Id = "new-connector" }]); Expect(old.Id == "new-connector" && Anchors(old) == before && s.Boards.Count == 2, "Connector change duplicated board"); Valid(s); });
        Check("one-new-secondary-adopts-offline-content-with-default-order", () => { var s = Disconnected(); var b = s.Boards[1]; var key = DesktopGridActions.ItemKey(b.RootItems[0]); DesktopDisplayRecovery.Reconcile(s, [primary, Screen("c", hardware: "different")]); Expect(b.Id == "c" && DesktopPages.Position(b, key) != new GridRect(7, 5, 1, 1), "Replacement failed"); Valid(s); });
        Check("ambiguous-new-monitors-leave-explicit-mapping", () => { var s = Disconnected(); DesktopDisplayRecovery.Reconcile(s, [primary, Screen("c", hardware: "same"), Screen("d", hardware: "same")]); Expect(DesktopDisplayRecovery.Offline(s, [primary, Screen("c"), Screen("d")]).Single().Id == "b" && s.Boards.Count == 4, "Ambiguous mapping guessed"); Valid(s); });
        Check("duplicate-edid-is-not-an-exact-identity", () => { var s = Disconnected(); DesktopDisplayRecovery.Reconcile(s, [primary, secondary, Screen("c")]); var key = DesktopGridActions.ItemKey(s.Boards[1].RootItems[0]); Expect(DesktopPages.Position(s.Boards[1], key) != new GridRect(7, 5, 1, 1), "Duplicate serial trusted"); Valid(s); });
        Check("restart-offline-serialization-and-v8-migration-backup", () => { var store = new StateStore(Path.Combine(folder, "migration")); var s = Fixture(); s.SchemaVersion = 7; s.Displays = null; store.Save(s); string previous = File.ReadAllText(store.FilePath); s = store.Load(); DesktopDisplayRecovery.Reconcile(s, [primary, secondary]); DesktopDisplayRecovery.Reconcile(s, [primary]); store.Save(s); var loaded = new StateStore(store.DirectoryPath).Load(); Expect(loaded.SchemaVersion == 8 && DesktopDisplayRecovery.Offline(loaded, [primary]).Count() == 1, "Restart lost offline references"); var copies = Directory.GetFiles(store.DirectoryPath, "*.before-display-v8-*.json"); Expect(copies.Length == 1 && File.ReadAllText(copies[0]) == previous, "Original schema not preserved"); Valid(loaded); });
        foreach (string type in new[] { "root", "folder", "card", "nested" })
            Check("extract-" + type + "-moves-reference-only-and-preserves-identity", () => { var s = Disconnected(); var source = s.Boards[1]; var dest = s.Boards[0]; string? parent = type == "nested" ? source.Boxes[0].Id : null; string key = type switch { "root" => DesktopGridActions.ItemKey(source.RootItems[0]), "folder" => DesktopGridActions.FolderKey(source.Boxes[0]), "card" => DesktopCards.Key(source.Cards![0]), _ => DesktopGridActions.ItemKey(source.Boxes[0].Items[0]) }; string? settings = type == "card" ? source.Cards![0].Settings.GetRawText() : null; DesktopDisplayRecovery.Extract(s, source.Id, key, parent, dest, 6, 4); Expect(DesktopPages.Position(dest, key)?.X == 6, "Requested available destination lost"); if (settings is not null) Expect(dest.Cards![0].Settings.GetRawText() == settings, "Card settings lost"); Expect(File.ReadAllText(Path.Combine(folder, "source.txt")) == "original bytes" && File.ReadAllText(Path.Combine(folder, "nested.txt")) == "nested bytes", "File altered"); Valid(s); });
        Check("occupied-drop-finds-next-free-slot", () => { var s = Disconnected(); var item = new DesktopItem { Name = "occupied", Target = Path.Combine(folder, "occupied") }; var dest = s.Boards[0]; dest.RootItems.Add(item); dest.Grid!.Placements.Add(DesktopGridActions.ItemKey(item), new(0, 0, 1, 1)); var key = DesktopGridActions.ItemKey(s.Boards[1].RootItems[0]); DesktopDisplayRecovery.Extract(s, "b", key, null, dest, 0, 0); Expect(DesktopPages.Position(dest, key) == new GridRect(1, 0, 1, 1), "Occupied cell overwritten"); Valid(s); });
        Check("full-page-failure-leaves-original-state", () => { var s = Disconnected(); var dest = s.Boards[0]; dest.Grid!.Columns = dest.Grid.Rows = 1; dest.RootItems.Add(new() { Name = "occupied", Target = Path.Combine(folder, "full") }); DesktopGridActions.ReconcileBoard(dest); string before = JsonSerializer.Serialize(s); var copy = StateStore.Copy(s); bool failed = false; try { DesktopDisplayRecovery.Extract(copy, "b", DesktopCards.Key(copy.Boards[1].Cards![0]), null, copy.Boards[0], 0, 0); } catch (LayoutEditException) { failed = true; } Expect(failed && before == JsonSerializer.Serialize(s), "Failed drop changed original"); Valid(s); });
        Check("explicit-restore-merges-without-losing-content", () => { var s = Disconnected(); int count = DesktopGridActions.Expected(s.Boards[1]).Count; DesktopDisplayRecovery.RestoreTo(s, "b", primary); Expect(s.Boards.Count == 1 && DesktopGridActions.Expected(s.Boards[0]).Count == count && s.Displays!.All(d => d.Id != "b"), "Restore lost board contents"); Valid(s); });
        Check("restore-appends-only-and-preserves-existing-pages-active-page-and-settings", () => {
            var state = Disconnected(); var board = state.Boards[0];
            var original = new DesktopItem { Name = "original", Target = Path.Combine(folder, "original") }; board.RootItems.Add(original);
            board.Grid!.Placements.Add(DesktopGridActions.ItemKey(original), new(8, 6, 1, 1));
            board.Paged = true; DesktopPages.Add(board, false); // An intentionally empty last page must also remain untouched.
            string oldPages = Anchors(board), active = board.ActivePageId!;
            var unplaced = board.Unplaced; var iconGrid = board.IconGrid; var sort = board.SortSettings;
            var incoming = DesktopGridActions.Expected(state.Boards[1]).Keys.ToHashSet();
            DesktopDisplayRecovery.RestoreTo(state, "b", primary);
            Expect(board.Pages!.Count == 3 && JsonSerializer.Serialize(board.Pages.Take(2), StateStore.Json) == oldPages, "Old pages changed or empty page reused");
            Expect(board.ActivePageId == active && ReferenceEquals(board.Unplaced, unplaced) && board.IconGrid == iconGrid && board.SortSettings == sort, "Existing presentation changed");
            Expect(incoming.SetEquals(board.Pages[2].Grid.Placements.Keys), "Incoming items filled existing gaps");
            DesktopPages.Select(board, board.Pages[2].Id); Valid(state);
            // A normal appended page follows the existing empty-page deletion rule.
            string appended = board.Pages[2].Id;
            board.RootItems.RemoveAll(i => incoming.Contains(DesktopGridActions.ItemKey(i)));
            board.Boxes.RemoveAll(b => incoming.Contains(DesktopGridActions.FolderKey(b)));
            board.Cards?.RemoveAll(c => incoming.Contains(DesktopCards.Key(c)));
            foreach (string key in incoming) DesktopPages.Forget(board, key);
            DesktopPages.Delete(board, appended);
            Expect(Anchors(board) == oldPages, "Deleting emptied appended page changed old pages"); Valid(state);
        });
        Check("three-restores-append-one-populated-page-per-source-with-no-empty-extras", () => {
            var state = Fixture(); state.Boards.RemoveAt(1); state.Displays!.RemoveAll(m => m.Id == "b"); var board = state.Boards[0];
            string original = Anchors(board);
            for (int i = 0; i < 3; i++) {
                var source = new Board { Id = "offline-" + i, Name = "副屏", RootItems = [new() { Name = "incoming-" + i, Target = Path.Combine(folder, "incoming-" + i) }] };
                DesktopDisplayRecovery.Pack(source, secondary, []); state.Boards.Add(source);
                DesktopDisplayRecovery.RestoreTo(state, source.Id, primary);
                Expect(board.Pages!.Count == i + 2 && board.Pages.Skip(1).All(p => p.Grid.Placements.Count == 1), "Restore duplicated or created empty pages"); Valid(state);
            }
            Expect(JsonSerializer.Serialize(board.Pages!.Take(1), StateStore.Json) == original, "First page changed");
        });
        foreach (string failure in new[] { "page-limit", "oversize", "card-limit" })
            Check("restore-preflight-" + failure + "-leaves-both-boards-unchanged", () => {
                var state = Disconnected(); var board = state.Boards[0];
                if (failure == "page-limit") { board.Paged = true; while (board.Pages!.Count < DesktopPages.MaximumPages) DesktopPages.Add(board, false); }
                if (failure == "oversize") { board.Grid!.Columns = 1; board.Grid.Rows = 1; }
                if (failure == "card-limit") { board.Cards = Enumerable.Range(0, 64).Select(_ => new DesktopCard { Type = "system.clock", Columns = 1, Rows = 1 }).ToList(); DesktopDisplayRecovery.Pack(board, primary, []); }
                string before = JsonSerializer.Serialize(state, StateStore.Json); bool failed = false;
                try { DesktopDisplayRecovery.RestoreTo(state, "b", primary); } catch (LayoutEditException) { failed = true; }
                Expect(failed && JsonSerializer.Serialize(state, StateStore.Json) == before, "Preflight failure partially changed state"); Valid(state);
            });
        Check("empty-source-does-not-append-an-empty-page", () => {
            var state = Fixture(); var source = state.Boards[1]; source.RootItems.Clear(); source.Boxes.Clear(); source.Cards = null; DesktopGridActions.ReconcileBoard(source);
            var board = state.Boards[0]; string before = JsonSerializer.Serialize(board, StateStore.Json);
            DesktopDisplayRecovery.RestoreTo(state, "b", primary);
            Expect(state.Boards.Count == 1 && JsonSerializer.Serialize(board, StateStore.Json) == before, "Empty source added a page"); Valid(state);
        });
        Check("restore-rejects-card-limit-on-transaction-copy", () => { var s = Disconnected(); var dest = s.Boards[0]; dest.Cards = Enumerable.Range(0, 64).Select(_ => new DesktopCard { Type = "system.clock", Columns = 1, Rows = 1 }).ToList(); DesktopDisplayRecovery.Pack(dest, primary, []); var copy = StateStore.Copy(s); bool failed = false; try { DesktopDisplayRecovery.RestoreTo(copy, "b", primary); } catch (LayoutEditException) { failed = true; } Expect(failed && s.Boards.Count == 2 && s.Boards[1].Cards!.Count == 1, "Limit failure mutated original"); Valid(s); });
        Check("many-page-overflow-retains-every-key", () => { var s = Fixture(); var b = s.Boards[1]; b.RootItems = Enumerable.Range(0, 4096).Select(i => new DesktopItem { Name = "item" + i, Target = Path.Combine(folder, "bulk" + i) }).ToList(); var tiny = Screen("b", dpi: 384, width: 320) with { Height = 240, PixelHeight = 240 }; DesktopDisplayRecovery.Pack(b, tiny, []); Expect(b.Pages!.Count <= DesktopPages.MaximumPages && b.Unplaced.Count > 0, "Overflow path not exercised"); Valid(s); });
        Check("custom-offline-order-triggers-repack-on-reconnect", () => { var s = Disconnected(); var b = s.Boards[1]; var memory = s.Displays!.Single(d => d.Id == "b"); memory.Order = DesktopDisplayRecovery.Order(b, memory).Reverse().ToList(); memory.Reordered = true; var first = memory.Order[0]; DesktopDisplayRecovery.Reconcile(s, [primary, secondary]); Expect(DesktopPages.Position(b, first) is { X: 0, Y: 0 }, "Offline custom order ignored"); Valid(s); });
        Check("malformed-display-record-rejected", () => { var s = Fixture(); s.Displays!.Add(s.Displays[0]); bool failed = false; try { StateStore.Validate(s); } catch (InvalidDataException) { failed = true; } Expect(failed, "Duplicate record accepted"); });
        Check("future-schema-remains-read-only", () => { var store = new StateStore(Path.Combine(folder, "future")); Directory.CreateDirectory(store.DirectoryPath); File.WriteAllText(store.FilePath, "{\"SchemaVersion\":10,\"Secret\":\"retain\"}"); string before = File.ReadAllText(store.FilePath); store.Load(); Expect(store.ReadOnly && File.ReadAllText(store.FilePath) == before, "Future version overwritten"); });
        return new { allPass = true, tests = results, output = folder, scope = "Owned files and layout fixtures only; no real hotplug, desktop activation or user acceptance." };
    }
}
