using System.IO;
using System.Text;
using System.Text.Json;
using System.Windows.Controls;
using System.Windows.Documents;
using Node=MagicCommunity.NativeHandoffStorage.Node;
namespace MagicCommunity;
internal static class NativeHandoffTests{
    internal static void Run(string fixture,Action<string,bool> check){
        string root=Path.Combine(fixture,"handoff-owned"),id="b7dde0511df64f14bd3d927a308ff6c02";Directory.CreateDirectory(root);var empty=JsonSerializer.SerializeToElement(new{});
        string original=Path.Combine(root,"原始附件.txt");File.WriteAllText(original,"original-unchanged");
        Node[] flow=[new("text","前面的说明 "),new("file",Path:original,Name:"原始附件.txt",Extension:"TXT",Bytes:18),new("text"," 后面的说明")];
        var settings=NativeHandoffStorage.Write(JsonSerializer.SerializeToElement(new{future="keep"}),flow,root,id);var read=NativeHandoffStorage.Read(settings,root,id);
        check("handoff ordered flow retains text attachment text",read.Skipped==0&&read.Nodes.Select(n=>n.Type).SequenceEqual(["text","file","text"])&&read.Nodes[2].Text==" 后面的说明");
        check("handoff unknown top-level setting preserved",settings.GetProperty("future").GetString()=="keep");
        check("original attachment never becomes owned storage",!NativeHandoffStorage.Owned(root,id,original)&&File.ReadAllText(original)=="original-unchanged");
        string text=new('测',1000);var large=NativeHandoffStorage.Write(empty,[new("text",text)],root,id);var external=NativeHandoffStorage.Read(large,root,id).Nodes.Single();
        check("UTF-8 size rather than character count externalizes long text",external.Reference.Length>0&&external.Text.Length==200);
        check("external text roundtrip is complete",NativeHandoffStorage.FullText(external,root,id)==text);
        check("references cannot cross card instances",NativeHandoffStorage.Read(large,root,"other-card").Skipped==1);
        check("network device ADS and relative attachments rejected",!NativeHandoffStorage.LocalFile(@"\\host\file")&&!NativeHandoffStorage.LocalFile(@"\\?\C:\file")&&!NativeHandoffStorage.LocalFile(@"C:\file:stream")&&!NativeHandoffStorage.LocalFile("relative.txt"));
        bool escaped=false;try{NativeHandoffStorage.ReferencePath(root,id,@"handoff\..\outside.txt");}catch(InvalidDataException){escaped=true;}check("handoff resource traversal refused",escaped);
        bool oversized=false;try{NativeHandoffStorage.Write(empty,[new("text",new string('x',NativeHandoffStorage.TextLimit+1))],root,id);}catch(LayoutEditException){oversized=true;}check("oversized text refuses write rather than truncating",oversized&&NativeHandoffStorage.FullText(external,root,id)==text);
        var packet=NativeHandoffClipboard.Prepare(read.Nodes,root,id,false);
        check("clipboard text includes full original file path in flow order",packet.Text.StartsWith("前面的说明 ",StringComparison.Ordinal)&&packet.Text.Contains("\""+original+"\"",StringComparison.Ordinal)&&packet.Text.EndsWith(" 后面的说明",StringComparison.Ordinal)&&packet.Files.SequenceEqual([original]));
        byte[] html=Encoding.UTF8.GetBytes(packet.Html);int Offset(string key)=>int.Parse(packet.Html.Split("\r\n").Single(x=>x.StartsWith(key+":",StringComparison.Ordinal)).Split(':')[1]);int start=Offset("StartFragment"),end=Offset("EndFragment");
        check("CF_HTML offsets count UTF-8 bytes for Chinese text",Encoding.UTF8.GetString(html[start..end]).StartsWith("前面的说明 ",StringComparison.Ordinal)&&Encoding.UTF8.GetString(html[..start]).EndsWith("<!--StartFragment-->",StringComparison.Ordinal));
        var safe=NativeHandoffClipboard.Prepare([new("text","<script>不执行</script>")],root,id,false);check("rich clipboard escapes user markup",safe.Html.Contains("&lt;script&gt;",StringComparison.Ordinal)&&!safe.Html.Contains("<script>",StringComparison.Ordinal));
        byte[] drop=NativeHandoffClipboard.FileDropBytes([original]);check("file drop uses wide DROPFILES and double null termination",BitConverter.ToInt32(drop,0)==20&&BitConverter.ToInt32(drop,16)==1&&Encoding.Unicode.GetString(drop[20..])==original+"\0\0");
        var line=new Run("abcd");var paragraph=new Paragraph(line);var document=new FlowDocument(paragraph);var attachment=new InlineUIContainer(new Border{Tag=flow[1]},line.ContentStart.GetPositionAtOffset(2)!);var nodes=NativeHandoffCards.ReadDocument(document);
        check("rich editor inserts attachment at caret rather than before whole run",nodes.Select(n=>n.Type).SequenceEqual(["text","file","text"])&&nodes[0].Text=="ab"&&nodes[2].Text=="cd");
        string fake=Path.Combine(NativeHandoffStorage.DirectoryFor(root,id),"text-original.txt");File.WriteAllText(fake,"user-owned");check("unrecognized names are never eligible for recycling",!NativeHandoffStorage.Owned(root,id,fake));
        check("empty card remains empty with no demo content",NativeHandoffStorage.Read(empty,root,id).Nodes.Length==0);
    }
}
