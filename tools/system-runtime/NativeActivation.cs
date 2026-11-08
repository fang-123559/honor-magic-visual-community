using System;using System.Runtime.InteropServices;
public static class MagicNativeActivation {
 [ComImport,Guid("2e941141-7f97-4756-ba1d-9decde894a3d"),InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]interface Activation {[PreserveSig]int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)]string app,[MarshalAs(UnmanagedType.LPWStr)]string args,uint options,out uint pid);[PreserveSig]int ActivateForFile(IntPtr a,IntPtr b,IntPtr c,out uint p);[PreserveSig]int ActivateForProtocol(IntPtr a,IntPtr b,out uint p);}
 // Loaded by Windows PowerShell 5.1 Add-Type; keep C# 5-compatible syntax.
 public static uint Start(string app,string args){var manager=(Activation)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("45ba127d-10a8-46ea-8ab7-56ea9078943c")));try{uint pid;Marshal.ThrowExceptionForHR(manager.ActivateApplication(app,args,0,out pid));return pid;}finally{Marshal.ReleaseComObject(manager);}}
}
