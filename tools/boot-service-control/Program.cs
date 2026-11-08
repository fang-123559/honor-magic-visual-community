using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

// One-shot client for an existing fixed signed service. No helper service,
// elevation, arbitrary command, task action replacement or UAC manipulation.
internal static class Program
{
    private const string ServiceHash="9107271073c0499142852d636b4fba57118fee3acc7bbe5b15b6e5dcceb66a24";
    static async Task<int> Main(string[] args)
    {
        try
        {
            if(args.Length!=1||args[0] is not ("--disable-original-logon" or "--restore-original-logon" or "--probe"))throw new ArgumentException("Unsupported mode");
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var self=Process.GetCurrentProcess();
            using var pipe=new NamedPipeClientStream(".","seelen-ui-service-"+self.SessionId,PipeDirection.InOut,PipeOptions.Asynchronous,TokenImpersonationLevel.Identification);
            await pipe.ConnectAsync(deadline.Token);
            if(!GetNamedPipeServerProcessId(pipe.SafePipeHandle.DangerousGetHandle(),out uint pid))throw new Win32Exception();
            nint handle=OpenProcess(0x1000,false,pid);if(handle==0)throw new Win32Exception();
            string expected=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),@"WindowsApps\Seelen.SeelenUI_2.8.9.0_x64__p6yyn03m1894e\slu-service.exe");
            try
            {
                var path=new StringBuilder(32768);uint length=(uint)path.Capacity;
                if(!QueryFullProcessImageName(handle,0,path,ref length)||!string.Equals(path.ToString(),expected,StringComparison.OrdinalIgnoreCase))throw new IOException("Unexpected service peer");
                using var file=File.OpenRead(expected);if(!Convert.ToHexString(SHA256.HashData(file)).Equals(ServiceHash,StringComparison.OrdinalIgnoreCase))throw new IOException("Signed service baseline differs");
            }
            finally{CloseHandle(handle);}
            bool enabled=args[0]=="--restore-original-logon";
            byte[] bytes=args[0]=="--probe"?[]:JsonSerializer.SerializeToUtf8Bytes(new{SetStartup=enabled});
            await pipe.WriteAsync(bytes,deadline.Token);await pipe.WriteAsync(new byte[]{0x17},deadline.Token);await pipe.FlushAsync(deadline.Token);
            using var response=new MemoryStream();byte[] one=new byte[1];
            while(response.Length<4096)
            {
                if(await pipe.ReadAsync(one,deadline.Token)!=1)throw new EndOfStreamException();
                if(one[0]==0x17)
                {
                    if(Encoding.UTF8.GetString(response.ToArray())!="\"Success\"")throw new IOException("Service action rejected: "+Encoding.UTF8.GetString(response.ToArray()));
                    Console.WriteLine(JsonSerializer.Serialize(new{success=true,mode=args[0],servicePid=pid,serviceSignedBaselineVerified=true}));return 0;
                }
                response.WriteByte(one[0]);
            }
            throw new IOException("Service response exceeded limit");
        }
        catch(Exception error){Console.Error.WriteLine(error.Message);return 1;}
    }
    [DllImport("kernel32.dll",SetLastError=true)]static extern bool GetNamedPipeServerProcessId(nint pipe,out uint pid);
    [DllImport("kernel32.dll",SetLastError=true)]static extern nint OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,SetLastError=true)]static extern bool QueryFullProcessImageName(nint process,uint flags,StringBuilder name,ref uint size);
    [DllImport("kernel32.dll")]static extern bool CloseHandle(nint handle);
}
