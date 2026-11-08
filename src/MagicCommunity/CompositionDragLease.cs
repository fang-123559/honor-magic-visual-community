using System.Diagnostics;
namespace MagicCommunity;

// A single drag can advance only the revisions produced by its own page turns.
// Any unrelated layout mutation invalidates it; no polling or persisted tokens.
internal sealed class CompositionDragLease
{
    internal NativeDesktopTrial.DragPayload Source { get; }
    internal string Token { get; }=Guid.NewGuid().ToString("N");
    internal string? SourcePage { get; }
    internal long Revision { get; private set; }
    private readonly long created=Stopwatch.GetTimestamp();
    internal CompositionDragLease(NativeDesktopTrial.DragPayload source,string? page)
    { Source=source with {Keys=(string[])source.Keys.Clone()};SourcePage=page;Revision=source.Revision; }
    internal bool Accept(NativeDesktopTrial.DragPayload drag,long revision)=>
        Stopwatch.GetElapsedTime(created)<TimeSpan.FromMinutes(2)&&Revision==revision&&drag.Lease==Token&&(drag.Page??"")==(SourcePage??"")&&
        drag.Session==Source.Session&&drag.Revision==Source.Revision&&drag.Board==Source.Board&&drag.Folder==Source.Folder&&
        drag.Anchor==Source.Anchor&&drag.Keys.SequenceEqual(Source.Keys);
    internal void Advance(long before,long after)
    {if(Revision!=before||after!=before+1)throw new LayoutEditException("拖动期间布局已变化。");Revision=after;}
}
