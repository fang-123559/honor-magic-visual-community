namespace MagicCommunity;

// Session-only, bounded metadata failure memory. No registry/file writes, no
// timers, no path/selection caching and no command-execution suppression.
internal sealed class ShellMenuFailureBackoff
{
    internal sealed record Failure(Guid Clsid,long RetryAtUtcMilliseconds,int HResult);
    private readonly Dictionary<Guid,Failure> failures=[];
    private readonly object gate=new();
    private readonly Func<long> now;
    internal ShellMenuFailureBackoff(Func<long>? clock=null){now=clock??(()=>DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());}
    internal bool Allows(Guid clsid){lock(gate){Prune();return !failures.ContainsKey(clsid);}}
    internal void Failed(Guid clsid,int hr){lock(gate){Prune();if(clsid==Guid.Empty)return;if(failures.Count>=128&&!failures.ContainsKey(clsid))return;failures[clsid]=new(clsid,now()+30_000,hr);}}
    internal void Succeeded(Guid clsid){lock(gate)failures.Remove(clsid);}
    internal Failure[] Export(){lock(gate){Prune();return failures.Values.ToArray();}}
    internal void Import(IEnumerable<Failure> values){lock(gate){Prune();long at=now();foreach(var item in values.Take(128))if(item.Clsid!=Guid.Empty&&item.RetryAtUtcMilliseconds>at&&(failures.Count<128||failures.ContainsKey(item.Clsid)))failures[item.Clsid]=item with{RetryAtUtcMilliseconds=Math.Min(item.RetryAtUtcMilliseconds,at+30_000)};}}
    private void Prune(){long at=now();foreach(var entry in failures.Values.Where(x=>x.RetryAtUtcMilliseconds<=at||x.RetryAtUtcMilliseconds>at+30_000).ToArray())failures.Remove(entry.Clsid);}
}
