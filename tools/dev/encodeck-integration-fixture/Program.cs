using System.IO.Pipes;
using System.Text;
string directory=AppContext.BaseDirectory;
File.AppendAllText(Path.Combine(directory,"launches.log"),string.Join('|',args)+"\n");
string name=File.ReadAllText(Path.Combine(directory,"pipe-name.txt"));
if(File.Exists(Path.Combine(directory,"no-pipe.txt"))){await Task.Delay(15000);return;}
for(int i=0;i<50;i++){
 await using var pipe=new NamedPipeServerStream(name,PipeDirection.In,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
 using var deadline=new CancellationTokenSource(15000);await pipe.WaitForConnectionAsync(deadline.Token);
 using var input=new StreamReader(pipe,Encoding.UTF8);string message=await input.ReadToEndAsync(deadline.Token);
 File.AppendAllText(Path.Combine(directory,"commands.log"),message.Replace('\n','|')+"\n");
 if(message=="--exit"&&!File.Exists(Path.Combine(directory,"ignore-exit.txt")))return;
}
