using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Runtime.InteropServices;
using System.Net;
using System.Net.Http;
namespace MagicCommunity;
internal static class CommunityUpdateTests
{
    internal static object Run(string output){
        Directory.CreateDirectory(output);var checks=new List<object>();void Expect(bool pass,string name){if(!pass)throw new InvalidDataException(name);checks.Add(new{name,pass});}
        var source=new UnconfiguredUpdateSource();var result=source.Check(CancellationToken.None).GetAwaiter().GetResult();
        Expect(result.State==CommunityUpdateState.Unconfigured&&result.Release is null,"no source never claims latest");
        using(var cancelled=new CancellationTokenSource()){cancelled.Cancel();try{source.Check(cancelled.Token).GetAwaiter().GetResult();throw new Exception("cancel ignored");}catch(OperationCanceledException){Expect(true,"source cancellation");}}
        CheckGitHub(Expect);
        CheckDualSource(Expect);
        var release=new CommunityRelease("test-only.1","新增：检查更新窗口\n优化：通知折叠与连续滑动\n修复：所有内容均为离屏测试数据",new("https://github.com/fixture/project/releases/tag/test"));
        CommunityUpdateWindow.ValidateRelease(release);Expect(true,"valid HTTPS GitHub release");
        foreach(string url in new[]{"http://github.com/a/b","https://github.com.evil.test/a","file:///C:/test.exe","https://user@github.com/a/b"}){
            try{CommunityUpdateWindow.ValidateRelease(release with{Page=new(url)});throw new Exception("unsafe URL accepted");}catch(InvalidDataException){Expect(true,"reject "+url);}
        }
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var icon=new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/MagicCommunity;component/UpdateIcon.png"));
        var rgba=new System.Windows.Media.Imaging.FormatConvertedBitmap(icon,System.Windows.Media.PixelFormats.Bgra32,null,0);var corner=new byte[4];
        foreach(var point in new[]{new System.Windows.Point(0,0),new System.Windows.Point(icon.PixelWidth-1,0),new System.Windows.Point(0,icon.PixelHeight-1),new System.Windows.Point(icon.PixelWidth-1,icon.PixelHeight-1)}){rgba.CopyPixels(new Int32Rect((int)point.X,(int)point.Y,1,1),corner,4,0);Expect(corner[3]==0,"embedded update icon has a transparent corner "+point);}
        foreach(bool dark in new[]{false,true})foreach(var state in Enum.GetValues<CommunityUpdateState>()){
            var window=new CommunityUpdateWindow(dark:dark);window.Paint(new(state,state==CommunityUpdateState.Available?release:null,state==CommunityUpdateState.NoRelease?"GitHub 发布仓库已接入，暂无适用于当前通道的已发布版本。":state==CommunityUpdateState.Unconfigured?"更新来源尚未接入":state==CommunityUpdateState.Failed?"测试网络错误":null));
            window.Export(Path.Combine(output,state+(dark?"-dark":"-light")+".png"));window.Close();Expect(true,"render "+state+" "+dark);
        }
        var caption=new CommunityUpdateWindow();var hwnd=new WindowInteropHelper(caption).EnsureHandle();
        Expect(!IsWindowVisible(hwnd),"caption material probe remains hidden");
        Expect((GetWindowLongPtr(hwnd,-20).ToInt64()&1)==1,"community default caption has no icon slot");
        Expect(SendMessage(hwnd,0x7f,IntPtr.Zero,IntPtr.Zero)==IntPtr.Zero,"small caption icon removed");
        Expect(SendMessage(hwnd,0x7f,new IntPtr(1),IntPtr.Zero)==IntPtr.Zero,"large caption icon removed");caption.Close();
        return new{allPass=true,checks,sourceConfigured=true,liveNetworkUsed=false,manualAcceptance=false};
    }
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellation)=>send(request,cancellation);
    }
    private static void CheckDualSource(Action<bool,string> expect)
    {
        string Manifest(string records="[]",string repository=GitHubCommunityUpdateSource.Repository,int schema=1)=>"{\"schemaVersion\":"+schema+",\"repository\":"+JsonSerializer.Serialize(repository)+",\"releases\":"+records+"}";
        string Release(string tag="0.3.9-preview.17",bool draft=false,bool prerelease=true,string published="2026-10-27T00:00:00Z")=>JsonSerializer.Serialize(new[]{new{tag_name=tag,draft,prerelease,published_at=published,body="公开更新说明"}});
        CommunityUpdateResult Probe(string manifest,HttpStatusCode api=HttpStatusCode.Forbidden,string apiBody="[]",string installed="0.3.9-preview.16",bool timeout=false)
        {
            int calls=0;
            using var http=new HttpClient(new Handler((request,token)=>{
                token.ThrowIfCancellationRequested();calls++;
                if(calls==1){expect(request.RequestUri!.Host=="api.github.com","dual source starts at API");if(timeout)throw new TaskCanceledException();return Task.FromResult(new HttpResponseMessage(api){Content=new StringContent(apiBody)});}
                expect(calls==2&&request.RequestUri==GitHubCommunityUpdateSource.ManifestUrl,"fallback makes one fixed Raw request");
                expect(request.Headers.Authorization is null&&!request.Headers.Contains("X-GitHub-Api-Version"),"Raw fallback has no token or API-only headers");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(manifest)});
            }));
            var result=new GitHubCommunityUpdateSource(http,installed).Check(CancellationToken.None).GetAwaiter().GetResult();
            expect(calls==(api==HttpStatusCode.OK&&apiBody=="[]"&&!timeout?1:2),"successful API does not request fallback");return result;
        }
        var manifest=Manifest(Release());
        foreach(var status in new[]{HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.InternalServerError,HttpStatusCode.NotFound})
            expect(Probe(manifest,status).State==CommunityUpdateState.Available,"fallback recovers API "+status);
        expect(Probe(manifest,timeout:true).State==CommunityUpdateState.Available,"API timeout still permits fallback");
        expect(Probe(manifest,HttpStatusCode.OK,"{").State==CommunityUpdateState.Available,"invalid API data uses validated fallback");
        expect(Probe(manifest,HttpStatusCode.OK).State==CommunityUpdateState.NoRelease,"successful empty API is authoritative");
        expect(Probe(Manifest()).State==CommunityUpdateState.NoRelease,"configured empty manifest is not latest");
        expect(Probe(Manifest(Release("0.3.9-preview.16"))).State==CommunityUpdateState.Latest,"fallback reuses version comparison");
        expect(Probe(manifest,installed:"0.3.9").State==CommunityUpdateState.NoRelease,"fallback respects stable channel");
        foreach(string invalid in new[]{Manifest(repository:"other/repo"),Manifest(schema:2),"{\"schemaVersion\":\"1\"}",Manifest(Release(draft:true)),Manifest(Release(tag:"../invalid")),Manifest(Release(published:"")),Manifest("{}"),"["+new string(' ',512*1024)+"]"})
        {
            try{Probe(invalid);throw new Exception("Unsafe fallback accepted");}catch(HttpRequestException e){expect(e.Message.Contains("稍后重试"),"dual failure is a retryable error, never latest");}
        }
        int cancelledCalls=0;using var stop=new CancellationTokenSource();
        using(var http=new HttpClient(new Handler((_,token)=>{cancelledCalls++;stop.Cancel();token.ThrowIfCancellationRequested();throw new Exception("Cancellation ignored");})))
        {
            try{new GitHubCommunityUpdateSource(http).Check(stop.Token).GetAwaiter().GetResult();throw new Exception("Cancellation ignored");}
            catch(OperationCanceledException){expect(cancelledCalls==1,"caller cancellation never starts fallback");}
        }
    }
    private static void CheckGitHub(Action<bool,string> expect)
    {
        static object Release(string tag,bool draft=false,bool prerelease=false,string notes="更新说明")=>new{tag_name=tag,draft,prerelease,body=notes,html_url="https://untrusted.invalid/releases",assets=new[]{new{browser_download_url="https://untrusted.invalid/installer.exe"}}};
        CommunityUpdateResult Probe(string body,string installed="0.3.9-preview.16",HttpStatusCode code=HttpStatusCode.OK)
        {
            using var http=new HttpClient(new Handler((request,token)=>{
                token.ThrowIfCancellationRequested();
                expect(request.Method==HttpMethod.Get&&request.RequestUri?.AbsoluteUri=="https://api.github.com/repos/"+GitHubCommunityUpdateSource.Repository+"/releases?per_page=100&page=1","request is bound to release repository");
                expect(request.Headers.Authorization is null&&request.Headers.UserAgent.Count>0&&request.Headers.Contains("X-GitHub-Api-Version"),"public request has identification and no credentials");
                return Task.FromResult(new HttpResponseMessage(code){Content=new StringContent(body)});
            }));
            return new GitHubCommunityUpdateSource(http,installed).CheckApi(CancellationToken.None).GetAwaiter().GetResult();
        }
        string List(params object[] releases)=>JsonSerializer.Serialize(releases);
        expect(Probe("[]").State==CommunityUpdateState.NoRelease,"empty repository never claims latest");
        expect(Probe(List(Release("0.3.9-preview.16"))).State==CommunityUpdateState.Latest,"same release is current");
        expect(Probe(List(Release("0.3.9-preview.15"))).State==CommunityUpdateState.Latest,"local newer preview is not downgraded");
        var next=Probe(List(Release("v0.3.9-preview.17",prerelease:true)));
        expect(next.State==CommunityUpdateState.Available&&next.Release?.Version=="v0.3.9-preview.17","preview channel accepts published preview");
        expect(next.Release?.Page.AbsoluteUri==GitHubCommunityUpdateSource.ReleasesPage.AbsoluteUri+"/tag/v0.3.9-preview.17"&&next.Release.Download is null,"untrusted release and asset URLs cannot redirect update action");
        expect(Probe(List(Release("v0.2.0-preview.67",prerelease:true)),"0.2.0-preview.65").Release?.Version=="v0.2.0-preview.67","historic full preview tags and v prefix compare correctly");
        expect(Probe(List(Release("0.3.9-preview.10"),Release("0.3.9-preview.9")),"0.3.9-preview.9").Release?.Version=="0.3.9-preview.10","numeric preview ordering beats response ordering");
        expect(Probe(List(Release("0.3.9"))).State==CommunityUpdateState.Available,"final release outranks same-core preview");
        expect(Probe(List(Release("0.4.0-preview.1",prerelease:true)),"0.3.9").State==CommunityUpdateState.NoRelease,"stable channel excludes previews");
        expect(Probe(List(Release("0.4.0",prerelease:true)),"0.3.9").State==CommunityUpdateState.NoRelease,"stable channel respects GitHub prerelease flag");
        expect(Probe(List(Release("9.0.0",draft:true),Release("nonsense"))).State==CommunityUpdateState.NoRelease,"drafts and unsupported tags are not offered");
        expect(Probe(List(Release("0.3.9-preview.16+new-build"))).State==CommunityUpdateState.Latest,"build metadata does not trigger an update");
        foreach(var pair in new[]{("1.0.0-alpha","1.0.0-alpha.1"),("1.0.0-alpha.1","1.0.0-alpha.beta"),("1.0.0-alpha.beta","1.0.0-beta"),("1.0.0-beta.2","1.0.0-beta.11"),("1.0.0-rc.1","1.0.0"),("9.0.0","10.0.0"),("1.99999999999999999999.0","1.100000000000000000000.0")})
            expect(GitHubReleaseVersion.Parse(pair.Item1)!.CompareTo(GitHubReleaseVersion.Parse(pair.Item2))<0,"SemVer precedence "+pair);
        foreach(string tag in new[]{"","v","1.0","01.0.0","1.0.0-01","1.0.0-","1.0.0+","1.0.0+a..b","1.0.0-../x"," 1.0.0","1.0.0.1",new string('1',81)})
            expect(GitHubReleaseVersion.Parse(tag) is null,"invalid version rejected "+tag);
        foreach(var code in new[]{HttpStatusCode.NotFound,HttpStatusCode.Forbidden,HttpStatusCode.TooManyRequests,HttpStatusCode.Found,HttpStatusCode.InternalServerError})
        {
            try{Probe("[]",code:code);throw new Exception("HTTP error claimed success");}catch(HttpRequestException){expect(true,"HTTP error is not latest "+code);}
        }
        foreach(string json in new[]{"{}","[null]","not json",List(Release("0.3.9-preview.17",notes:new string('x',65537)))})
        {
            try{Probe(json);throw new Exception("Malformed data accepted");}catch(Exception e)when(e is JsonException or InvalidDataException){expect(true,"malformed response rejected");}
        }
        try{Probe("["+new string(' ',2*1024*1024)+"]");throw new Exception("Unbounded response accepted");}catch(HttpRequestException){expect(true,"release response size bounded");}
        using(var cancelled=new CancellationTokenSource())
        using(var http=new HttpClient(new Handler((_,token)=>{token.ThrowIfCancellationRequested();throw new Exception("Cancelled request ran");})))
        {
            cancelled.Cancel();try{new GitHubCommunityUpdateSource(http).Check(cancelled.Token).GetAwaiter().GetResult();throw new Exception("Cancelled request accepted");}catch(OperationCanceledException){expect(true,"GitHub source preserves caller cancellation");}
        }
        using(var http=new HttpClient(new Handler((_,_)=>throw new TaskCanceledException())))
        {
            try{new GitHubCommunityUpdateSource(http).CheckApi(CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Timeout accepted");}catch(TimeoutException){expect(true,"network timeout has retryable state");}
        }
        int requests=0;
        string fullPage=List(Enumerable.Repeat(Release("0.3.9-preview.16"),100).ToArray());
        using(var http=new HttpClient(new Handler((request,_)=>{
            requests++;expect(request.RequestUri!.Query.EndsWith("page="+requests),"release pages advance");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(requests==1?fullPage:List(Release("0.3.9-preview.17")))});
        })))
            expect(new GitHubCommunityUpdateSource(http,"0.3.9-preview.16").CheckApi(CancellationToken.None).GetAwaiter().GetResult().State==CommunityUpdateState.Available&&requests==2,"later page is checked before claiming latest");
        requests=0;
        using(var http=new HttpClient(new Handler((_,_)=>{requests++;return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=new StringContent(fullPage)});})))
        {
            try{new GitHubCommunityUpdateSource(http,"0.3.9-preview.16").CheckApi(CancellationToken.None).GetAwaiter().GetResult();throw new Exception("Unbounded release pagination accepted");}
            catch(InvalidDataException){expect(requests==5,"release pagination is bounded and never claims latest from incomplete data");}
        }
    }
    [DllImport("user32.dll")]private static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll",EntryPoint="GetWindowLongPtrW")]private static extern IntPtr GetWindowLongPtr(IntPtr h,int index);
    [DllImport("user32.dll",EntryPoint="SendMessageW")]private static extern IntPtr SendMessage(IntPtr h,int message,IntPtr w,IntPtr l);
}
