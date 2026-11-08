using System.IO;
using System.Text.Json;

namespace MagicCommunity;

public sealed partial class CompositionDesktopRuntime
{
    private bool recoveryVisible;
    private string? recoveryBoard, recoveryFolder;
    private int recoveryOffset;
    private HashSet<string> recoveryCardIds = [];
    private long recoveryWindow;
    private Board OfflineBoard(string id) => DesktopDisplayRecovery.Offline(controller.State, controller.Screens).SingleOrDefault(b => b.Id == id)
        ?? throw new LayoutEditException("副屏已接入或内容已取出，请重新打开副屏内容。");
    private object RecoveryOpen(JsonElement request)
    {
        string action = Optional(request, "action") ?? "open";
        if (action == "close") { recoveryVisible = false; recoveryCardIds.Clear(); recoveryBoard = recoveryFolder = null; recoveryWindow = 0; QueueSnapshot(); return new { closed = true }; }
        if (request.TryGetProperty("window", out var hwnd))
        {
            if (!CompositionRendererIdentity.Verify(hwnd.GetInt64(), controller.Root)) throw new InvalidDataException("副屏面板窗口已变化。");
            recoveryWindow = hwnd.GetInt64();
        }
        recoveryVisible = true;
        var result=RecoveryView(Optional(request, "board"), Optional(request, "folder"), request.TryGetProperty("offset", out var offset) ? offset.GetInt32() : 0);QueueSnapshot();return result;
    }
    private object RecoveryView(string? boardId, string? folderId, int offset)
    {
        var offline = DesktopDisplayRecovery.Offline(controller.State, controller.Screens).ToArray();
        if (offline.Length == 0) { recoveryVisible = false; recoveryCardIds.Clear(); recoveryBoard = recoveryFolder = null; return new { closed = true }; }
        var board = offline.FirstOrDefault(b => b.Id == boardId) ?? offline[0];
        var folder = board.Boxes.SingleOrDefault(b => b.Id == folderId);
        recoveryBoard = board.Id; recoveryFolder = folder?.Id;
        var root = NativeDesktopTrial.DesktopTiles(board).ToDictionary(t => t.Key);
        var cards = DesktopCards.Items(board).ToDictionary(DesktopCards.Key);
        var keys = folder is not null ? folder.Items.Select(DesktopGridActions.ItemKey).ToArray() : DesktopDisplayRecovery.Order(board, controller.State.Displays?.SingleOrDefault(m => m.Id == board.Id)).ToArray();
        offset = Math.Clamp(offset, 0, Math.Max(0, keys.Length - 1));
        var shownCards = new HashSet<string>();
        var definitions = bridge?.NativeCardDefinitions ?? catalog.Types;
        var tiles = new List<object>(); var placements = new List<object>(); int bytes = 0, end = offset;
        for (; end < keys.Length && end < offset + 256; end++)
        {
            string key = keys[end]; object tile;
            if (cards.TryGetValue(key, out var card))
                tile = new { key, name = definitions.FirstOrDefault(t => t.Type == card.Type)?.Title ?? "卡片", card = new { card.Id, card.Type, card.Columns, card.Rows, card.Settings, custom = definitions.FirstOrDefault(t => t.Type == card.Type)?.Custom } };
            else
            {
                var item = folder is null ? root.GetValueOrDefault(key)?.Item : folder.Items.Single(i => DesktopGridActions.ItemKey(i) == key);
                var box = folder is null ? root.GetValueOrDefault(key)?.Folder : null;
                if (item is null && box is null) continue;
                tile = new { key, name = item?.Name ?? box!.Name, target = item?.Target, isVirtual = item?.IsVirtual ?? false, isFolder = item?.IsFolder ?? false, folderId = box?.Id,
                    items = box?.Items.Take(9).Select(i => new { key = DesktopGridActions.ItemKey(i), i.Name, i.Target, isFolder = i.IsFolder, isVirtual = i.IsVirtual }) };
            }
            int size = JsonSerializer.SerializeToUtf8Bytes(tile, Wire).Length;
            if (tiles.Count > 0 && bytes + size > 480 * 1024) break;
            bytes += size; tiles.Add(tile);if(cards.ContainsKey(key))shownCards.Add(key);
        }
        recoveryOffset = offset;recoveryCardIds = shownCards;
        var m = controller.State.Displays?.SingleOrDefault(d => d.Id == board.Id);
        var screens = controller.Screens.Select(s => new { s.Id, s.Label, s.Primary }).ToArray();
        return new { closed = false, revision = controller.State.Revision, board = board.Id, folder = folder?.Id, title = folder?.Name ?? "副屏内容", subtitle = m is { Width: > 0 } ? $"{m.Width} × {m.Height} · 已断开" : "已断开 · 保留全部布局引用",
            tabs = offline.Select(b => new { b.Id, b.Name, count = DesktopGridActions.Expected(b).Count }), screens, offset, next = end < keys.Length ? end : -1, total = keys.Length,
            boards = new[] { new { board.Id, board.Name, projection = new { originX = 20, originY = 100, stepX = 96, stepY = 96, scale = 0 }, placements, tiles } } };
    }
    private void VerifyRecovery(JsonElement request)
    {
        if (!recoveryVisible || recoveryWindow == 0 || request.GetProperty("window").GetInt64() != recoveryWindow || !CompositionRendererIdentity.Verify(recoveryWindow, controller.Root))
            throw new LayoutEditException("副屏面板已关闭，请重新打开。");
    }
    private object RecoveryIcons(JsonElement request)
    {
        VerifyRecovery(request); var board = OfflineBoard(Text(request, "board"));
        var keys = request.GetProperty("keys").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (keys.Length > 16 || keys.Any(k => k is null || k.Length > 160)) throw new InvalidDataException("图标批次无效。");
        var members = board.RootItems.Concat(board.Boxes.SelectMany(b => b.Items)).GroupBy(DesktopGridActions.ItemKey).ToDictionary(g => g.Key, g => g.First());
        return new { icons = keys.Select(key => { var item = members.GetValueOrDefault(key); string? uri = item is null ? null : bridge?.NativeIcon(item) ?? previewIcons?.Get(item);
            return new { key, png = uri is not null && uri.StartsWith("data:image/png;base64,", StringComparison.Ordinal) && uri.Length < 90000 ? uri[22..] : null }; }).ToArray() };
    }
    private object RecoveryAction(JsonElement request)
    {
        VerifyRecovery(request); string boardId = Text(request, "board"), kind = Text(request, "kind"), key = Optional(request, "key") ?? "";
        if(request.GetProperty("expectedRevision").GetInt64()!=controller.State.Revision)throw new LayoutEditException("拖动期间布局已变化，原内容已保留，请重试。");
        var source = OfflineBoard(boardId); string? folderId = Optional(request, "folder");
        if (kind == "open")
        {
            var item = (folderId is null ? source.RootItems : source.Boxes.Single(b => b.Id == folderId).Items).Single(i => DesktopGridActions.ItemKey(i) == key);
            controller.Open(item.Target); return new { opened = true };
        }
        if (controller.Store.ReadOnly) throw new LayoutEditException("布局只读，原内容已保留。");
        if (kind == "extract")
        {
            string destination = Text(request, "destination"); _ = controller.Screens.Single(s => s.Id == destination);
            int? x = request.TryGetProperty("column", out var cx) ? cx.GetInt32() : null, y = request.TryGetProperty("row", out var cy) ? cy.GetInt32() : null;
            controller.Commit(state => DesktopDisplayRecovery.Extract(state, boardId, key, folderId, state.Boards.Single(b => b.Id == destination), x, y), "已取出到桌面，文件路径未改变。");
        }
        else if (kind == "restore")
        {
            var screen = controller.Screens.Single(s => s.Id == Text(request, "destination"));
            controller.Commit(state => DesktopDisplayRecovery.RestoreTo(state, boardId, screen), "副屏内容已放到所选屏幕末尾的新页面，已有页面和位置保留。");
        }
        else if (kind == "reorder")
        {
            string before = Text(request, "before");
            if(key==before)return RecoveryView(boardId,folderId,recoveryOffset);
            controller.Commit(state => {
                var b = state.Boards.Single(b => b.Id == boardId);
                if (folderId is not null) { var box = b.Boxes.Single(b => b.Id == folderId); var item = box.Items.Single(i => DesktopGridActions.ItemKey(i) == key); var target = box.Items.Single(i => DesktopGridActions.ItemKey(i) == before); box.Items.Remove(item); box.Items.Insert(box.Items.IndexOf(target), item); }
                else { var memory = state.Displays!.Single(d => d.Id == boardId); var order = DesktopDisplayRecovery.Order(b, memory).ToList(); if (!order.Contains(key) || !order.Contains(before) || key == before) return; order.Remove(key); order.Insert(order.IndexOf(before), key); memory.Order = order; memory.Reordered = true; }
            }, "副屏内容顺序已保存，接入时将按当前顺序重排。");
        }
        else throw new InvalidDataException("未知的副屏操作。");
        return RecoveryView(boardId, folderId, recoveryOffset);
    }
}
